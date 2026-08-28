using System.Collections.Generic;
using Oculus.Interaction;
using Oculus.Interaction.Input;
using Oculus.Interaction.PoseDetection;
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// Evaluates a set of <see cref="HandPose"/> assets against one hand each frame,
/// picks a single winner, and raises begin/end through
/// <see cref="GestureCommands"/> under the winning pose's key.
///
/// Drops in beside <see cref="GestureBinder"/> rather than replacing it: both
/// publish into the same bus, so an existing consumer cannot tell whether a key
/// came from an SDK selector or from a recorded pose.
///
/// Arbitration is deliberate. Several poses will match at once as soon as you
/// have more than a couple — a fist satisfies most of a "thumbs up" minus the
/// thumb. Firing every match makes consumers responsible for sorting it out.
/// Exactly one pose is active at a time, chosen on match quality, with the more
/// specific pose winning a tie because it was harder to satisfy.
///
/// Tracking loss holds the current state rather than ending it. Hands clip the
/// camera constantly and a hold is far less disruptive than a spurious end
/// event followed by a re-begin.
///
/// Filename must match the class name or Unity will not offer this in the
/// Add Component menu.
/// </summary>
[DefaultExecutionOrder(100)] // after the debugger's Update, so its snapshots are this frame's
public sealed class HandPoseRecognizer : MonoBehaviour
{
    public enum SourceMode
    {
        /// <summary>Use the hand while it reads; switch to the debugger when it does not.</summary>
        Auto,

        /// <summary>Read FingerShapes directly off the IHand.</summary>
        Hand,

        /// <summary>Read the values a FingerFeatureStateDebugger already polled this frame.</summary>
        Debugger
    }

    [Header("Source")]
    [SerializeField]
    [Tooltip(
        "Auto re-probes the hand each frame and falls through to the debugger " +
        "when it reads nothing. Pin it once you know which path works."
    )]
    private SourceMode sourceMode = SourceMode.Auto;

    [SerializeField, Interface(typeof(IHand))]
    [Tooltip("Usually a HandRef. Also supplies handedness and confidence when reading via the debugger.")]
    private UnityEngine.Object handSource;

    [SerializeField, Optional]
    [Tooltip(
        "Fallback reader. Must be watching every finger and feature your poses " +
        "constrain, or those poses can never match."
    )]
    private FingerFeatureStateDebugger debuggerSource;

    [Header("Poses")]
    [SerializeField, Tooltip("Candidates evaluated every frame. Order does not matter; scoring decides.")]
    private List<HandPose> poses = new List<HandPose>();

    [Header("Matching")]
    [SerializeField]
    [Tooltip(
        "Matching is pure range containment: a constraint passes when the live " +
        "value sits between its Min and Max. The pose asset's Required " +
        "Confidence and Tolerance Scale are ignored."
    )]
    [Range(0f, 1f)]
    //[Tooltip("Widens every range by this fraction once a pose is held. Anti-chatter only.")]
    private float exitMargin = 0.15f;

    [SerializeField]
    [Tooltip(
        "Skip constraints the source cannot read rather than failing the pose. " +
        "Needed when reading through a debugger that is not watching every " +
        "feature — otherwise those poses can never match."
    )]
    private bool ignoreUnreadableConstraints = true;

    [Header("Options")]
    [SerializeField]
    [Tooltip(
        "Require the incoming pose to beat the held one by this margin before " +
        "switching. Stops two similar poses trading the lock back and forth."
    )]
    [Range(0f, 0.5f)]
    private float switchMargin = 0.08f;

    [SerializeField, Tooltip("Log every begin and end with its score.")]
    private bool logTransitions;

    [Header("Local hooks")]
    [Tooltip("Fired in addition to the GestureCommands bus. Argument is the pose key.")]
    public UnityEvent<string> WhenPoseBegan;

    public UnityEvent<string> WhenPoseEnded;

    private IHand hand;

    private HandValueSource handValueSource;
    private DebuggerValueSource debuggerValueSource;
    private IFingerValueSource activeSource;

    private HandPose activePose;
    private float activeScore;

    private HandPose candidate;
    private float candidateScore;
    private float candidateSince;
    private float failingSince;
    private bool activeIsFailing;

    /// <summary>Currently held pose, or null.</summary>
    public HandPose ActivePose => activePose;

    /// <summary>Match quality of the held pose. 0 when nothing is held.</summary>
    public float ActiveScore => activePose == null ? 0f : activeScore;

    public string ActiveKey => activePose == null ? string.Empty : activePose.Key;

    /// <summary>Which reader is being used right now. For the inspector.</summary>
    public string SourceDescription =>
        activeSource == null
            ? "none"
            : activeSource == debuggerValueSource ? "FingerFeatureStateDebugger" : "IHand (FingerShapes)";

    private void Awake()
    {
        hand = handSource as IHand;

        if (hand == null && handSource != null)
        {
            Debug.LogError(
                $"[HandPoseRecognizer] {handSource.GetType().Name} does not implement IHand.",
                this
            );
        }

        if (hand == null && debuggerSource == null)
        {
            Debug.LogError(
                "[HandPoseRecognizer] No hand and no debugger assigned; nothing to read.",
                this
            );
        }

        handValueSource = new HandValueSource(hand);
        debuggerValueSource = new DebuggerValueSource(debuggerSource, hand);
    }

    /// <summary>
    /// Picks the reader for this frame. Auto re-probes rather than latching,
    /// because a hand that starts reading mid-session should be picked up
    /// without a restart.
    /// </summary>
    private IFingerValueSource ResolveSource()
    {
        switch (sourceMode)
        {
            case SourceMode.Hand:
                return hand != null ? handValueSource : null;

            case SourceMode.Debugger:
                return debuggerSource != null ? (IFingerValueSource)debuggerValueSource : null;

            default:
                if (hand != null && HandReadsAnything())
                    return handValueSource;

                return debuggerSource != null ? (IFingerValueSource)debuggerValueSource : null;
        }
    }

    /// <summary>
    /// Evaluates a pose by plain range containment against the live source.
    ///
    /// This deliberately does not call <c>HandPose.TryMatch</c>. That path also
    /// gates on an averaged match score, which means a pose whose every value is
    /// comfortably inside its recorded range can still refuse to fire because
    /// the values sit off-centre. Here a constraint is met or it is not, and the
    /// score is reported for ranking rather than used as a threshold.
    ///
    /// <paramref name="results"/> may be null when only the verdict is wanted.
    /// </summary>
    private bool Evaluate(
        HandPose pose,
        bool held,
        List<HandPose.ConstraintEvaluation> results,
        out float score,
        out int metCount,
        out int testedCount)
    {
        results?.Clear();

        score = 0f;
        metCount = 0;
        testedCount = 0;

        if (pose == null || activeSource == null)
            return false;

        if (!pose.MatchesEitherHand && activeSource.Handedness != pose.RecordedHandedness)
            return false;

        float widen = held ? 1f + exitMargin : 1f;
        float total = 0f;
        bool allMet = true;

        foreach (HandPose.Constraint constraint in pose.Constraints)
        {
            if (!constraint.Enabled)
                continue;

            bool readable = activeSource.TryGetValue(
                constraint.Finger,
                constraint.Feature,
                out float value
            );

            float halfWidth = constraint.HalfWidth * widen;
            float min = constraint.Centre - halfWidth;
            float max = constraint.Centre + halfWidth;

            var evaluation = new HandPose.ConstraintEvaluation
            {
                Finger = constraint.Finger,
                Feature = constraint.Feature,
                Readable = readable,
                Value = value,
                EffectiveMin = min,
                EffectiveMax = max,
                Distance = readable ? Mathf.Abs(value - constraint.Centre) / constraint.HalfWidth : float.NaN,
                Met = readable && value >= min && value <= max
            };

            results?.Add(evaluation);

            if (!readable)
            {
                // An unwatched feature is a gap in the reader, not evidence
                // about the hand. Failing on it makes whole poses undebuggable.
                if (!ignoreUnreadableConstraints)
                    allMet = false;

                continue;
            }

            testedCount++;
            total += Mathf.Clamp01(evaluation.Distance);

            if (evaluation.Met)
                metCount++;
            else
                allMet = false;
        }

        if (testedCount == 0)
            return false;

        score = 1f - (total / testedCount);

        return allMet;
    }

    private bool HandReadsAnything()
    {
        foreach (HandFinger finger in FingerFeatureSampler.AllFingers)
        {
            foreach (FingerFeature feature in FingerFeatureSampler.AllFeatures)
            {
                if (FingerFeatureSampler.TryGetValue(hand, finger, feature, out _))
                    return true;
            }
        }

        return false;
    }

    private void OnDisable()
    {
        // Never leave the bus believing a pose is still held.
        if (activePose != null)
            EndActive();

        candidate = null;
    }

    private void Update()
    {
        activeSource = ResolveSource();

        if (activeSource == null)
            return;

        // Hold everything while tracking is untrustworthy.
        if (!activeSource.IsHighConfidence && RequiresConfidence())
            return;

        EvaluateActive();
        EvaluateCandidate();
    }

    /// <summary>True if the held pose (or, with none held, any pose) wants confident tracking.</summary>
    private bool RequiresConfidence()
    {
        if (activePose != null)
            return activePose.RequireHighConfidenceTracking;

        foreach (HandPose pose in poses)
        {
            if (pose != null && pose.RequireHighConfidenceTracking)
                return true;
        }

        return false;
    }

    private void EvaluateActive()
    {
        if (activePose == null)
            return;

        if (Evaluate(activePose, true, null, out float score, out _, out _))
        {
            activeScore = score;
            activeIsFailing = false;
            return;
        }

        if (!activeIsFailing)
        {
            activeIsFailing = true;
            failingSince = Time.unscaledTime;
            return;
        }

        if (Time.unscaledTime - failingSince >= activePose.EndHoldSeconds)
            EndActive();
    }

    private void EvaluateCandidate()
    {
        HandPose best = null;
        float bestScore = 0f;
        int bestSpecificity = 0;

        foreach (HandPose pose in poses)
        {
            if (pose == null || pose == activePose)
                continue;

            if (!Evaluate(pose, false, null, out float score, out _, out _))
                continue;

            int specificity = pose.EnabledCount;

            bool better =
                best == null ||
                score > bestScore + 0.0001f ||
                (Mathf.Abs(score - bestScore) <= 0.0001f && specificity > bestSpecificity);

            if (!better)
                continue;

            best = pose;
            bestScore = score;
            bestSpecificity = specificity;
        }

        if (best == null)
        {
            candidate = null;
            return;
        }

        // Do not unseat a held pose unless the newcomer is clearly better.
        if (activePose != null && bestScore < activeScore + switchMargin)
        {
            candidate = null;
            return;
        }

        if (candidate != best)
        {
            candidate = best;
            candidateSince = Time.unscaledTime;
        }

        candidateScore = bestScore;

        if (Time.unscaledTime - candidateSince < best.BeginHoldSeconds)
            return;

        if (activePose != null)
            EndActive();

        BeginPose(best, candidateScore);
        candidate = null;
    }

    private void BeginPose(HandPose pose, float score)
    {
        activePose = pose;
        activeScore = score;
        activeIsFailing = false;

        if (logTransitions)
            Debug.Log($"[HandPoseRecognizer] began \"{pose.Key}\" (score {score:F2})", this);

        GestureCommands.RaiseBegan(pose.Key, BuildContext(pose.Key));
        WhenPoseBegan?.Invoke(pose.Key);
    }

    private void EndActive()
    {
        HandPose ending = activePose;

        activePose = null;
        activeScore = 0f;
        activeIsFailing = false;

        if (ending == null)
            return;

        if (logTransitions)
            Debug.Log($"[HandPoseRecognizer] ended \"{ending.Key}\"", this);

        GestureCommands.RaiseEnded(ending.Key, BuildContext(ending.Key));
        WhenPoseEnded?.Invoke(ending.Key);
    }

    private GestureContext BuildContext(string key)
    {
        Pose rootPose = Pose.identity;

        if (hand != null)
            hand.GetRootPose(out rootPose);

        return new GestureContext(
            key,
            activeSource?.Handedness ?? Handedness.Right,
            rootPose,
            Time.unscaledTime
        );
    }

    /// <summary>Poses assigned to this recognizer. For the inspector.</summary>
    public IReadOnlyList<HandPose> Poses => poses;

    /// <summary>
    /// True when the current source is producing readings at all. Distinguishes
    /// "nothing matched" from "nothing is being read", which look identical
    /// from the outside and have completely different fixes.
    /// </summary>
    public bool SourceIsReading
    {
        get
        {
            IFingerValueSource source = activeSource ?? ResolveSource();

            if (source == null)
                return false;

            foreach (HandFinger finger in FingerFeatureSampler.AllFingers)
            {
                foreach (FingerFeature feature in FingerFeatureSampler.AllFeatures)
                {
                    if (source.TryGetValue(finger, feature, out _))
                        return true;
                }
            }

            return false;
        }
    }

    /// <summary>Per-constraint verdicts for one pose. Editor diagnostics; allocates.</summary>
    public void DebugEvaluate(
        HandPose pose,
        List<HandPose.ConstraintEvaluation> results,
        out float score,
        out bool constraintsMet,
        out bool handednessMet)
    {
        score = 0f;
        constraintsMet = false;
        handednessMet = true;

        results?.Clear();

        if (pose == null)
            return;

        // The inspector calls this outside the update loop.
        if (activeSource == null)
            activeSource = ResolveSource();

        if (activeSource != null)
        {
            handednessMet =
                pose.MatchesEitherHand ||
                activeSource.Handedness == pose.RecordedHandedness;
        }

        constraintsMet = Evaluate(
            pose,
            pose == activePose,
            results,
            out score,
            out _,
            out _
        );
    }

    /// <summary>How many enabled, readable constraints of a pose are currently satisfied.</summary>
    public void DebugMetCount(HandPose pose, out int met, out int tested)
    {
        met = 0;
        tested = 0;

        if (pose == null)
            return;

        if (activeSource == null)
            activeSource = ResolveSource();

        Evaluate(pose, pose == activePose, null, out _, out met, out tested);
    }

    /// <summary>Live scores for every candidate. Editor diagnostics; allocates, so do not call per frame in a build.</summary>
    public List<KeyValuePair<string, float>> DebugScores()
    {
        var results = new List<KeyValuePair<string, float>>();

        if (activeSource == null)
            activeSource = ResolveSource();

        if (activeSource == null)
            return results;

        foreach (HandPose pose in poses)
        {
            if (pose == null)
                continue;

            Evaluate(pose, pose == activePose, null, out float score, out _, out _);
            results.Add(new KeyValuePair<string, float>(pose.Key, score));
        }

        return results;
    }
}