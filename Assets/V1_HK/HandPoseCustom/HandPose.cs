using System;
using System.Collections.Generic;
using UnityEngine;
using Oculus.Interaction.Input;
using Oculus.Interaction.PoseDetection;

/// <summary>
/// Reads finger feature values straight out of <see cref="FingerShapes"/>,
/// bypassing <see cref="FingerFeatureStateProvider"/> entirely.
///
/// The state provider exists to quantise these same numbers into named bands
/// using a thresholds asset that is authored per finger and shared by every
/// pose in the project. Poses that want overlapping bands on the same finger
/// cannot both be expressed that way. Working in raw values instead means each
/// pose carries its own ranges, and the only dependency is an IHand.
/// </summary>
public static class FingerFeatureSampler
{
    public static readonly HandFinger[] AllFingers =
    {
        HandFinger.Thumb,
        HandFinger.Index,
        HandFinger.Middle,
        HandFinger.Ring,
        HandFinger.Pinky
    };

    public static readonly FingerFeature[] AllFeatures =
    {
        FingerFeature.Curl,
        FingerFeature.Flexion,
        FingerFeature.Abduction,
        FingerFeature.Opposition
    };

    private static readonly FingerShapes Shapes = FingerFeatureStateProvider.DefaultFingerShapes;

    /// <summary>
    /// Rough full-scale span of each feature, used only to normalise "how tightly
    /// was this held" during recording. Curl/flexion/abduction are angles in
    /// degrees; opposition is a distance in metres, which is why one shared
    /// constant would be meaningless. Tune if your auto-enable is too eager.
    /// </summary>
    public static float NominalSpan(FingerFeature feature)
    {
        switch (feature)
        {
            case FingerFeature.Curl: return 100f;
            case FingerFeature.Flexion: return 100f;
            case FingerFeature.Abduction: return 40f;
            case FingerFeature.Opposition: return 0.12f;
            default: return 100f;
        }
    }

    /// <summary>Smallest padding worth applying to a range, in that feature's own units.</summary>
    public static float MinimumMargin(FingerFeature feature) =>
        feature == FingerFeature.Opposition ? 0.004f : 2.5f;

    /// <summary>
    /// False when the hand is untracked or the pair is undefined — abduction has
    /// no meaning on the pinky, opposition none on the thumb.
    /// </summary>
    public static bool TryGetValue(IHand hand, HandFinger finger, FingerFeature feature, out float value)
    {
        value = 0f;

        if (hand == null)
            return false;

        float? sampled = Shapes.GetValue(finger, feature, hand);

        if (!sampled.HasValue || float.IsNaN(sampled.Value))
            return false;

        value = sampled.Value;
        return true;
    }
}

/// <summary>
/// Where a pose gets its numbers from. Exists so matching does not care whether
/// the values came straight off an IHand or out of a debugger that already
/// polled them this frame.
/// </summary>
public interface IFingerValueSource
{
    bool TryGetValue(HandFinger finger, FingerFeature feature, out float value);

    Handedness Handedness { get; }

    bool IsHighConfidence { get; }
}

/// <summary>Reads FingerShapes directly. Mutable so it can be reused without allocating per frame.</summary>
public sealed class HandValueSource : IFingerValueSource
{
    public IHand Hand { get; set; }

    public HandValueSource(IHand hand = null) => Hand = hand;

    public bool TryGetValue(HandFinger finger, FingerFeature feature, out float value) =>
        FingerFeatureSampler.TryGetValue(Hand, finger, feature, out value);

    public Handedness Handedness => Hand?.Handedness ?? Handedness.Right;

    public bool IsHighConfidence => Hand != null && Hand.IsHighConfidence;
}

/// <summary>
/// Reads the snapshots a <see cref="FingerFeatureStateDebugger"/> took this
/// frame. An optional hand supplies handedness and confidence, which the
/// debugger does not carry.
/// </summary>
public sealed class DebuggerValueSource : IFingerValueSource
{
    public FingerFeatureStateDebugger Debugger { get; set; }

    public IHand Hand { get; set; }

    public DebuggerValueSource(FingerFeatureStateDebugger debugger = null, IHand hand = null)
    {
        Debugger = debugger;
        Hand = hand;
    }

    public bool TryGetValue(HandFinger finger, FingerFeature feature, out float value)
    {
        value = 0f;

        if (Debugger == null ||
            !Debugger.TryGetSnapshot(finger, feature, out FingerFeatureStateDebugger.FingerFeatureSnapshot snapshot) ||
            !snapshot.HasValue)
        {
            return false;
        }

        value = snapshot.Value;
        return true;
    }

    public Handedness Handedness => Hand?.Handedness ?? Handedness.Right;

    /// <summary>Without a hand there is nothing to ask, so assume good rather than block everything.</summary>
    public bool IsHighConfidence => Hand == null || Hand.IsHighConfidence;
}

/// <summary>
/// A hand pose defined as a set of per-finger value ranges, recorded from a
/// distribution rather than a single frame.
///
/// Strictness lives on the pose, not on the recogniser: a deliberate,
/// unmistakable pose can afford a tight <see cref="requiredConfidence"/>, while
/// a loose "relaxed hand" pose needs to accept far more variation. One global
/// setting cannot serve both.
///
/// Matching is asymmetric by design. A constraint must fall inside its recorded
/// range to *enter* the pose, but is allowed to drift out to
/// <see cref="exitToleranceMultiplier"/> before the pose is considered exited.
/// Without that gap a value sitting on a boundary chatters between begin and
/// end every frame, which is the failure mode a raw min/max test always has.
/// </summary>
[CreateAssetMenu(menuName = "Hands/Hand Pose", fileName = "HandPose")]
public sealed class HandPose : ScriptableObject
{
    /// <summary>One finger feature pinned to a range, plus the statistics it came from.</summary>
    [Serializable]
    public struct Constraint
    {
        [Tooltip("Disabled constraints are ignored entirely — they neither match nor block.")]
        public bool Enabled;

        public HandFinger Finger;
        public FingerFeature Feature;

        [Tooltip("Inclusive range for entering the pose, already padded by the recorder.")]
        public float Min;

        public float Max;

        [Header("Capture statistics")]
        public float Mean;
        public float StandardDeviation;
        public float ObservedMin;
        public float ObservedMax;
        public int SampleCount;

        public float Centre => 0.5f * (Min + Max);

        public float HalfWidth => Mathf.Max(0.0001f, 0.5f * (Max - Min));

        /// <summary>0 at the centre of the range, 1 at its edge, above 1 outside it.</summary>
        public float NormalisedDistance(float value) =>
            Mathf.Abs(value - Centre) / HalfWidth;
    }

    [Header("Identity")]
    [SerializeField, Tooltip("Raised through GestureCommands under this key. Keep stable — renaming orphans subscribers.")]
    private string key = "Pose";

    [SerializeField, Tooltip("Hand this was recorded from. Abduction sign conventions differ between hands.")]
    private Handedness recordedHandedness = Handedness.Right;

    [SerializeField, Tooltip("Allow this pose to match the opposite hand. Off unless you have verified it transfers.")]
    private bool matchesEitherHand = true;

    [Header("Constraints")]
    [SerializeField]
    private List<Constraint> constraints = new List<Constraint>();

    [Header("Strictness")]
    [SerializeField, Range(0f, 1f)]
    [Tooltip(
        "Minimum match quality to hold this pose. 0 accepts anything inside the " +
        "ranges; 0.5 demands values sit near the middle of them. Raise it on a " +
        "pose that fires when it should not, lower it on one that will not fire."
    )]
    private float requiredConfidence = 0.25f;

    [SerializeField, Range(0.25f, 4f)]
    [Tooltip("Scales every recorded range about its centre. Above 1 widens, below 1 tightens.")]
    private float toleranceScale = 1f;

    [SerializeField, Range(1f, 2f)]
    [Tooltip("How far outside the range a value may drift before the pose ends. This is the anti-chatter margin.")]
    private float exitToleranceMultiplier = 1.35f;

    [Header("Timing")]
    [SerializeField, Tooltip("Seconds the pose must match continuously before it begins.")]
    private float beginHoldSeconds = 0.08f;

    [SerializeField, Tooltip("Seconds the pose must fail continuously before it ends.")]
    private float endHoldSeconds = 0.06f;

    [Header("Tracking")]
    [SerializeField, Tooltip("Ignore frames where hand tracking is low confidence rather than matching against noise.")]
    private bool requireHighConfidenceTracking = true;

    [SerializeField, Tooltip("Informational: when this pose was last recorded.")]
    private string recordedAt;

    public string Key => key;
    public Handedness RecordedHandedness => recordedHandedness;
    public bool MatchesEitherHand => matchesEitherHand;
    public float RequiredConfidence => requiredConfidence;
    public float BeginHoldSeconds => beginHoldSeconds;
    public float EndHoldSeconds => endHoldSeconds;
    public bool RequireHighConfidenceTracking => requireHighConfidenceTracking;
    public string RecordedAt => recordedAt;

    public IReadOnlyList<Constraint> Constraints => constraints;

    /// <summary>Number of constraints actually being tested. Doubles as a specificity measure for arbitration.</summary>
    public int EnabledCount
    {
        get
        {
            int count = 0;

            foreach (Constraint constraint in constraints)
            {
                if (constraint.Enabled)
                    count++;
            }

            return count;
        }
    }

    /// <summary>
    /// Evaluate against a live value source.
    ///
    /// <paramref name="currentlyHeld"/> selects which tolerance applies: entering
    /// uses the recorded range, holding uses the widened exit range.
    /// </summary>
    /// <param name="score">
    /// 1 when every constraint sits dead centre, 0 at the edge of tolerance.
    /// Meaningful only when the method returns true.
    /// </param>
    public bool TryMatch(IFingerValueSource source, bool currentlyHeld, out float score)
    {
        score = 0f;

        if (source == null || constraints.Count == 0)
            return false;

        if (!matchesEitherHand && source.Handedness != recordedHandedness)
            return false;

        float limit = currentlyHeld ? exitToleranceMultiplier : 1f;
        float total = 0f;
        int tested = 0;

        foreach (Constraint constraint in constraints)
        {
            if (!constraint.Enabled)
                continue;

            if (!source.TryGetValue(constraint.Finger, constraint.Feature, out float value))
            {
                // A constraint we cannot read is a constraint we cannot satisfy.
                // Treating it as a pass would let poses fire through occlusion.
                return false;
            }

            float distance = constraint.NormalisedDistance(value) / Mathf.Max(0.01f, toleranceScale);

            if (distance > limit)
                return false;

            total += Mathf.Clamp01(distance);
            tested++;
        }

        if (tested == 0)
            return false;

        score = 1f - (total / tested);

        return score >= (currentlyHeld ? requiredConfidence * 0.75f : requiredConfidence);
    }

    private static readonly HandValueSource SharedHandSource = new HandValueSource();

    /// <summary>Convenience overload for callers holding an IHand. Not reentrant.</summary>
    public bool TryMatch(IHand hand, bool currentlyHeld, out float score)
    {
        SharedHandSource.Hand = hand;
        return TryMatch(SharedHandSource, currentlyHeld, out score);
    }

    /// <summary>One constraint's verdict, for diagnostics. Never short-circuits.</summary>
    public struct ConstraintEvaluation
    {
        public HandFinger Finger;
        public FingerFeature Feature;

        /// <summary>False when the source could not produce a value — occlusion, or an undefined pair.</summary>
        public bool Readable;

        public float Value;

        /// <summary>Range actually being tested, after toleranceScale and the enter/exit limit.</summary>
        public float EffectiveMin;

        public float EffectiveMax;

        /// <summary>0 at range centre, 1 at its edge. Above the limit means failed.</summary>
        public float Distance;

        public bool Met;
    }

    /// <summary>Match quality this pose demands right now. Relaxes once held.</summary>
    public float ConfidenceThreshold(bool currentlyHeld) =>
        currentlyHeld ? requiredConfidence * 0.75f : requiredConfidence;

    /// <summary>
    /// Evaluates every enabled constraint and reports each one, rather than
    /// returning at the first failure the way <see cref="TryMatch"/> does.
    ///
    /// The score here averages over all enabled constraints including failing
    /// ones, so it will not always agree with TryMatch's score on a failing
    /// pose. It is a diagnostic, not the matching path.
    /// </summary>
    public void Evaluate(
        IFingerValueSource source,
        bool currentlyHeld,
        List<ConstraintEvaluation> results,
        out float score,
        out bool constraintsMet,
        out bool handednessMet)
    {
        results?.Clear();

        score = 0f;
        constraintsMet = false;
        handednessMet = true;

        if (source == null)
            return;

        handednessMet = matchesEitherHand || source.Handedness == recordedHandedness;

        float limit = currentlyHeld ? exitToleranceMultiplier : 1f;
        float scale = Mathf.Max(0.01f, toleranceScale);
        float total = 0f;
        int tested = 0;
        bool allMet = true;

        foreach (Constraint constraint in constraints)
        {
            if (!constraint.Enabled)
                continue;

            bool readable = source.TryGetValue(constraint.Finger, constraint.Feature, out float value);

            float halfWidth = constraint.HalfWidth * scale * limit;

            var evaluation = new ConstraintEvaluation
            {
                Finger = constraint.Finger,
                Feature = constraint.Feature,
                Readable = readable,
                Value = value,
                EffectiveMin = constraint.Centre - halfWidth,
                EffectiveMax = constraint.Centre + halfWidth,
                Distance = readable ? constraint.NormalisedDistance(value) / scale : float.NaN,
                Met = false
            };

            if (readable)
            {
                evaluation.Met = evaluation.Distance <= limit;
                total += Mathf.Clamp01(evaluation.Distance);
                tested++;
            }

            if (!evaluation.Met)
                allMet = false;

            results?.Add(evaluation);
        }

        if (tested > 0)
            score = 1f - (total / tested);

        constraintsMet = allMet && tested > 0;
    }

    /// <summary>Overwrite the constraint set. Used by the recorder.</summary>
    public void SetConstraints(IEnumerable<Constraint> recorded, Handedness handedness)
    {
        constraints.Clear();
        constraints.AddRange(recorded);
        recordedHandedness = handedness;
        recordedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        MarkDirty();
    }

    public string ToJson(bool prettyPrint = true) => JsonUtility.ToJson(this, prettyPrint);

    /// <summary>Overwrite this asset from JSON, preserving nothing.</summary>
    public void OverwriteFromJson(string json)
    {
        JsonUtility.FromJsonOverwrite(json, this);
        MarkDirty();
    }

    /// <summary>Build a throwaway pose from JSON. Does not touch any asset on disk.</summary>
    public static HandPose FromJson(string json)
    {
        HandPose instance = CreateInstance<HandPose>();
        JsonUtility.FromJsonOverwrite(json, instance);
        return instance;
    }

    private void MarkDirty()
    {
#if UNITY_EDITOR
        UnityEditor.EditorUtility.SetDirty(this);
#endif
    }
}