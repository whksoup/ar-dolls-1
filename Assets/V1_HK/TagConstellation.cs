using System;
using System.Collections.Generic;
using UnityEngine;

using UnityPose = UnityEngine.Pose;

/// <summary>
/// Fuses several tags mounted rigidly on one object into a single virtual tag.
///
/// This is where multi-tag actually pays: two tags 40 cm apart give a rotational
/// lever arm no amount of single-tag refinement can match, and the planar pose
/// ambiguity — the thing <c>StereoTagFusion.maxRotationDisagreementDeg</c> can
/// only detect and paper over — stops being expressible at all, because a flip
/// on one tag no longer agrees with the others' positions.
///
/// It is an <see cref="ITagPoseSource"/> consuming an <see cref="ITagPoseSource"/>,
/// so it layers on top of whatever is already there with no change to either
/// side:
///
///     tracker(L) ─┐
///                 ├─> StereoTagFusion ─> TagConstellation ─> AprilTagAnchor
///     tracker(R) ─┘
///
/// Authoring, mirroring <see cref="AprilTagAnchor"/>: put a child transform per
/// tag under this GameObject, posed as the physical tag sits on the rig, assign
/// it to the matching <see cref="Member"/>, then optionally bake so the markers
/// can be hidden or stripped at runtime. This transform is the rig origin, and
/// the rig origin's pose is what gets published.
///
/// Cost is a handful of quaternion ops per rig per frame, plus a few Newton
/// iterations when the rigid fit is enabled. It runs once per emission, not
/// once per tag per frame.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(150)] // after StereoTagFusion's Update, before LateUpdate consumers
[AddComponentMenu("AprilTag/Tag Constellation")]
public sealed class TagConstellation : MonoBehaviour, ITagPoseSource
{
    /// <summary>One tag id and where it sits on the rig.</summary>
    [Serializable]
    public struct Member
    {
        public int Id;

        [Tooltip("Child transform posed as this tag is mounted. Ignored once offsets are baked.")]
        public Transform Marker;

        [Tooltip("Offset in rig-local space. Filled in by Bake Offsets.")]
        public UnityPose BakedOffset;

        [Tooltip("Relative confidence in this tag. Larger tags deserve more.")]
        public float Weight;
    }

    /// <inheritdoc />
    public event Action<TagObservation> TagObserved;

    [Header("Source")]
    [Tooltip("Any ITagPoseSource: a single tracker, or a StereoTagFusion node.")]
    [SerializeField]
    private MonoBehaviour sourceBehaviour;

    [Header("Rig")]
    [Tooltip("The tags on this rig and where each one sits, relative to this transform.")]
    [SerializeField]
    private Member[] members = Array.Empty<Member>();

    [Tooltip(
        "Use baked offsets instead of reading the marker transforms live. Bake " +
        "if you intend to disable, hide or delete the markers at runtime."
    )]
    [SerializeField]
    private bool useBakedOffsets;

    [Header("Output")]
    [Tooltip(
        "Id stamped onto the fused rig observation. Keep it clear of any real " +
        "tag id; point AprilTagAnchor.tagId at this."
    )]
    [SerializeField]
    private int outputTagId = 1000;

    [Tooltip(
        "Also republish observations for ids that are not members, unchanged. " +
        "Lets this node sit in-line without hiding other tags from consumers " +
        "bound to it."
    )]
    [SerializeField]
    private bool forwardUnmatched = true;

    [Header("Gathering")]
    [Tooltip(
        "How far back from the newest member sighting to look for its partners. " +
        "The rig is rigid, so old sightings are still valid — but only while the " +
        "head has not moved much, since each sighting carries its own camera pose."
    )]
    [SerializeField]
    private float gatherWindow = 0.08f;

    [Tooltip("Members required before a rig pose is published. 1 still works, it is just a single tag.")]
    [SerializeField, Min(1)]
    private int minMembers = 1;

    [Header("Outlier rejection")]
    [Tooltip(
        "Reject a member whose implied rig position sits this far from the " +
        "consensus. A flipped or misdetected tag shows up here first."
    )]
    [SerializeField]
    private float maxPositionSpread = 0.05f;

    [Tooltip("Reject a member whose implied rig rotation sits this far from the consensus.")]
    [SerializeField]
    private float maxRotationSpreadDeg = 15f;

    [Header("Rigid fit")]
    [Tooltip(
        "Solve rotation from the observed tag positions rather than averaging " +
        "the per-tag rotations. This is the real win of a constellation: with " +
        "three or more non-collinear tags, rotation comes from the geometry of " +
        "well-separated points instead of from each tag's own weak planar solve. " +
        "The averaged result is computed too and kept if it fits better, so this " +
        "cannot make things worse."
    )]
    [SerializeField]
    private bool refineWithRigidFit = true;

    [SerializeField, Range(1, 12)]
    private int refineIterations = 5;

    [Tooltip("Skip the rigid fit when the tags span less than this, in metres. Too small a baseline is worse than the average.")]
    [SerializeField]
    private float minRigSpan = 0.05f;

    [Header("Diagnostics")]
    [SerializeField]
    private bool logConstellation;

    private struct Sighting
    {
        public TagObservation Observation;
        public bool Valid;
    }

    private sealed class Candidate
    {
        public int Id;
        public UnityPose RigPose;      // rig pose implied by this tag alone
        public Vector3 RigLocalPoint;  // tag origin in rig space
        public Vector3 WorldPoint;     // tag origin observed in world space
        public float Weight;
        public bool Accepted;
    }

    private ITagPoseSource source;
    private Action<TagObservation> handler;

    private readonly Dictionary<int, int> memberIndexById = new Dictionary<int, int>();
    private readonly Dictionary<int, Sighting> sightings = new Dictionary<int, Sighting>();

    private readonly List<Candidate> candidates = new List<Candidate>();
    private readonly List<Candidate> pool = new List<Candidate>();

    private float lastConsumedTime = -1f;

    /// <summary>Members that contributed to the last published rig pose.</summary>
    public int LastMemberCount { get; private set; }

    /// <summary>RMS distance between the fitted rig's tag positions and the observed ones, in metres.</summary>
    public float LastFitResidual { get; private set; }

    /// <summary>Largest rotational disagreement between members on the last solve, in degrees.</summary>
    public float LastRotationSpread { get; private set; }

    /// <summary>True if the last solve used the rigid fit rather than the averaged rotation.</summary>
    public bool LastUsedRigidFit { get; private set; }

    /// <summary>Rig pose most recently published.</summary>
    public UnityPose LastRigPose { get; private set; }

    /// <summary>Id stamped onto the fused rig observation.</summary>
    public int OutputTagId => outputTagId;

    private void OnEnable()
    {
        source = sourceBehaviour as ITagPoseSource;

        if (source == null)
        {
            Debug.LogError(
                sourceBehaviour == null
                    ? "No pose source assigned."
                    : $"{sourceBehaviour.GetType().Name} does not implement ITagPoseSource.",
                this
            );

            return;
        }

        BuildIndex();

        handler = OnObserved;
        source.TagObserved += handler;
    }

    private void OnDisable()
    {
        if (source != null)
            source.TagObserved -= handler;

        source = null;
        sightings.Clear();
        lastConsumedTime = -1f;
    }

    private void OnValidate()
    {
        if (Application.isPlaying && source != null)
            BuildIndex();
    }

    private void BuildIndex()
    {
        memberIndexById.Clear();

        for (int index = 0; index < members.Length; index++)
        {
            int id = members[index].Id;

            if (id == outputTagId)
            {
                Debug.LogError(
                    $"Member id {id} collides with the output id. Give the rig " +
                    "an output id no physical tag uses.",
                    this
                );

                continue;
            }

            if (memberIndexById.ContainsKey(id))
            {
                Debug.LogWarning($"Duplicate member id {id}; only the first is used.", this);
                continue;
            }

            memberIndexById[id] = index;
        }
    }

    private void OnObserved(TagObservation observation)
    {
        if (!memberIndexById.ContainsKey(observation.Id))
        {
            if (forwardUnmatched)
                TagObserved?.Invoke(observation);

            return;
        }

        sightings[observation.Id] = new Sighting
        {
            Observation = observation,
            Valid = true
        };
    }

    private void Update()
    {
        if (source == null || sightings.Count == 0)
            return;

        // Newest sighting defines the instant we are solving for.
        float newest = float.NegativeInfinity;

        foreach (KeyValuePair<int, Sighting> entry in sightings)
        {
            if (entry.Value.Observation.PublishTime > newest)
                newest = entry.Value.Observation.PublishTime;
        }

        // Nothing new since the last solve.
        if (newest <= lastConsumedTime)
            return;

        lastConsumedTime = newest;

        if (!TrySolve(newest, out UnityPose rigPose, out TagObservation reference, out bool sizeCalibrated))
            return;

        LastRigPose = rigPose;

        Emit(rigPose, reference, sizeCalibrated);
    }

    private bool TrySolve(
        float newest,
        out UnityPose rigPose,
        out TagObservation reference,
        out bool sizeCalibrated)
    {
        rigPose = default;
        reference = default;
        sizeCalibrated = true;

        GatherCandidates(newest, out reference, out sizeCalibrated);

        if (candidates.Count < minMembers)
            return false;

        // --- Consensus, then outlier rejection ------------------------------
        Vector3 provisionalPosition = WeightedMeanPosition(candidates, acceptedOnly: false);
        Quaternion provisionalRotation = WeightedMeanRotation(candidates, acceptedOnly: false);

        int accepted = 0;
        float worstRotation = 0f;

        foreach (Candidate candidate in candidates)
        {
            float positionError =
                Vector3.Distance(candidate.RigPose.position, provisionalPosition);

            float rotationError =
                Quaternion.Angle(candidate.RigPose.rotation, provisionalRotation);

            worstRotation = Mathf.Max(worstRotation, rotationError);

            // With a single candidate there is nothing to disagree with.
            candidate.Accepted =
                candidates.Count == 1 ||
                (positionError <= maxPositionSpread &&
                 rotationError <= maxRotationSpreadDeg);

            if (candidate.Accepted)
                accepted++;
        }

        LastRotationSpread = worstRotation;

        if (accepted < minMembers)
        {
            // Everything disagreed with everything. Nothing here is trustworthy.
            return false;
        }

        Vector3 position = WeightedMeanPosition(candidates, acceptedOnly: true);
        Quaternion rotation = WeightedMeanRotation(candidates, acceptedOnly: true);

        LastMemberCount = accepted;
        LastUsedRigidFit = false;

        float averagedResidual = FitResidual(position, rotation);

        LastFitResidual = averagedResidual;

        // --- Optional rigid fit ---------------------------------------------
        if (refineWithRigidFit && accepted >= 3 && RigSpan() >= minRigSpan)
        {
            if (TryRigidFit(rotation, out Quaternion fitRotation, out Vector3 fitPosition))
            {
                float fitResidual = FitResidual(fitPosition, fitRotation);

                // Only keep it if it genuinely fits better. This is the guard
                // that makes enabling the refinement risk-free.
                if (fitResidual < averagedResidual)
                {
                    position = fitPosition;
                    rotation = fitRotation;
                    LastFitResidual = fitResidual;
                    LastUsedRigidFit = true;
                }
            }
        }

        rigPose = new UnityPose(position, rotation);

        if (logConstellation)
        {
            Debug.Log(
                $"[TagConstellation {outputTagId}] {accepted}/{candidates.Count} members, " +
                $"residual {LastFitResidual * 1000f:F2} mm, " +
                $"rot spread {LastRotationSpread:F2} deg, " +
                $"{(LastUsedRigidFit ? "rigid fit" : "averaged")}" +
                (sizeCalibrated ? string.Empty : ", size assumed"),
                this
            );
        }

        return true;
    }

    /// <summary>
    /// Turns every member sighting inside the gather window into the rig pose it
    /// implies on its own:  rig = tagWorld * offset^-1.
    /// </summary>
    private void GatherCandidates(
        float newest,
        out TagObservation reference,
        out bool sizeCalibrated)
    {
        ReleaseCandidates();

        reference = default;
        sizeCalibrated = true;

        bool haveReference = false;
        float referenceTime = float.NegativeInfinity;

        foreach (KeyValuePair<int, Sighting> entry in sightings)
        {
            Sighting sighting = entry.Value;

            if (!sighting.Valid)
                continue;

            if (newest - sighting.Observation.PublishTime > gatherWindow)
                continue;

            if (!memberIndexById.TryGetValue(entry.Key, out int memberIndex))
                continue;

            UnityPose offset = OffsetFor(members[memberIndex]);

            Quaternion rigRotation =
                sighting.Observation.WorldPose.rotation * Quaternion.Inverse(offset.rotation);

            Vector3 rigPosition =
                sighting.Observation.WorldPose.position - rigRotation * offset.position;

            Candidate candidate = Rent();
            candidate.Id = entry.Key;
            candidate.RigPose = new UnityPose(rigPosition, rigRotation);
            candidate.RigLocalPoint = offset.position;
            candidate.WorldPoint = sighting.Observation.WorldPose.position;
            candidate.Weight = members[memberIndex].Weight > 0f ? members[memberIndex].Weight : 1f;
            candidate.Accepted = true;

            candidates.Add(candidate);

            sizeCalibrated &= sighting.Observation.SizeCalibrated;

            // The most recent sighting supplies the camera context we republish.
            if (sighting.Observation.PublishTime > referenceTime)
            {
                referenceTime = sighting.Observation.PublishTime;
                reference = sighting.Observation;
                haveReference = true;
            }
        }

        if (!haveReference)
            sizeCalibrated = false;
    }

    private static Vector3 WeightedMeanPosition(List<Candidate> list, bool acceptedOnly)
    {
        Vector3 sum = Vector3.zero;
        float weight = 0f;

        foreach (Candidate candidate in list)
        {
            if (acceptedOnly && !candidate.Accepted)
                continue;

            sum += candidate.RigPose.position * candidate.Weight;
            weight += candidate.Weight;
        }

        return weight > 0f ? sum / weight : Vector3.zero;
    }

    /// <summary>
    /// Incremental weighted slerp. Order-dependent in principle, negligibly so
    /// for the small angular spreads a working rig produces, and far cheaper
    /// than a proper quaternion eigen-average.
    /// </summary>
    private static Quaternion WeightedMeanRotation(List<Candidate> list, bool acceptedOnly)
    {
        Quaternion mean = Quaternion.identity;
        float accumulated = 0f;
        bool first = true;

        foreach (Candidate candidate in list)
        {
            if (acceptedOnly && !candidate.Accepted)
                continue;

            if (first)
            {
                mean = candidate.RigPose.rotation;
                accumulated = candidate.Weight;
                first = false;
                continue;
            }

            accumulated += candidate.Weight;

            mean = Quaternion.Slerp(
                mean,
                candidate.RigPose.rotation,
                candidate.Weight / accumulated
            );
        }

        return mean;
    }

    /// <summary>RMS distance between where the rig says the tags are and where they were seen.</summary>
    private float FitResidual(Vector3 position, Quaternion rotation)
    {
        float sum = 0f;
        int count = 0;

        foreach (Candidate candidate in candidates)
        {
            if (!candidate.Accepted)
                continue;

            Vector3 predicted = position + rotation * candidate.RigLocalPoint;
            sum += (predicted - candidate.WorldPoint).sqrMagnitude;
            count++;
        }

        return count > 0 ? Mathf.Sqrt(sum / count) : 0f;
    }

    /// <summary>Largest separation between accepted tags in rig space.</summary>
    private float RigSpan()
    {
        float span = 0f;

        for (int i = 0; i < candidates.Count; i++)
        {
            if (!candidates[i].Accepted)
                continue;

            for (int j = i + 1; j < candidates.Count; j++)
            {
                if (!candidates[j].Accepted)
                    continue;

                span = Mathf.Max(
                    span,
                    Vector3.Distance(candidates[i].RigLocalPoint, candidates[j].RigLocalPoint)
                );
            }
        }

        return span;
    }

    /// <summary>
    /// Rotation and translation aligning the rig-space tag positions onto the
    /// observed world positions.
    ///
    /// Rotation is refined by small-angle Newton iteration rather than an SVD:
    /// with the centred point sets a and b, the residual torque
    ///   omega = sum(Ra x b) / sum(Ra . b)
    /// is a first-order estimate of the remaining rotation, and applying
    /// AngleAxis(atan(|omega|), omega) converges in a handful of steps from any
    /// sensible seed. Cheaper and far less code than a 3x3 SVD, and the caller
    /// only keeps the result if it beats the averaged rotation anyway.
    /// </summary>
    private bool TryRigidFit(Quaternion seed, out Quaternion rotation, out Vector3 position)
    {
        rotation = seed;
        position = Vector3.zero;

        Vector3 centroidLocal = Vector3.zero;
        Vector3 centroidWorld = Vector3.zero;
        float weight = 0f;

        foreach (Candidate candidate in candidates)
        {
            if (!candidate.Accepted)
                continue;

            centroidLocal += candidate.RigLocalPoint * candidate.Weight;
            centroidWorld += candidate.WorldPoint * candidate.Weight;
            weight += candidate.Weight;
        }

        if (weight <= 0f)
            return false;

        centroidLocal /= weight;
        centroidWorld /= weight;

        for (int iteration = 0; iteration < refineIterations; iteration++)
        {
            Vector3 numerator = Vector3.zero;
            float denominator = 0f;

            foreach (Candidate candidate in candidates)
            {
                if (!candidate.Accepted)
                    continue;

                Vector3 a = rotation * (candidate.RigLocalPoint - centroidLocal);
                Vector3 b = candidate.WorldPoint - centroidWorld;

                numerator += Vector3.Cross(a, b) * candidate.Weight;
                denominator += Vector3.Dot(a, b) * candidate.Weight;
            }

            if (Mathf.Abs(denominator) < 1e-9f)
                return false;

            Vector3 omega = numerator / denominator;
            float magnitude = omega.magnitude;

            if (magnitude < 1e-9f)
                break;

            float angle = Mathf.Atan(magnitude) * Mathf.Rad2Deg;

            rotation = Quaternion.AngleAxis(angle, omega / magnitude) * rotation;

            if (angle < 1e-4f)
                break;
        }

        position = centroidWorld - rotation * centroidLocal;
        return true;
    }

    private void Emit(in UnityPose rigPose, in TagObservation reference, bool sizeCalibrated)
    {
        Quaternion inverseCameraRotation =
            Quaternion.Inverse(reference.CameraPose.rotation);

        UnityPose cameraLocalPose = new UnityPose(
            inverseCameraRotation * (rigPose.position - reference.CameraPose.position),
            inverseCameraRotation * rigPose.rotation
        );

        TagObserved?.Invoke(
            new TagObservation(
                outputTagId,
                rigPose,
                cameraLocalPose,
                reference.CameraPose,
                reference.CaptureTime,
                Time.unscaledTime,
                reference.SourceId,
                sizeCalibrated
            )
        );
    }

    /// <summary>Offset of a member in rig-local space.</summary>
    private UnityPose OffsetFor(in Member member)
    {
        if (useBakedOffsets || member.Marker == null)
            return member.BakedOffset;

        return LocalPoseOf(member.Marker);
    }

    private UnityPose LocalPoseOf(Transform marker)
    {
        return new UnityPose(
            transform.InverseTransformPoint(marker.position),
            Quaternion.Inverse(transform.rotation) * marker.rotation
        );
    }

    [ContextMenu("Bake Offsets")]
    private void BakeOffsets()
    {
        int baked = 0;

        for (int index = 0; index < members.Length; index++)
        {
            if (members[index].Marker == null)
                continue;

            members[index].BakedOffset = LocalPoseOf(members[index].Marker);
            baked++;
        }

        if (baked == 0)
        {
            Debug.LogWarning("No markers assigned; nothing to bake.", this);
            return;
        }

        useBakedOffsets = true;

        Debug.Log($"Baked {baked} member offset(s).", this);

#if UNITY_EDITOR
        UnityEditor.EditorUtility.SetDirty(this);
#endif
    }

    [ContextMenu("Log Rig")]
    private void LogRig()
    {
        var builder = new System.Text.StringBuilder();

        builder.AppendLine($"Output id {outputTagId}, {members.Length} member(s)");

        foreach (Member member in members)
        {
            UnityPose offset = OffsetFor(member);

            builder.AppendLine(
                $"  id {member.Id}: local {offset.position}, " +
                $"euler {offset.rotation.eulerAngles}, " +
                $"weight {(member.Weight > 0f ? member.Weight : 1f):F2}"
            );
        }

        builder.Append(
            $"Last solve: {LastMemberCount} member(s), " +
            $"residual {LastFitResidual * 1000f:F2} mm, " +
            $"rot spread {LastRotationSpread:F2} deg, " +
            $"{(LastUsedRigidFit ? "rigid fit" : "averaged")}"
        );

        Debug.Log(builder.ToString(), this);
    }

    // ---- candidate pooling (keeps the per-frame solve allocation-free) -----

    private Candidate Rent()
    {
        int last = pool.Count - 1;

        if (last < 0)
            return new Candidate();

        Candidate candidate = pool[last];
        pool.RemoveAt(last);
        return candidate;
    }

    private void ReleaseCandidates()
    {
        foreach (Candidate candidate in candidates)
            pool.Add(candidate);

        candidates.Clear();
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(0.2f, 1f, 0.6f, 1f);

        foreach (Member member in members)
        {
            UnityPose offset = OffsetFor(member);

            Vector3 worldPosition = transform.TransformPoint(offset.position);
            Quaternion worldRotation = transform.rotation * offset.rotation;

            Gizmos.color = new Color(0.2f, 1f, 0.6f, 1f);
            Gizmos.DrawLine(transform.position, worldPosition);

            Gizmos.matrix = Matrix4x4.TRS(worldPosition, worldRotation, Vector3.one);
            Gizmos.DrawWireCube(Vector3.zero, new Vector3(0.12f, 0.12f, 0.001f));

            Gizmos.color = Color.blue;
            Gizmos.DrawLine(Vector3.zero, Vector3.forward * 0.06f);

            Gizmos.matrix = Matrix4x4.identity;
        }
    }
}
