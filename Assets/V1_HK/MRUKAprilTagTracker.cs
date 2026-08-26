using System;
using System.Collections.Generic;
using AprilTag;
using Meta.XR;
using UnityEngine;

using TagFamily = AprilTag.Interop.TagFamily;
using UnityPose = UnityEngine.Pose;

/// <summary>
/// Detects AprilTags in one passthrough camera feed and publishes their pose.
///
/// One instance per camera. To run stereo, place two of these, each pointing at
/// a <see cref="PassthroughCameraAccess"/> configured for a different
/// CameraPosition, and feed both into a fusion node that implements
/// <see cref="ITagPoseSource"/>.
///
/// Multi-tag notes: detection cost is per *frame*, not per tag — the readback,
/// the memcpy and ProcessImage's segmentation pass all run over the whole image
/// regardless of how many tags are in it. The marginal cost of an extra tag is
/// one homography plus pose refinement inside the detector, and the few vector
/// ops below. The knob that actually costs you is <see cref="decimation"/>.
/// </summary>
public sealed class MRUKAprilTagTracker : MonoBehaviour, ITagPoseSource
{
    public enum FovDerivation
    {
        /// <summary>
        /// Vertical FOV from the sensor focal length and crop region. Matches
        /// the centred-pinhole model the detector assumes internally.
        /// </summary>
        IntrinsicFocalLength,

        /// <summary>
        /// Angle between the viewport rays at the top and bottom of the image.
        /// Includes principal-point asymmetry, which the detector cannot model,
        /// so it back-derives a slightly inflated focal length. Kept for A/B.
        /// </summary>
        ViewportRayAngle
    }

    /// <summary>
    /// Physical size of one specific tag id.
    ///
    /// The detector is handed a single nominal size for the whole image, and
    /// camera-space position comes back exactly linear in that size. So rather
    /// than running the detector once per size class, we detect once at the
    /// nominal size and rescale each detection by SizeMetres / nominal. One
    /// multiply per tag; rotation is unaffected.
    /// </summary>
    [Serializable]
    public struct TagProfile
    {
        [Tooltip("AprilTag id this profile applies to.")]
        public int Id;

        [Tooltip("Measured outer edge of the black square for this tag, in metres.")]
        public float SizeMetres;
    }

    /// <inheritdoc />
    public event Action<TagObservation> TagObserved;

    [Header("References")]
    [SerializeField]
    private PassthroughCameraAccess cameraAccess;

    [Tooltip("0 = left, 1 = right. Stamped onto observations so a fusion stage can tell them apart.")]
    [SerializeField]
    private int sourceId;

    [Header("Tag")]
    [SerializeField]
    private TagFamily tagFamily = TagFamily.Tag36h11;

    [Tooltip(
        "Nominal tag size handed to the detector, and the fallback for any id " +
        "without a profile. Measured outer edge of the black tag square, in " +
        "metres. Measure the printed article with calipers — printer scaling of " +
        "a few percent is directly a few percent of range error."
    )]
    [SerializeField]
    private float tagSizeMetres = 0.12f;

    [Tooltip(
        "Per-id overrides for tags that are not the nominal size. Detections " +
        "for ids listed here are rescaled and stamped as size-calibrated, which " +
        "is what lets a downstream stage trust their range."
    )]
    [SerializeField]
    private TagProfile[] tagProfiles = Array.Empty<TagProfile>();

    [Tooltip(
        "Treat an id with no profile as size-calibrated at the nominal size. " +
        "On is correct when every tag you use is the nominal size. Off is the " +
        "safe setting for a mixed rig: unprofiled ids still track, but no " +
        "downstream stage will learn a range correction from them."
    )]
    [SerializeField]
    private bool unprofiledIdsAreCalibrated = true;

    [Header("Detection")]
    [SerializeField]
    private float detectionRate = 12f;

    [SerializeField, Range(1, 4)]
    private int decimation = 2;

    [Tooltip("Skip frames where the camera texture has not been refreshed. Avoids re-detecting an identical image.")]
    [SerializeField]
    private bool requireFreshFrame = true;

    [Header("Optics")]
    [SerializeField]
    private FovDerivation fovDerivation = FovDerivation.IntrinsicFocalLength;

    [Tooltip(
        "Multiplies the camera-space tag position. Range is linear in both the " +
        "focal length and the tag size, so one scalar absorbs the residual of " +
        "both. 1 = uncalibrated. Set via CalibrateFromLastDetection()."
    )]
    [SerializeField]
    private float rangeCalibrationScale = 1f;

    [Header("Coordinate correction")]
    [Tooltip(
        "Fixed rotation between the detector's tag axes and Unity's. A one-time " +
        "constant for the detector/family (usually identity or a 180 flip) — it " +
        "is NOT where you compensate for how a tag is mounted. Use the " +
        "ReferenceTag transform on AprilTagAnchor for that."
    )]
    [SerializeField]
    private Vector3 detectorFrameEuler;

    [Header("Calibration aid")]
    [Tooltip("True distance from camera to tag origin, in metres, for the calibration context menu.")]
    [SerializeField]
    private float knownCalibrationDistance = 1f;

    [Tooltip(
        "Which id to calibrate against. -1 uses whichever tag was detected most " +
        "recently, which is ambiguous when several are in view."
    )]
    [SerializeField]
    private int calibrationTagId = -1;

    [Tooltip("Log the raw (uncalibrated) range of every detection.")]
    [SerializeField]
    private bool logRange;

    private TagDetector detector;
    private Color32[] pixels;

    private int detectorWidth;
    private int detectorHeight;
    private float nextDetectionTime;

    // Raw range is per-tag now: with several tags in view, "the last detection"
    // is whichever the detector happened to return last, which is arbitrary.
    private readonly Dictionary<int, float> lastRawRangeById = new Dictionary<int, float>();
    private float lastRawRange;
    private int lastDetectedId = -1;

    public bool HasDetector => detector != null;

    /// <summary>Uncalibrated range of the most recent detection of any tag, in metres.</summary>
    public float LastRawRange => lastRawRange;

    /// <summary>Id of the most recent detection, or -1.</summary>
    public int LastDetectedId => lastDetectedId;

    /// <summary>Uncalibrated range of the most recent detection of a specific tag. 0 if never seen.</summary>
    public float GetLastRawRange(int id) =>
        lastRawRangeById.TryGetValue(id, out float range) ? range : 0f;

    /// <summary>Vertical FOV currently being handed to the detector, in radians. 0 if unavailable.</summary>
    public float CurrentVerticalFov =>
        cameraAccess != null && cameraAccess.IsPlaying
            ? ComputeVerticalFovRadians()
            : 0f;

    /// <summary>Physical size assumed for an id, in metres.</summary>
    public float SizeForId(int id)
    {
        if (tagProfiles != null)
        {
            for (int index = 0; index < tagProfiles.Length; index++)
            {
                if (tagProfiles[index].Id == id && tagProfiles[index].SizeMetres > 0f)
                    return tagProfiles[index].SizeMetres;
            }
        }

        return tagSizeMetres;
    }

    /// <summary>True when this id has an explicit profile.</summary>
    public bool HasProfile(int id)
    {
        if (tagProfiles == null)
            return false;

        for (int index = 0; index < tagProfiles.Length; index++)
        {
            if (tagProfiles[index].Id == id && tagProfiles[index].SizeMetres > 0f)
                return true;
        }

        return false;
    }

    /// <summary>Multiplier taking a detection made at the nominal size to this id's true size.</summary>
    private float SizeScaleForId(int id)
    {
        if (tagSizeMetres <= 0.0001f)
            return 1f;

        return SizeForId(id) / tagSizeMetres;
    }

    private void Update()
    {
        if (cameraAccess == null ||
            !cameraAccess.IsPlaying ||
            Time.unscaledTime < nextDetectionTime)
        {
            return;
        }

        if (requireFreshFrame && !cameraAccess.IsUpdatedThisFrame)
        {
            return;
        }

        Texture cameraTexture = cameraAccess.GetTexture();

        if (cameraTexture == null ||
            cameraTexture.width < 32 ||
            cameraTexture.height < 32)
        {
            return;
        }

        EnsureDetector(
            cameraTexture.width,
            cameraTexture.height
        );

        // Cache the pose and timestamp associated with THIS frame, before the
        // readback stalls us. GetCameraPose() already resolves the headset pose
        // at the image timestamp, so this is capture-time correct.
        UnityPose cameraPose = cameraAccess.GetCameraPose();
        DateTime captureTime = cameraAccess.Timestamp;

        // Blocking CPU readback. This is why detection is throttled. If this
        // becomes the bottleneck, replace with a non-blocking AsyncGPUReadback
        // and carry the pose/timestamp forward to the completion callback.
        var colors = cameraAccess.GetColors();

        if (!colors.IsCreated ||
            colors.Length != pixels.Length)
        {
            return;
        }

        colors.CopyTo(pixels);

        float verticalFovRadians = ComputeVerticalFovRadians();

        if (verticalFovRadians <= 0f)
        {
            return;
        }

        // Color32[] implicitly converts to ReadOnlySpan<Color32>.
        // One pass, all tags. Everything above this line is the fixed per-frame
        // cost; everything below is the (small) per-tag cost.
        detector.ProcessImage(
            pixels,
            verticalFovRadians,
            tagSizeMetres
        );

        Quaternion detectorFix = Quaternion.Euler(detectorFrameEuler);
        float now = Time.unscaledTime;
        float opticsScale = Mathf.Max(0.0001f, rangeCalibrationScale);

        foreach (TagPose tag in detector.DetectedTags)
        {
            float rawRange = tag.Position.magnitude;

            lastRawRange = rawRange;
            lastDetectedId = tag.ID;
            lastRawRangeById[tag.ID] = rawRange;

            // Two independent scalars, deliberately kept apart: one is a
            // property of the optics (shared by every tag this camera sees),
            // the other is a property of this particular printed tag.
            float sizeScale = SizeScaleForId(tag.ID);
            float scale = opticsScale * sizeScale;

            bool sizeCalibrated = HasProfile(tag.ID) || unprofiledIdsAreCalibrated;

            if (logRange)
            {
                Debug.Log(
                    $"[AprilTag {tag.ID}] raw range {rawRange:F4} m, " +
                    $"size {SizeForId(tag.ID):F4} m (x{sizeScale:F3}), " +
                    $"calibrated {rawRange * scale:F4} m" +
                    (sizeCalibrated ? string.Empty : " [size assumed]"),
                    this
                );
            }

            // Scaling the whole camera-space vector scales range along a fixed
            // bearing, which is exactly what a focal/tag-size error does.
            Vector3 localPosition = tag.Position * scale;
            Quaternion localRotation = tag.Rotation * detectorFix;

            UnityPose cameraLocalPose = new UnityPose(
                localPosition,
                localRotation
            );

            UnityPose worldPose = new UnityPose(
                cameraPose.position + cameraPose.rotation * localPosition,
                cameraPose.rotation * localRotation
            );

            TagObserved?.Invoke(
                new TagObservation(
                    tag.ID,
                    worldPose,
                    cameraLocalPose,
                    cameraPose,
                    captureTime,
                    now,
                    sourceId,
                    sizeCalibrated
                )
            );
        }

        nextDetectionTime =
            now + 1f / Mathf.Max(1f, detectionRate);
    }

    /// <summary>
    /// Vertical FOV in radians, matched to the detector's centred-pinhole model.
    ///
    /// The delivered image is a centre crop of the sensor, rescaled to
    /// CurrentResolution. Focal length is expressed in sensor pixels, so the
    /// angular height is set by the crop height and fy — the rescale to
    /// CurrentResolution cancels out.
    /// </summary>
    private float ComputeVerticalFovRadians()
    {
        if (fovDerivation == FovDerivation.ViewportRayAngle)
        {
            return ComputeVerticalFovFromRays();
        }

        var intrinsics = cameraAccess.Intrinsics;

        Vector2 sensorResolution = intrinsics.SensorResolution;
        Vector2 currentResolution = cameraAccess.CurrentResolution;

        if (sensorResolution.x <= 0f ||
            sensorResolution.y <= 0f ||
            intrinsics.FocalLength.y <= 0f)
        {
            // Intrinsics not populated yet; fall back rather than emit garbage.
            return ComputeVerticalFovFromRays();
        }

        Vector2 scaleFactor = new Vector2(
            currentResolution.x / sensorResolution.x,
            currentResolution.y / sensorResolution.y
        );

        scaleFactor /= Mathf.Max(scaleFactor.x, scaleFactor.y);

        float cropHeight = sensorResolution.y * scaleFactor.y;

        return 2f * Mathf.Atan(
            cropHeight / (2f * intrinsics.FocalLength.y)
        );
    }

    private float ComputeVerticalFovFromRays()
    {
        Ray bottomRay = cameraAccess.ViewportPointToRay(
            new Vector2(0.5f, 0f)
        );

        Ray topRay = cameraAccess.ViewportPointToRay(
            new Vector2(0.5f, 1f)
        );

        return Vector3.Angle(
            bottomRay.direction,
            topRay.direction
        ) * Mathf.Deg2Rad;
    }

    /// <summary>
    /// Sets <c>rangeCalibrationScale</c> so the chosen tag's last detection reads
    /// as <c>knownCalibrationDistance</c>. Hold the tag at a measured distance,
    /// let it detect, then invoke.
    ///
    /// The tag's own size profile is divided out first, so calibrating against a
    /// non-nominal tag still yields a pure optics correction.
    /// </summary>
    [ContextMenu("Calibrate From Last Detection")]
    public void CalibrateFromLastDetection()
    {
        int id = calibrationTagId >= 0 ? calibrationTagId : lastDetectedId;

        if (id < 0)
        {
            Debug.LogWarning("No detection to calibrate from.", this);
            return;
        }

        float rawRange = GetLastRawRange(id);

        if (rawRange <= 0.0001f)
        {
            Debug.LogWarning($"No detection of tag {id} to calibrate from.", this);
            return;
        }

        float sizeScale = SizeScaleForId(id);
        float sizedRange = rawRange * sizeScale;

        rangeCalibrationScale = knownCalibrationDistance / sizedRange;

        Debug.Log(
            $"Range calibration set to {rangeCalibrationScale:F4} from tag {id} " +
            $"({rawRange:F4} m raw, {sizedRange:F4} m after size profile -> " +
            $"{knownCalibrationDistance:F4} m true, " +
            $"{(rangeCalibrationScale - 1f) * 100f:F2}% correction).",
            this
        );

#if UNITY_EDITOR
        UnityEditor.EditorUtility.SetDirty(this);
#endif
    }

    [ContextMenu("Log Optics")]
    private void LogOptics()
    {
        if (cameraAccess == null || !cameraAccess.IsPlaying)
        {
            Debug.LogWarning("Camera not playing.", this);
            return;
        }

        var intrinsics = cameraAccess.Intrinsics;

        float fromIntrinsics = 2f * Mathf.Atan(
            intrinsics.SensorResolution.y / (2f * intrinsics.FocalLength.y)
        );

        Debug.Log(
            $"fy={intrinsics.FocalLength.y:F2} px, " +
            $"pp={intrinsics.PrincipalPoint}, " +
            $"sensor={intrinsics.SensorResolution}, " +
            $"current={cameraAccess.CurrentResolution}\n" +
            $"vFOV intrinsics (uncropped) = {fromIntrinsics * Mathf.Rad2Deg:F3} deg\n" +
            $"vFOV intrinsics (cropped)   = {ComputeVerticalFovRadians() * Mathf.Rad2Deg:F3} deg\n" +
            $"vFOV ray angle              = {ComputeVerticalFovFromRays() * Mathf.Rad2Deg:F3} deg",
            this
        );
    }

    [ContextMenu("Log Tag Profiles")]
    private void LogTagProfiles()
    {
        var builder = new System.Text.StringBuilder();

        builder.AppendLine($"Nominal size = {tagSizeMetres:F4} m (handed to the detector)");

        if (tagProfiles == null || tagProfiles.Length == 0)
        {
            builder.AppendLine("No per-id profiles; every tag is assumed nominal.");
        }
        else
        {
            foreach (TagProfile profile in tagProfiles)
            {
                builder.AppendLine(
                    $"  id {profile.Id}: {profile.SizeMetres:F4} m " +
                    $"(x{SizeScaleForId(profile.Id):F4})"
                );
            }
        }

        builder.Append(
            $"Unprofiled ids are {(unprofiledIdsAreCalibrated ? "" : "NOT ")}" +
            "treated as size-calibrated."
        );

        Debug.Log(builder.ToString(), this);
    }

    private void EnsureDetector(int width, int height)
    {
        if (detector != null &&
            detectorWidth == width &&
            detectorHeight == height)
        {
            return;
        }

        detector?.Dispose();

        detector = new TagDetector(
            width,
            height,
            tagFamily,
            decimation
        );

        detectorWidth = width;
        detectorHeight = height;
        pixels = new Color32[width * height];
    }

    private void OnDestroy()
    {
        detector?.Dispose();
        detector = null;
    }
}
