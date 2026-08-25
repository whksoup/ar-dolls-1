using System;
using UnityEngine;

using UnityPose = UnityEngine.Pose;

/// <summary>
/// Aligns this transform (and everything parented under it) so that a nominated
/// child — the ReferenceTag — lands exactly on the physical AprilTag.
///
/// Authoring workflow:
///   1. Put your content under this GameObject.
///   2. Add a child quad/empty for the tag and assign it to <see cref="referenceTag"/>.
///   3. Pose that child the way the real tag will be mounted (slanted, tilted,
///      on a wall, whatever). Your content does not move while you do this.
///   4. Optionally hit "Bake Reference Pose" so the reference can be hidden or
///      stripped at runtime.
///
/// The authored reference pose is treated as identity: whatever orientation you
/// gave it becomes "rotation zero", and the runtime solve absorbs the offset.
/// </summary>
[DisallowMultipleComponent]
public sealed class AprilTagAnchor : MonoBehaviour
{
    [Header("Source")]
    [Tooltip("Any component implementing ITagPoseSource: a single-camera tracker, or a stereo fusion node.")]
    [SerializeField]
    private MonoBehaviour poseSourceBehaviour;

    [SerializeField]
    private int tagId;

    [Header("Reference")]
    [Tooltip("Child transform posed as the physical tag is mounted.")]
    [SerializeField]
    private Transform referenceTag;

    [Tooltip(
        "Use the baked pose instead of reading the reference transform live. " +
        "Bake if you intend to disable, hide or delete the reference at runtime."
    )]
    [SerializeField]
    private bool useBakedReference;

    [SerializeField]
    private UnityPose bakedReference = UnityPose.identity;

    [Header("Smoothing")]
    [Tooltip("Seconds to converge ~63% of a positional correction.")]
    [SerializeField]
    private float positionSmoothTime = 0.12f;

    [Tooltip("Seconds to converge ~63% of a rotational correction.")]
    [SerializeField]
    private float rotationSmoothTime = 0.12f;

    [Tooltip("Corrections smaller than this are ignored, killing static jitter.")]
    [SerializeField]
    private float positionDeadband = 0.002f;

    [SerializeField]
    private float rotationDeadband = 0.25f;

    [Header("Outlier rejection")]
    [Tooltip("Single detections that jump further than this are discarded.")]
    [SerializeField]
    private float positionJumpLimit = 0.35f;

    [SerializeField]
    private float rotationJumpLimit = 40f;

    [Tooltip(
        "How many consecutive out-of-limit detections before we believe them " +
        "and snap. Covers the case where the tag genuinely was moved."
    )]
    [SerializeField, Min(1)]
    private int outliersBeforeAccept = 4;

    [Header("Loss")]
    [Tooltip("Seconds without a detection before the anchor is considered lost. It freezes in place.")]
    [SerializeField]
    private float lostTimeout = 0.75f;

    public event Action Acquired;
    public event Action Lost;

    private bool hasTarget;
    private UnityPose target;
    private float lastObservationTime;
    private int outlierCount;

    public bool IsTracking => hasTarget &&
        Time.unscaledTime - lastObservationTime <= lostTimeout;

    private ITagPoseSource poseSource;

    private void OnEnable()
    {
        poseSource = poseSourceBehaviour as ITagPoseSource;

        if (poseSource == null)
        {
            Debug.LogError(
                poseSourceBehaviour == null
                    ? "No pose source assigned."
                    : $"{poseSourceBehaviour.GetType().Name} does not implement ITagPoseSource.",
                this
            );

            return;
        }

        poseSource.TagObserved += OnTagObserved;
    }

    private void OnDisable()
    {
        if (poseSource != null)
            poseSource.TagObserved -= OnTagObserved;

        poseSource = null;
    }

    /// <summary>Forget the current lock. The next detection snaps instantly.</summary>
    public void ResetTracking()
    {
        hasTarget = false;
        outlierCount = 0;
    }

    private void OnTagObserved(TagObservation observation)
    {
        if (observation.Id != tagId)
            return;

        UnityPose candidate = SolveAnchorPose(observation.WorldPose);
        bool wasTracking = IsTracking;

        if (hasTarget)
        {
            float positionDelta =
                Vector3.Distance(candidate.position, target.position);

            float rotationDelta =
                Quaternion.Angle(candidate.rotation, target.rotation);

            if (positionDelta > positionJumpLimit ||
                rotationDelta > rotationJumpLimit)
            {
                outlierCount++;

                if (outlierCount < outliersBeforeAccept)
                    return;

                // Believed: the tag really did move. Snap rather than sweep.
                outlierCount = 0;
                target = candidate;
                lastObservationTime = observation.PublishTime;
                ApplyImmediate();
                return;
            }

            outlierCount = 0;

            if (positionDelta < positionDeadband &&
                rotationDelta < rotationDeadband)
            {
                // Within noise. Keep the old target, but stay "alive".
                lastObservationTime = observation.PublishTime;
                return;
            }

            target = candidate;
            lastObservationTime = observation.PublishTime;
        }
        else
        {
            target = candidate;
            lastObservationTime = observation.PublishTime;
            hasTarget = true;
            outlierCount = 0;
            ApplyImmediate();
        }

        if (!wasTracking)
            Acquired?.Invoke();
    }

    private void LateUpdate()
    {
        if (!hasTarget)
            return;

        if (Time.unscaledTime - lastObservationTime > lostTimeout)
        {
            if (wasTrackingLastFrame)
            {
                wasTrackingLastFrame = false;
                Lost?.Invoke();
            }

            // Freeze at the last good pose.
            return;
        }

        wasTrackingLastFrame = true;

        float deltaTime = Time.unscaledDeltaTime;

        float positionBlend = ExponentialBlend(deltaTime, positionSmoothTime);
        float rotationBlend = ExponentialBlend(deltaTime, rotationSmoothTime);

        transform.SetPositionAndRotation(
            Vector3.Lerp(
                transform.position,
                target.position,
                positionBlend
            ),
            Quaternion.Slerp(
                transform.rotation,
                target.rotation,
                rotationBlend
            )
        );
    }

    private bool wasTrackingLastFrame;

    private void ApplyImmediate()
    {
        transform.SetPositionAndRotation(
            target.position,
            target.rotation
        );
    }

    private static float ExponentialBlend(float deltaTime, float smoothTime)
    {
        if (smoothTime <= 0f)
            return 1f;

        return 1f - Mathf.Exp(-deltaTime / smoothTime);
    }

    /// <summary>
    /// Given where the physical tag is, work out where this transform must sit
    /// so the reference child coincides with it:  anchor = detected * refLocal^-1
    /// </summary>
    private UnityPose SolveAnchorPose(UnityPose detected)
    {
        UnityPose reference = GetReferenceLocalPose();

        Quaternion anchorRotation =
            detected.rotation * Quaternion.Inverse(reference.rotation);

        Vector3 anchorPosition =
            detected.position - anchorRotation * reference.position;

        return new UnityPose(anchorPosition, anchorRotation);
    }

    /// <summary>Reference pose expressed in this transform's local space.</summary>
    private UnityPose GetReferenceLocalPose()
    {
        if (useBakedReference || referenceTag == null)
            return bakedReference;

        return new UnityPose(
            transform.InverseTransformPoint(referenceTag.position),
            Quaternion.Inverse(transform.rotation) * referenceTag.rotation
        );
    }

    [ContextMenu("Bake Reference Pose")]
    private void BakeReferencePose()
    {
        if (referenceTag == null)
        {
            Debug.LogWarning(
                "No reference tag assigned; nothing to bake.",
                this
            );

            return;
        }

        bakedReference = GetReferenceLocalPoseFromTransform();
        useBakedReference = true;

#if UNITY_EDITOR
        UnityEditor.EditorUtility.SetDirty(this);
#endif
    }

    private UnityPose GetReferenceLocalPoseFromTransform()
    {
        return new UnityPose(
            transform.InverseTransformPoint(referenceTag.position),
            Quaternion.Inverse(transform.rotation) * referenceTag.rotation
        );
    }

    private void OnDrawGizmosSelected()
    {
        UnityPose reference = GetReferenceLocalPose();

        Vector3 worldPosition = transform.TransformPoint(reference.position);
        Quaternion worldRotation = transform.rotation * reference.rotation;

        Gizmos.color = Color.cyan;
        Gizmos.DrawLine(transform.position, worldPosition);

        Gizmos.matrix = Matrix4x4.TRS(
            worldPosition,
            worldRotation,
            Vector3.one
        );

        Gizmos.DrawWireCube(Vector3.zero, new Vector3(0.12f, 0.12f, 0.001f));
        Gizmos.color = Color.blue;
        Gizmos.DrawLine(Vector3.zero, Vector3.forward * 0.06f);
    }
}