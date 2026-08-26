using System;
using System.Collections.Generic;
using UnityEngine;

using UnityPose = UnityEngine.Pose;

/// <summary>
/// Fuses two single-camera <see cref="ITagPoseSource"/> detectors into one
/// better source. Itself an <see cref="ITagPoseSource"/>, so it drops straight
/// into <c>AprilTagAnchor.poseSourceBehaviour</c> with no change to the anchor.
///
/// What it does, in order of value:
///
///   1. Discards both monocular range estimates and triangulates the tag origin
///      from the two bearings. Bearing is the well-conditioned quantity; range
///      from apparent size is not. This is the whole point of running stereo.
///
///   2. Uses the triangulated range as ground truth to continuously calibrate a
///      per-camera range correction, so that when one camera loses the tag the
///      surviving monocular observation is already correctly scaled and the
///      pose does not jump on the stereo -> mono handoff.
///
///   3. Compares the two rotation estimates. The planar pose ambiguity flip
///      rarely happens in both cameras at the same instant, so a large
///      disagreement is a reliable "this rotation is garbage" signal.
///
/// Emitted observations carry SourceId = <see cref="FusedSourceId"/> when the
/// pose was triangulated, and the originating camera's SourceId when it came
/// from the monocular fallback path.
///
/// Multi-tag: every gate, every piece of history and every diagnostic is keyed
/// by tag id. The per-tag cost of triangulation is a dozen dot products, so the
/// number of tags in view is not a meaningful compute concern here — the cost
/// lives upstream, in the detectors.
///
/// The one genuinely shared quantity is <see cref="rangeCorrection"/>, and that
/// is correct: it is a property of the camera's optics, not of any tag. It is
/// only learned from observations flagged <see cref="TagObservation.SizeCalibrated"/>,
/// because a tag of unknown size produces a range error indistinguishable from
/// a focal-length error, and folding one into the other would then corrupt
/// every other tag.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(100)] // after the trackers' Update, before any LateUpdate consumer
public sealed class StereoTagFusion : MonoBehaviour, ITagPoseSource
{
    /// <summary>SourceId stamped onto triangulated observations.</summary>
    public const int FusedSourceId = 2;

    /// <inheritdoc />
    public event Action<TagObservation> TagObserved;

    /// <summary>Everything the fusion stage remembers about one tag id.</summary>
    public struct TagState
    {
        public Quaternion TrustedRotation;
        public bool HasTrustedRotation;

        public float LastResidual;
        public float LastConvergenceAngle;
        public float LastRotationDisagreement;
        public bool IsStereo;
        public float LastEmitTime;
    }

    /// <summary>An observation waiting for its partner, plus when the wait started.</summary>
    private struct Pending
    {
        public TagObservation Observation;

        /// <summary>
        /// When this id first landed in this slot. The timeout is measured from
        /// here rather than from the observation's own PublishTime: a superseding
        /// sighting refreshes the observation but must not refresh the deadline,
        /// or a pair that keeps failing a gate at a detection rate faster than
        /// the timeout never retires, and the mono fallback silently starves.
        /// </summary>
        public float FirstSeenTime;
    }

    [Header("Sources")]
    [Tooltip("Tracker for the left camera. Any ITagPoseSource.")]
    [SerializeField]
    private MonoBehaviour leftSourceBehaviour;

    [Tooltip("Tracker for the right camera. Any ITagPoseSource.")]
    [SerializeField]
    private MonoBehaviour rightSourceBehaviour;

    [Header("Pairing")]
    [Tooltip(
        "Maximum capture-time difference between two observations for them to " +
        "be treated as simultaneous. The cameras are not guaranteed to be " +
        "genlocked; with a 64 mm baseline, skew is magnified about 15x into " +
        "range error, so keep this tight."
    )]
    [SerializeField]
    private float maxCaptureSkewMs = 8f;

    [Tooltip(
        "How long an unpaired observation waits for its partner before being " +
        "emitted through the monocular path. Lower = less latency when only " +
        "one camera can see the tag, but more missed pairings. Measured from " +
        "when the id first entered the queue, not from the newest sighting."
    )]
    [SerializeField]
    private float pairingTimeout = 0.05f;

    [Header("Triangulation gates")]
    [Tooltip(
        "Minimum angle between the two bearings. Below this the intersection " +
        "is ill-conditioned and range collapses. 0.3 deg is roughly 12 m with " +
        "the Quest's inter-camera baseline."
    )]
    [SerializeField]
    private float minConvergenceAngleDeg = 0.3f;

    [Tooltip(
        "Closest-approach distance between the two rays, allowed per metre of " +
        "range. The rays should very nearly intersect; a large residual means " +
        "a bad detection, bad extrinsics, or capture desync."
    )]
    [SerializeField]
    private float maxResidualPerMetre = 0.02f;

    [Tooltip(
        "Reject a pair when (head angular speed x capture skew) exceeds this. " +
        "This is the actual physical quantity that corrupts the baseline — " +
        "perfectly synced captures are safe at any head speed."
    )]
    [SerializeField]
    private float maxSyncRotationErrorDeg = 0.5f;

    [Header("Rotation")]
    [Tooltip(
        "Disagreement between the two cameras' rotation estimates above which " +
        "we assume a planar pose ambiguity flip. Expect a few degrees of " +
        "honest noise; a flip shows up as tens of degrees."
    )]
    [SerializeField]
    private float maxRotationDisagreementDeg = 12f;

    [Tooltip(
        "On disagreement, keep the last trusted rotation and still publish the " +
        "triangulated position. Position is unaffected by the ambiguity, so " +
        "this is usually better than dropping the frame entirely."
    )]
    [SerializeField]
    private bool holdRotationOnDisagreement = true;

    [Header("Monocular fallback")]
    [Tooltip("Publish single-camera observations when no pair is available.")]
    [SerializeField]
    private bool allowMonoFallback = true;

    [Tooltip("Learn a per-camera range correction from the triangulated range.")]
    [SerializeField]
    private bool autoCalibrateRange = true;

    [Tooltip(
        "Only learn from tags whose physical size is actually known. Leave on " +
        "for any rig with more than one tag size, or where some ids are " +
        "unprofiled — otherwise a tag-size error is learned as an optics error " +
        "and applied to every other tag."
    )]
    [SerializeField]
    private bool requireSizeCalibratedForLearning = true;

    [Tooltip("Blend weight per stereo sample. Small = slow, stable convergence.")]
    [SerializeField, Range(0.001f, 1f)]
    private float calibrationBlend = 0.03f;

    [Tooltip("Clamp on the learned correction. 0.25 = accept at most +/-25%.")]
    [SerializeField]
    private float maxCalibrationCorrection = 0.25f;

    [Header("State retention")]
    [Tooltip(
        "Drop a tag's remembered state after this long without a sighting. " +
        "Chiefly stops holdRotationOnDisagreement from resurrecting a rotation " +
        "that was trusted minutes ago and is now meaningless."
    )]
    [SerializeField]
    private float stateRetention = 10f;

    [Header("Diagnostics")]
    [SerializeField]
    private bool logFusion;

    private ITagPoseSource leftSource;
    private ITagPoseSource rightSource;

    private Action<TagObservation> leftHandler;
    private Action<TagObservation> rightHandler;

    // Pending observations awaiting a partner, keyed by tag id, one dict per slot.
    private readonly Dictionary<int, Pending>[] pending =
    {
        new Dictionary<int, Pending>(),
        new Dictionary<int, Pending>()
    };

    // Ids whose pair was already evaluated and rejected. Cleared the moment
    // either slot receives fresh data, so we never re-run TryFuse on inputs we
    // have already judged.
    private readonly HashSet<int> rejectedPairs = new HashSet<int>();

    // Everything remembered per tag: trusted rotation plus diagnostics.
    private readonly Dictionary<int, TagState> tagStates = new Dictionary<int, TagState>();

    private readonly float[] rangeCorrection = { 1f, 1f };
    private readonly bool[] hasRangeCorrection = { false, false };

    private readonly Quaternion[] lastCameraRotation = new Quaternion[2];
    private readonly float[] lastCameraRotationTime = new float[2];
    private readonly bool[] hasCameraRotation = { false, false };
    private readonly float[] headAngularSpeed = new float[2];

    private readonly List<int> scratchIds = new List<int>();
    private readonly List<int> scratchExpired = new List<int>();

    private int lastEmittedId = -1;
    private float nextPruneTime;

    /// <summary>Id of the most recent emission. Use the per-id accessors when several tags are live.</summary>
    public int LastEmittedId => lastEmittedId;

    /// <summary>Ray closest-approach distance of the last accepted pair for the last emitted tag, in metres.</summary>
    public float LastResidual => GetState(lastEmittedId).LastResidual;

    /// <summary>Angle between the two bearings on the last accepted pair for the last emitted tag, in degrees.</summary>
    public float LastConvergenceAngle => GetState(lastEmittedId).LastConvergenceAngle;

    /// <summary>Rotation disagreement on the last evaluated pair for the last emitted tag, in degrees.</summary>
    public float LastRotationDisagreement => GetState(lastEmittedId).LastRotationDisagreement;

    /// <summary>True while the last emission for the last emitted tag came from triangulation.</summary>
    public bool IsStereo => GetState(lastEmittedId).IsStereo;

    /// <summary>Per-tag equivalent of <see cref="IsStereo"/>. Prefer this when tracking several tags.</summary>
    public bool IsStereoFor(int id) => GetState(id).IsStereo;

    /// <summary>Everything remembered about one tag. Default-valued if the tag is unknown.</summary>
    public TagState GetState(int id) =>
        id >= 0 && tagStates.TryGetValue(id, out TagState state) ? state : default;

    /// <summary>Ids the fusion stage currently holds state for.</summary>
    public IEnumerable<int> TrackedIds => tagStates.Keys;

    /// <summary>Learned range correction for a camera slot. 0 = left, 1 = right.</summary>
    public float GetRangeCorrection(int slot) =>
        slot >= 0 && slot < 2 ? rangeCorrection[slot] : 1f;

    private void OnEnable()
    {
        leftSource = leftSourceBehaviour as ITagPoseSource;
        rightSource = rightSourceBehaviour as ITagPoseSource;

        if (leftSource == null || rightSource == null)
        {
            Debug.LogError(
                "StereoTagFusion needs two components implementing ITagPoseSource.",
                this
            );

            return;
        }

        if (ReferenceEquals(leftSource, rightSource))
        {
            Debug.LogError(
                "Left and right sources are the same object. Assign one tracker " +
                "per PassthroughCameraAccess, each with a different CameraPosition.",
                this
            );

            leftSource = null;
            rightSource = null;
            return;
        }

        leftHandler = observation => OnObserved(0, observation);
        rightHandler = observation => OnObserved(1, observation);

        leftSource.TagObserved += leftHandler;
        rightSource.TagObserved += rightHandler;
    }

    private void OnDisable()
    {
        if (leftSource != null)
            leftSource.TagObserved -= leftHandler;

        if (rightSource != null)
            rightSource.TagObserved -= rightHandler;

        leftSource = null;
        rightSource = null;

        pending[0].Clear();
        pending[1].Clear();
        rejectedPairs.Clear();
    }

    /// <summary>Forget the learned range corrections and all per-tag history.</summary>
    [ContextMenu("Reset Calibration")]
    public void ResetCalibration()
    {
        rangeCorrection[0] = 1f;
        rangeCorrection[1] = 1f;
        hasRangeCorrection[0] = false;
        hasRangeCorrection[1] = false;
        tagStates.Clear();
        lastEmittedId = -1;
    }

    private void OnObserved(int slot, TagObservation observation)
    {
        UpdateHeadAngularSpeed(slot, observation);

        // A newer sighting supersedes an older unpaired one for the same tag,
        // but does NOT reset the pairing deadline.
        if (pending[slot].TryGetValue(observation.Id, out Pending entry))
        {
            entry.Observation = observation;
        }
        else
        {
            entry = new Pending
            {
                Observation = observation,
                FirstSeenTime = observation.PublishTime
            };
        }

        pending[slot][observation.Id] = entry;

        // Fresh data for this id: any earlier verdict on its pair is stale.
        rejectedPairs.Remove(observation.Id);
    }

    private void UpdateHeadAngularSpeed(int slot, in TagObservation observation)
    {
        if (hasCameraRotation[slot])
        {
            float dt = observation.PublishTime - lastCameraRotationTime[slot];

            if (dt > 1e-4f)
            {
                headAngularSpeed[slot] = Quaternion.Angle(
                    lastCameraRotation[slot],
                    observation.CameraPose.rotation
                ) / dt;
            }
        }

        lastCameraRotation[slot] = observation.CameraPose.rotation;
        lastCameraRotationTime[slot] = observation.PublishTime;
        hasCameraRotation[slot] = true;
    }

    private void Update()
    {
        if (leftSource == null || rightSource == null)
            return;

        ProcessPairs();
        ProcessTimeouts();
        PruneStaleState();
    }

    private void ProcessPairs()
    {
        if (pending[0].Count == 0 || pending[1].Count == 0)
            return;

        scratchIds.Clear();

        foreach (int id in pending[0].Keys)
        {
            if (pending[1].ContainsKey(id) && !rejectedPairs.Contains(id))
                scratchIds.Add(id);
        }

        foreach (int id in scratchIds)
        {
            TagObservation a = pending[0][id].Observation;
            TagObservation b = pending[1][id].Observation;

            if (TryFuse(a, b, out UnityPose fused))
            {
                pending[0].Remove(id);
                pending[1].Remove(id);
                Emit(fused, a, FusedSourceId);
            }
            else
            {
                // Pair rejected. Remember that, so we do not re-evaluate the
                // same two observations every frame, and let both fall through
                // to the timeout path so the mono fallback still gets a chance.
                rejectedPairs.Add(id);
            }
        }
    }

    /// <summary>
    /// Retires observations that never found a partner (or whose pair was
    /// rejected) through the monocular path. Both slots are handled in one
    /// pass: if a rejected pair times out, exactly one mono observation is
    /// emitted rather than two competing ones from the same instant.
    /// </summary>
    private void ProcessTimeouts()
    {
        if (pending[0].Count == 0 && pending[1].Count == 0)
            return;

        float now = Time.unscaledTime;

        scratchIds.Clear();

        for (int slot = 0; slot < 2; slot++)
        {
            foreach (KeyValuePair<int, Pending> entry in pending[slot])
            {
                if (now - entry.Value.FirstSeenTime >= pairingTimeout &&
                    !scratchIds.Contains(entry.Key))
                {
                    scratchIds.Add(entry.Key);
                }
            }
        }

        foreach (int id in scratchIds)
        {
            bool hasLeft = pending[0].TryGetValue(id, out Pending leftEntry);
            bool hasRight = pending[1].TryGetValue(id, out Pending rightEntry);

            pending[0].Remove(id);
            pending[1].Remove(id);
            rejectedPairs.Remove(id);

            if (!allowMonoFallback || (!hasLeft && !hasRight))
                continue;

            TagObservation left = leftEntry.Observation;
            TagObservation right = rightEntry.Observation;

            // Both present means the pair was rejected by a gate. Prefer the
            // camera whose view is better conditioned for rotation.
            int slot;
            TagObservation chosen;

            if (hasLeft && hasRight)
            {
                bool preferLeft =
                    ConditioningTilt(left) >= ConditioningTilt(right);

                slot = preferLeft ? 0 : 1;
                chosen = preferLeft ? left : right;
            }
            else
            {
                slot = hasLeft ? 0 : 1;
                chosen = hasLeft ? left : right;
            }

            Emit(
                BuildMonoPose(chosen, slot),
                chosen,
                chosen.SourceId
            );
        }
    }

    /// <summary>
    /// Drops per-tag state for tags nobody has seen in a while. Bounded work,
    /// run about once a second.
    /// </summary>
    private void PruneStaleState()
    {
        if (tagStates.Count == 0 || Time.unscaledTime < nextPruneTime)
            return;

        nextPruneTime = Time.unscaledTime + 1f;

        if (stateRetention <= 0f)
            return;

        scratchExpired.Clear();

        foreach (KeyValuePair<int, TagState> entry in tagStates)
        {
            if (Time.unscaledTime - entry.Value.LastEmitTime > stateRetention)
                scratchExpired.Add(entry.Key);
        }

        foreach (int id in scratchExpired)
            tagStates.Remove(id);
    }

    /// <summary>
    /// Triangulates the tag origin from the two bearings and fuses the two
    /// rotation estimates. Returns false if any conditioning gate fails.
    /// </summary>
    private bool TryFuse(in TagObservation a, in TagObservation b, out UnityPose fused)
    {
        fused = default;

        // --- Gate: capture simultaneity ------------------------------------
        float skewSeconds = (float)Math.Abs(
            (a.CaptureTime - b.CaptureTime).TotalSeconds
        );

        if (skewSeconds * 1000f > maxCaptureSkewMs)
            return false;

        float worstHeadSpeed = Mathf.Max(headAngularSpeed[0], headAngularSpeed[1]);

        if (worstHeadSpeed * skewSeconds > maxSyncRotationErrorDeg)
            return false;

        // --- Triangulate ----------------------------------------------------
        Vector3 pA = a.CameraPose.position;
        Vector3 pB = b.CameraPose.position;
        Vector3 dA = a.WorldBearing;
        Vector3 dB = b.WorldBearing;

        if (dA.sqrMagnitude < 0.5f || dB.sqrMagnitude < 0.5f)
            return false; // degenerate bearing (tag at the camera origin)

        float convergence = Vector3.Angle(dA, dB);

        if (convergence < minConvergenceAngleDeg)
            return false;

        Vector3 r = pA - pB;
        float dot = Vector3.Dot(dA, dB);
        float denom = 1f - dot * dot;

        if (denom < 1e-9f)
            return false;

        float s = (dot * Vector3.Dot(dB, r) - Vector3.Dot(dA, r)) / denom;
        float t = (Vector3.Dot(dB, r) - dot * Vector3.Dot(dA, r)) / denom;

        if (s <= 0f || t <= 0f)
            return false; // solution sits behind a camera

        Vector3 closestA = pA + s * dA;
        Vector3 closestB = pB + t * dB;

        Vector3 position = 0.5f * (closestA + closestB);
        float residual = Vector3.Distance(closestA, closestB);
        float range = 0.5f * (s + t);

        if (range <= 0.01f)
            return false;

        if (residual / range > maxResidualPerMetre)
            return false;

        // --- Rotation -------------------------------------------------------
        float disagreement = Quaternion.Angle(
            a.WorldPose.rotation,
            b.WorldPose.rotation
        );

        TagState state = GetState(a.Id);
        state.LastRotationDisagreement = disagreement;
        tagStates[a.Id] = state;

        if (!TryResolveRotation(a, b, disagreement, out Quaternion rotation))
            return false;

        // --- Accept ---------------------------------------------------------
        state = GetState(a.Id);
        state.LastResidual = residual;
        state.LastConvergenceAngle = convergence;
        state.LastRotationDisagreement = disagreement;
        state.TrustedRotation = rotation;
        state.HasTrustedRotation = true;
        tagStates[a.Id] = state;

        if (autoCalibrateRange &&
            (!requireSizeCalibratedForLearning || (a.SizeCalibrated && b.SizeCalibrated)))
        {
            UpdateRangeCorrection(0, a, s);
            UpdateRangeCorrection(1, b, t);
        }

        if (logFusion)
        {
            Debug.Log(
                $"[StereoFusion {a.Id}] range {range:F4} m, " +
                $"residual {residual * 1000f:F1} mm, " +
                $"convergence {convergence:F2} deg, " +
                $"rot disagreement {disagreement:F1} deg, " +
                $"skew {skewSeconds * 1000f:F2} ms, " +
                $"corr L {rangeCorrection[0]:F4} R {rangeCorrection[1]:F4}" +
                (a.SizeCalibrated && b.SizeCalibrated ? string.Empty : ", size assumed"),
                this
            );
        }

        fused = new UnityPose(position, rotation);
        return true;
    }

    /// <summary>
    /// Blends the two rotation estimates, weighted by how far each camera is
    /// from the fronto-parallel view where the planar solve degenerates.
    /// Large disagreement is read as an ambiguity flip.
    /// </summary>
    private bool TryResolveRotation(
        in TagObservation a,
        in TagObservation b,
        float disagreement,
        out Quaternion rotation)
    {
        if (disagreement > maxRotationDisagreementDeg)
        {
            TagState state = GetState(a.Id);

            if (holdRotationOnDisagreement && state.HasTrustedRotation)
            {
                rotation = state.TrustedRotation;
                return true;
            }

            rotation = default;
            return false;
        }

        float weightA = Mathf.Max(1f, ConditioningTilt(a));
        float weightB = Mathf.Max(1f, ConditioningTilt(b));

        rotation = Quaternion.Slerp(
            a.WorldPose.rotation,
            b.WorldPose.rotation,
            weightB / (weightA + weightB)
        );

        return true;
    }

    /// <summary>
    /// Angle between the tag's plane and the viewing ray, folded into 0..90.
    /// Near 0 means fronto-parallel, which is where the rotation solve is worst.
    /// </summary>
    private static float ConditioningTilt(in TagObservation observation)
    {
        float tilt = Vector3.Angle(
            observation.CameraLocalPose.rotation * Vector3.forward,
            -observation.CameraLocalPose.position
        );

        return tilt > 90f ? 180f - tilt : tilt;
    }

    /// <summary>
    /// Nudges the per-camera range correction toward the ratio between the
    /// triangulated range and this camera's monocular range estimate.
    /// </summary>
    private void UpdateRangeCorrection(
        int slot,
        in TagObservation observation,
        float trueRange)
    {
        float observedRange = observation.Range;

        if (observedRange < 0.01f || trueRange < 0.01f)
            return;

        float ratio = trueRange / observedRange;

        float minRatio = 1f - maxCalibrationCorrection;
        float maxRatio = 1f + maxCalibrationCorrection;

        if (ratio < minRatio || ratio > maxRatio)
            return; // implausible; almost certainly a bad triangulation

        if (!hasRangeCorrection[slot])
        {
            rangeCorrection[slot] = ratio;
            hasRangeCorrection[slot] = true;
            return;
        }

        rangeCorrection[slot] = Mathf.Lerp(
            rangeCorrection[slot],
            ratio,
            calibrationBlend
        );
    }

    /// <summary>
    /// Single-camera pose with the learned range correction applied, so the
    /// stereo -> mono transition does not step.
    /// </summary>
    private UnityPose BuildMonoPose(in TagObservation observation, int slot)
    {
        Vector3 localPosition =
            observation.CameraLocalPose.position * rangeCorrection[slot];

        return new UnityPose(
            observation.CameraPose.position +
                observation.CameraPose.rotation * localPosition,
            observation.WorldPose.rotation
        );
    }

    /// <summary>
    /// Republishes a pose as an observation, expressed relative to the
    /// reference camera so downstream stages keep the full stereo context.
    /// </summary>
    private void Emit(in UnityPose worldPose, in TagObservation reference, int sourceId)
    {
        TagState state = GetState(reference.Id);
        state.IsStereo = sourceId == FusedSourceId;
        state.LastEmitTime = Time.unscaledTime;
        tagStates[reference.Id] = state;

        lastEmittedId = reference.Id;

        Quaternion inverseCameraRotation =
            Quaternion.Inverse(reference.CameraPose.rotation);

        UnityPose cameraLocalPose = new UnityPose(
            inverseCameraRotation * (worldPose.position - reference.CameraPose.position),
            inverseCameraRotation * worldPose.rotation
        );

        TagObserved?.Invoke(
            new TagObservation(
                reference.Id,
                worldPose,
                cameraLocalPose,
                reference.CameraPose,
                reference.CaptureTime,
                Time.unscaledTime,
                sourceId,
                reference.SizeCalibrated
            )
        );
    }

    [ContextMenu("Log State")]
    private void LogState()
    {
        var builder = new System.Text.StringBuilder();

        builder.AppendLine(
            $"Range correction L = {rangeCorrection[0]:F4} " +
            $"({(rangeCorrection[0] - 1f) * 100f:F2}%), " +
            $"R = {rangeCorrection[1]:F4} " +
            $"({(rangeCorrection[1] - 1f) * 100f:F2}%)"
        );

        builder.AppendLine(
            $"Head angular speed = L {headAngularSpeed[0]:F1} / R {headAngularSpeed[1]:F1} deg/s"
        );

        builder.AppendLine($"Tracked ids: {tagStates.Count}");

        foreach (KeyValuePair<int, TagState> entry in tagStates)
        {
            TagState state = entry.Value;

            builder.AppendLine(
                $"  id {entry.Key}: {(state.IsStereo ? "stereo" : "mono")}, " +
                $"residual {state.LastResidual * 1000f:F2} mm, " +
                $"convergence {state.LastConvergenceAngle:F3} deg, " +
                $"rot disagreement {state.LastRotationDisagreement:F2} deg, " +
                $"age {Time.unscaledTime - state.LastEmitTime:F2} s"
            );
        }

        Debug.Log(builder.ToString(), this);
    }
}