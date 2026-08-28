using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Events;
using Oculus.Interaction;
using Oculus.Interaction.Input;
using Oculus.Interaction.PoseDetection;

/// <summary>
/// Records a <see cref="HandPose"/> by sampling every finger feature across a
/// window rather than capturing a single frame.
///
/// A single frame yields zero-width ranges that can never be matched again. The
/// spread across a held pose is the useful signal: it tells you both where the
/// pose sits and how precisely the wearer can reproduce it, and the second of
/// those is what sets a sensible tolerance.
///
/// Constraints are auto-enabled only where the value was held tightly relative
/// to the feature's nominal span. Enabling all eighteen would produce a pose
/// that never fires, because noise in any one dimension drops the match.
///
/// Filename must match the class name or Unity will not offer this in the
/// Add Component menu.
/// </summary>
[DefaultExecutionOrder(100)] // after the debugger's Update, so its snapshots are this frame's
public sealed class HandPoseRecorder : MonoBehaviour
{
    private struct Accumulator
    {
        public int Count;
        public double Sum;
        public double SumOfSquares;
        public float Min;
        public float Max;

        public void Add(float value)
        {
            if (Count == 0)
            {
                Min = value;
                Max = value;
            }
            else
            {
                if (value < Min) Min = value;
                if (value > Max) Max = value;
            }

            Count++;
            Sum += value;
            SumOfSquares += (double)value * value;
        }

        public float Mean => Count == 0 ? 0f : (float)(Sum / Count);

        public float StandardDeviation
        {
            get
            {
                if (Count < 2)
                    return 0f;

                double mean = Sum / Count;
                double variance = (SumOfSquares / Count) - (mean * mean);

                return variance <= 0d ? 0f : (float)Math.Sqrt(variance);
            }
        }
    }

    public enum Phase
    {
        Idle,
        LeadIn,
        Recording
    }

    public enum SourceMode
    {
        /// <summary>Try the hand; fall back to the debugger if it yields nothing readable.</summary>
        Auto,

        /// <summary>Read FingerShapes directly off the IHand.</summary>
        Hand,

        /// <summary>Read the values a FingerFeatureStateDebugger already polled this frame.</summary>
        Debugger
    }

    [Header("Source")]
    [SerializeField]
    [Tooltip(
        "Auto probes the hand once at record time and falls back to the debugger " +
        "if nothing is readable. Pin it to one or the other once you know which works."
    )]
    private SourceMode sourceMode = SourceMode.Auto;

    [SerializeField, Interface(typeof(IHand))]
    [Tooltip("Usually a HandRef. The pose is recorded from whichever hand this resolves to.")]
    private UnityEngine.Object handSource;

    [SerializeField, Optional]
    [Tooltip(
        "Fallback reader. Must be watching all fingers and all features, or the " +
        "constraints it cannot see will be silently missing from the recording. " +
        "Still worth assigning a hand alongside it, for handedness and confidence."
    )]
    private FingerFeatureStateDebugger debuggerSource;

    [Header("Target")]
    [SerializeField, Tooltip("Asset overwritten on save. Leave null to write JSON only.")]
    private HandPose target;

    [SerializeField, Tooltip("Also write a .json next to the persistent data path, for on-device recording.")]
    private bool writeJson = true;

    [SerializeField, Tooltip("Filename stem for the JSON. Defaults to the target asset's key.")]
    private string jsonFileName = string.Empty;

    [Header("Timing")]
    [SerializeField, Tooltip("Seconds to get into the pose before sampling starts.")]
    private float leadInSeconds = 1.5f;

    [SerializeField, Tooltip("Seconds of sampling.")]
    private float recordSeconds = 5f;

    [Header("Range derivation")]
    [SerializeField, Range(0f, 5f)]
    [Tooltip("Range is the observed spread padded by this many standard deviations.")]
    private float paddingSigma = 2f;

    [SerializeField, Range(0f, 0.5f)]
    [Tooltip(
        "Auto-enable a constraint when its observed spread is under this " +
        "fraction of the feature's nominal span. Lower keeps only the tightest " +
        "dimensions; higher constrains more and risks a pose that never fires."
    )]
    private float autoEnableSpreadFraction = 0.18f;

    [SerializeField]
    [Tooltip("Discard samples taken while tracking confidence was low.")]
    private bool requireHighConfidence = true;

    [Header("Events")]
    public UnityEvent RecordingStarted;
    public UnityEvent<float> RecordingProgress;
    public UnityEvent<HandPose> RecordingFinished;

    private IHand hand;
    private Phase phase = Phase.Idle;
    private float phaseEndTime;
    private int rejectedSamples;
    private bool readingFromDebugger;
    private IFingerValueSource valueSource;

    /// <summary>Which reader the current or most recent recording used. For the inspector.</summary>
    public string SourceDescription =>
        readingFromDebugger ? "FingerFeatureStateDebugger" : "IHand (FingerShapes)";

    /// <summary>True when this recorder has something it can actually sample from right now.</summary>
    public bool CanRecord =>
        (handSource as IHand) != null || debuggerSource != null;

    private readonly Dictionary<int, Accumulator> accumulators = new Dictionary<int, Accumulator>();

    public Phase CurrentPhase => phase;
    public HandPose Target => target;

    /// <summary>0..1 through the current phase. 0 while idle.</summary>
    public float Progress
    {
        get
        {
            if (phase == Phase.Idle)
                return 0f;

            float duration = phase == Phase.LeadIn ? leadInSeconds : recordSeconds;

            if (duration <= 0f)
                return 1f;

            return Mathf.Clamp01(1f - ((phaseEndTime - Time.unscaledTime) / duration));
        }
    }

    /// <summary>Samples collected so far for the first enabled-looking pair. Rough progress signal.</summary>
    public int SampleCount
    {
        get
        {
            foreach (KeyValuePair<int, Accumulator> entry in accumulators)
                return entry.Value.Count;

            return 0;
        }
    }

    public int RejectedSamples => rejectedSamples;

    private void Awake()
    {
        hand = handSource as IHand;

        if (hand == null && handSource != null)
        {
            Debug.LogError(
                $"[HandPoseRecorder] {handSource.GetType().Name} does not implement IHand.",
                this
            );
        }

        if (hand == null && debuggerSource == null)
            Debug.LogError("[HandPoseRecorder] No hand and no debugger assigned; nothing to sample.", this);
    }

    /// <summary>Begin the lead-in, then sample. Safe to call while already recording — restarts.</summary>
    [ContextMenu("Start Recording")]
    public void StartRecording()
    {
        if (hand == null)
            hand = handSource as IHand;

        if (!ResolveSource())
            return;

        accumulators.Clear();
        rejectedSamples = 0;

        phase = leadInSeconds > 0f ? Phase.LeadIn : Phase.Recording;
        phaseEndTime = Time.unscaledTime + (leadInSeconds > 0f ? leadInSeconds : recordSeconds);

        if (phase == Phase.Recording)
            RecordingStarted?.Invoke();
    }

    [ContextMenu("Cancel Recording")]
    public void CancelRecording()
    {
        phase = Phase.Idle;
        accumulators.Clear();
    }

    private void Update()
    {
        if (phase == Phase.Idle)
            return;

        RecordingProgress?.Invoke(Progress);

        if (phase == Phase.LeadIn)
        {
            if (Time.unscaledTime < phaseEndTime)
                return;

            phase = Phase.Recording;
            phaseEndTime = Time.unscaledTime + recordSeconds;
            RecordingStarted?.Invoke();
            return;
        }

        Sample();

        if (Time.unscaledTime >= phaseEndTime)
            Finish();
    }

    /// <summary>
    /// Decides which reader this recording will use, and complains loudly enough
    /// that a silently empty recording is not possible.
    /// </summary>
    private bool ResolveSource()
    {
        switch (sourceMode)
        {
            case SourceMode.Hand:
                readingFromDebugger = false;
                break;

            case SourceMode.Debugger:
                readingFromDebugger = true;
                break;

            default:
                // Probe: a hand that resolves but reads nothing is the failure
                // mode the fallback exists for, so test the read rather than
                // the reference.
                readingFromDebugger = hand == null || !HandReadsAnything();
                break;
        }

        if (!readingFromDebugger)
        {
            if (hand != null)
            {
                valueSource = new HandValueSource(hand);
                return true;
            }

            Debug.LogError("[HandPoseRecorder] Hand mode selected but no IHand resolved.", this);
            return false;
        }

        if (debuggerSource == null)
        {
            Debug.LogError(
                "[HandPoseRecorder] Nothing readable from the hand and no " +
                "FingerFeatureStateDebugger assigned to fall back to.",
                this
            );

            return false;
        }

        if (!debuggerSource.isActiveAndEnabled)
        {
            Debug.LogError("[HandPoseRecorder] The assigned debugger is disabled; it will not poll.", this);
            return false;
        }

        WarnIfDebuggerIsNarrow();

        valueSource = new DebuggerValueSource(debuggerSource, hand);

        if (sourceMode == SourceMode.Auto)
            Debug.LogWarning("[HandPoseRecorder] Hand read nothing; falling back to the debugger.", this);

        return true;
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

    /// <summary>
    /// The debugger only stores what it was told to watch. Anything unwatched
    /// produces no constraint at all, which looks identical to a pose that
    /// genuinely does not care about that finger.
    /// </summary>
    private void WarnIfDebuggerIsNarrow()
    {
        var missing = new List<string>();

        foreach (HandFinger finger in FingerFeatureSampler.AllFingers)
        {
            if (!debuggerSource.IsFingerWatched(finger))
                missing.Add(finger.ToString());
        }

        foreach (FingerFeature feature in FingerFeatureSampler.AllFeatures)
        {
            if (!debuggerSource.IsFeatureWatched(feature))
                missing.Add(feature.ToString());
        }

        if (missing.Count == 0)
            return;

        Debug.LogWarning(
            "[HandPoseRecorder] The debugger is not watching " +
            string.Join(", ", missing) +
            ". Those will be absent from the recording — set it to All/All before recording.",
            this
        );
    }

    /// <summary>Read one pair through whichever source was resolved.</summary>
    private bool TrySampleValue(HandFinger finger, FingerFeature feature, out float value)
    {
        value = 0f;
        return valueSource != null && valueSource.TryGetValue(finger, feature, out value);
    }

    private void Sample()
    {
        // Only the hand knows about tracking confidence. Reading through the
        // debugger without one means accepting whatever it polled.
        if (requireHighConfidence && hand != null && !hand.IsHighConfidence)
        {
            rejectedSamples++;
            return;
        }

        foreach (HandFinger finger in FingerFeatureSampler.AllFingers)
        {
            foreach (FingerFeature feature in FingerFeatureSampler.AllFeatures)
            {
                if (!TrySampleValue(finger, feature, out float value))
                    continue;

                int key = Key(finger, feature);

                accumulators.TryGetValue(key, out Accumulator accumulator);
                accumulator.Add(value);
                accumulators[key] = accumulator;
            }
        }
    }

    private void Finish()
    {
        phase = Phase.Idle;

        if (accumulators.Count == 0)
        {
            Debug.LogWarning(
                $"[HandPoseRecorder] No usable samples ({rejectedSamples} rejected for low " +
                "confidence). Nothing was written.",
                this
            );

            return;
        }

        var recorded = new List<HandPose.Constraint>();
        int enabled = 0;

        foreach (HandFinger finger in FingerFeatureSampler.AllFingers)
        {
            foreach (FingerFeature feature in FingerFeatureSampler.AllFeatures)
            {
                int key = Key(finger, feature);

                if (!accumulators.TryGetValue(key, out Accumulator accumulator) || accumulator.Count == 0)
                    continue;

                float sigma = accumulator.StandardDeviation;
                float span = FingerFeatureSampler.NominalSpan(feature);
                float margin = Mathf.Max(FingerFeatureSampler.MinimumMargin(feature), sigma * paddingSigma);

                float observedSpread = accumulator.Max - accumulator.Min;
                bool tight = (observedSpread / span) <= autoEnableSpreadFraction;

                if (tight)
                    enabled++;

                recorded.Add(new HandPose.Constraint
                {
                    Enabled = tight,
                    Finger = finger,
                    Feature = feature,
                    Min = accumulator.Min - margin,
                    Max = accumulator.Max + margin,
                    Mean = accumulator.Mean,
                    StandardDeviation = sigma,
                    ObservedMin = accumulator.Min,
                    ObservedMax = accumulator.Max,
                    SampleCount = accumulator.Count
                });
            }
        }

        if (target != null)
        {
            Handedness handedness = hand?.Handedness ?? Handedness.Right;

            if (hand == null)
            {
                Debug.LogWarning(
                    "[HandPoseRecorder] No hand reference, so handedness was guessed as Right. " +
                    "Check it on the asset before relying on it.",
                    this
                );
            }

            target.SetConstraints(recorded, handedness);

            Debug.Log(
                $"[HandPoseRecorder] Recorded \"{target.Key}\": {enabled} of {recorded.Count} " +
                $"constraints enabled, {SampleCount} samples, {rejectedSamples} rejected.",
                target
            );

            if (writeJson)
                WriteJson(target);

            RecordingFinished?.Invoke(target);
        }
        else
        {
            Debug.LogWarning(
                "[HandPoseRecorder] No target asset assigned; recording discarded.",
                this
            );
        }

        accumulators.Clear();
    }

    private void WriteJson(HandPose pose)
    {
        string stem = string.IsNullOrEmpty(jsonFileName)
            ? (string.IsNullOrEmpty(pose.Key) ? pose.name : pose.Key)
            : jsonFileName;

        string path = Path.Combine(Application.persistentDataPath, stem + ".json");

        try
        {
            File.WriteAllText(path, pose.ToJson());
            Debug.Log($"[HandPoseRecorder] Wrote {path}", this);
        }
        catch (Exception exception)
        {
            Debug.LogError($"[HandPoseRecorder] Could not write {path}: {exception.Message}", this);
        }
    }

    /// <summary>Load a previously recorded JSON back over the target asset.</summary>
    [ContextMenu("Load JSON Into Target")]
    public void LoadJsonIntoTarget()
    {
        if (target == null)
        {
            Debug.LogWarning("[HandPoseRecorder] No target to load into.", this);
            return;
        }

        string stem = string.IsNullOrEmpty(jsonFileName)
            ? (string.IsNullOrEmpty(target.Key) ? target.name : target.Key)
            : jsonFileName;

        string path = Path.Combine(Application.persistentDataPath, stem + ".json");

        if (!File.Exists(path))
        {
            Debug.LogWarning($"[HandPoseRecorder] No file at {path}", this);
            return;
        }

        target.OverwriteFromJson(File.ReadAllText(path));
        Debug.Log($"[HandPoseRecorder] Loaded {path} into {target.name}", target);
    }

    private static int Key(HandFinger finger, FingerFeature feature) =>
        (int)finger * FingerFeatureSampler.AllFeatures.Length + (int)feature;
}