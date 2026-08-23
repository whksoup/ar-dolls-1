using AprilTag;
using Meta.XR;
using UnityEngine;

using TagFamily = AprilTag.Interop.TagFamily;
using UnityPose = UnityEngine.Pose;

public sealed class MRUKAprilTagTracker : MonoBehaviour
{
    [Header("References")]
    [SerializeField]
    private PassthroughCameraAccess cameraAccess;

    [SerializeField]
    private Transform trackedObject;

    [Header("Tag")]
    [SerializeField]
    private TagFamily tagFamily = TagFamily.Tag36h11;

    [SerializeField]
    private int targetTagId;

    [Tooltip("Measured outer edge of the black tag square, in metres.")]
    [SerializeField]
    private float tagSizeMetres = 0.12f;

    [Header("Detection")]
    [SerializeField]
    private float detectionRate = 12f;

    [SerializeField, Range(1, 4)]
    private int decimation = 2;

    [Header("Coordinate correction")]
    [Tooltip("Fixed rotation between the detector's tag axes and the digital tag.")]
    [SerializeField]
    private Vector3 tagToObjectEuler;

    private TagDetector detector;
    private Color32[] pixels;

    private int detectorWidth;
    private int detectorHeight;
    private float nextDetectionTime;

    private void Update()
    {
        if (cameraAccess == null ||
            trackedObject == null ||
            !cameraAccess.IsPlaying ||
            Time.unscaledTime < nextDetectionTime)
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

        // Cache the physical camera pose associated with this frame.
        UnityPose cameraPose = cameraAccess.GetCameraPose();

        // CPU readback. This is why detection is throttled.
        var colors = cameraAccess.GetColors();

        if (!colors.IsCreated ||
            colors.Length != pixels.Length)
        {
            return;
        }

        colors.CopyTo(pixels);

        // TagDetector expects vertical FOV in radians.
        Ray bottomRay = cameraAccess.ViewportPointToRay(
            new Vector2(0.5f, 0f)
        );

        Ray topRay = cameraAccess.ViewportPointToRay(
            new Vector2(0.5f, 1f)
        );

        float verticalFovRadians =
            Vector3.Angle(
                bottomRay.direction,
                topRay.direction
            ) * Mathf.Deg2Rad;

        // Color32[] implicitly converts to ReadOnlySpan<Color32>.
        detector.ProcessImage(
            pixels,
            verticalFovRadians,
            tagSizeMetres
        );

        foreach (TagPose tag in detector.DetectedTags)
        {
            if (tag.ID != targetTagId)
                continue;

            Vector3 worldPosition =
                cameraPose.position +
                cameraPose.rotation * tag.Position;

            Quaternion tagToObjectRotation =
                Quaternion.Euler(tagToObjectEuler);

            Quaternion worldRotation =
                cameraPose.rotation *
                tag.Rotation *
                tagToObjectRotation;

            trackedObject.SetPositionAndRotation(
                worldPosition,
                worldRotation
            );

            break;
        }

        nextDetectionTime =
            Time.unscaledTime +
            1f / Mathf.Max(1f, detectionRate);
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