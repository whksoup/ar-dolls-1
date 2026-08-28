using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Events;
using Oculus.Interaction;
using Oculus.Interaction.Input;
using Oculus.Interaction.PoseDetection;

/// <summary>
/// Reads every selected finger/feature pair out of an
/// <see cref="IFingerFeatureStateProvider"/> once per frame and reports both the
/// quantised state name and the raw value behind it.
///
/// Two outputs, for two different questions:
///   - <see cref="StateChanged"/> and the console log answer "did the state I
///     care about ever fire, and when?"
///   - <see cref="ReadoutChanged"/> hands you a formatted block for an in-headset
///     text field, which answers "what is my hand doing *right now*", the thing
///     you cannot see from a log while wearing the device.
///
/// Polling is deliberate. The provider's proactive evaluation only refreshes
/// features that have already been touched, and it can be switched off entirely
/// on the provider — in which case a feature is evaluated only when queried.
/// Querying everything every frame is correct under both settings.
///
/// Filename must match the class name or Unity will not offer this in the
/// Add Component menu.
/// </summary>
public sealed class FingerFeatureStateDebugger : MonoBehaviour
{
    [Flags]
    public enum Fingers
    {
        None = 0,
        Thumb = 1 << 0,
        Index = 1 << 1,
        Middle = 1 << 2,
        Ring = 1 << 3,
        Pinky = 1 << 4,
        All = Thumb | Index | Middle | Ring | Pinky
    }

    [Flags]
    public enum Features
    {
        None = 0,
        Curl = 1 << 0,
        Flexion = 1 << 1,
        Abduction = 1 << 2,
        Opposition = 1 << 3,
        All = Curl | Flexion | Abduction | Opposition
    }

    /// <summary>Current reading for one finger/feature pair, as of the last Update.</summary>
    public readonly struct FingerFeatureSnapshot
    {
        public readonly HandFinger Finger;
        public readonly FingerFeature Feature;
        public readonly string State;
        public readonly float Value;

        /// <summary>False if the hand is untracked or this pair is not defined (e.g. thumb abduction).</summary>
        public readonly bool HasValue;

        public FingerFeatureSnapshot(HandFinger finger, FingerFeature feature, string state, float value, bool hasValue)
        {
            Finger = finger;
            Feature = feature;
            State = state;
            Value = value;
            HasValue = hasValue;
        }
    }

    /// <summary>Finger, feature, the state it just entered, and the value that caused it.</summary>
    public readonly struct FingerFeatureChange
    {
        public readonly HandFinger Finger;
        public readonly FingerFeature Feature;
        public readonly string PreviousState;
        public readonly string CurrentState;
        public readonly float Value;

        public FingerFeatureChange(
            HandFinger finger,
            FingerFeature feature,
            string previousState,
            string currentState,
            float value)
        {
            Finger = finger;
            Feature = feature;
            PreviousState = previousState;
            CurrentState = currentState;
            Value = value;
        }

        public override string ToString() =>
            $"{Finger}.{Feature}: {Describe(PreviousState)} -> {Describe(CurrentState)} ({Value:F2})";

        private static string Describe(string state) =>
            string.IsNullOrEmpty(state) ? "<none>" : state;
    }

    [Serializable]
    public sealed class ChangeEvent : UnityEvent<string> { }

    [Header("Source")]
    [SerializeField, Interface(typeof(IFingerFeatureStateProvider))]
    [Tooltip("Your FingerFeatureStateProvider, or anything else implementing the interface.")]
    private UnityEngine.Object stateProviderSource;

    [Header("What to watch")]
    [SerializeField]
    private Fingers fingers = Fingers.All;

    [SerializeField]
    private Features features = Features.Curl | Features.Flexion;

    [Header("Console")]
    [Tooltip("Log a line whenever a watched pair changes state.")]
    [SerializeField]
    private bool logChanges = true;

    [Header("Live readout")]
    [Tooltip(
        "Formatted every frame and pushed to the event below. Wire it to a " +
        "TMP_Text.SetText(string) so you can read it inside the headset."
    )]
    [SerializeField]
    private bool buildReadout = true;

    [Tooltip("Include the raw feature value next to each state name.")]
    [SerializeField]
    private bool readoutIncludesValues = true;

    [Header("Events")]
    public UnityEvent<FingerFeatureChange> StateChanged;

    [Tooltip("Fires only when the formatted text actually differs from last frame.")]
    public ChangeEvent ReadoutChanged;

    private IFingerFeatureStateProvider stateProvider;

    private readonly Dictionary<int, string> lastState = new Dictionary<int, string>();
    private readonly Dictionary<int, FingerFeatureSnapshot> snapshots = new Dictionary<int, FingerFeatureSnapshot>();
    private readonly StringBuilder builder = new StringBuilder(512);

    private string readout = string.Empty;

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

    /// <summary>Most recent formatted snapshot. Empty until the first poll.</summary>
    public string Readout => readout;

    /// <summary>True once at least one Update has run and populated snapshots.</summary>
    public bool HasPolled { get; private set; }

    public bool IsFingerWatched(HandFinger finger) => IsSelected(finger);

    public bool IsFeatureWatched(FingerFeature feature) => IsSelected(feature);

    /// <summary>Live reading for one pair, as of the last Update. False if never polled or undefined.</summary>
    public bool TryGetSnapshot(HandFinger finger, FingerFeature feature, out FingerFeatureSnapshot snapshot) =>
        snapshots.TryGetValue(Key(finger, feature), out snapshot);

    private void Awake()
    {
        stateProvider = stateProviderSource as IFingerFeatureStateProvider;

        if (stateProvider == null)
        {
            Debug.LogError(
                stateProviderSource == null
                    ? "[FingerFeatureStateDebugger] No state provider assigned."
                    : $"[FingerFeatureStateDebugger] {stateProviderSource.GetType().Name} " +
                      "does not implement IFingerFeatureStateProvider.",
                this
            );
        }
    }

    private void OnDisable()
    {
        // Otherwise the first poll after re-enabling reports no change for a
        // state that may well have moved while we were not looking.
        lastState.Clear();
        snapshots.Clear();
        HasPolled = false;
    }

    private void Update()
    {
        if (stateProvider == null)
            return;

        HasPolled = true;

        if (buildReadout)
            builder.Clear();

        foreach (HandFinger finger in AllFingers)
        {
            if (!IsSelected(finger))
                continue;

            bool wroteFinger = false;

            foreach (FingerFeature feature in AllFeatures)
            {
                if (!IsSelected(feature))
                    continue;

                float? rawValue = stateProvider.GetFeatureValue(finger, feature);

                // Null means the hand is not tracked, or this pair is not
                // defined for this finger — abduction has no meaning on the
                // pinky, opposition has none on the thumb.
                if (!rawValue.HasValue || float.IsNaN(rawValue.Value))
                    continue;

                stateProvider.GetCurrentState(finger, feature, out string state);

                snapshots[Key(finger, feature)] =
                    new FingerFeatureSnapshot(finger, feature, state, rawValue.Value, true);

                Report(finger, feature, state, rawValue.Value);

                if (!buildReadout)
                    continue;

                if (!wroteFinger)
                {
                    builder.Append(finger).Append('\n');
                    wroteFinger = true;
                }

                builder.Append("  ").Append(feature).Append(": ");
                builder.Append(string.IsNullOrEmpty(state) ? "-" : state);

                if (readoutIncludesValues)
                    builder.Append("  (").Append(rawValue.Value.ToString("F2")).Append(')');

                builder.Append('\n');
            }
        }

        if (!buildReadout)
            return;

        string updated = builder.ToString();

        if (updated == readout)
            return;

        readout = updated;
        ReadoutChanged?.Invoke(readout);
    }

    private void Report(HandFinger finger, FingerFeature feature, string state, float value)
    {
        int key = Key(finger, feature);

        lastState.TryGetValue(key, out string previous);

        if (string.Equals(previous, state, StringComparison.Ordinal))
            return;

        lastState[key] = state;

        var change = new FingerFeatureChange(finger, feature, previous, state, value);

        if (logChanges)
            Debug.Log($"[FingerFeature] {change}", this);

        StateChanged?.Invoke(change);
    }

    /// <summary>
    /// True while a named state is held. Thin pass-through, provided so a
    /// consumer can ask a one-off question without touching the SDK types twice.
    /// </summary>
    public bool IsStateActive(HandFinger finger, FingerFeature feature, string stateId) =>
        stateProvider != null &&
        stateProvider.IsStateActive(finger, feature, FeatureStateActiveMode.Is, stateId);

    /// <summary>Raw value, or NaN when the hand is untracked or the pair is undefined.</summary>
    public float GetValue(HandFinger finger, FingerFeature feature) =>
        stateProvider?.GetFeatureValue(finger, feature) ?? float.NaN;

    [ContextMenu("Log Snapshot")]
    private void LogSnapshot()
    {
        if (stateProvider == null)
        {
            Debug.LogWarning("[FingerFeatureStateDebugger] No state provider.", this);
            return;
        }

        var snapshot = new StringBuilder("Finger feature snapshot\n");

        foreach (HandFinger finger in AllFingers)
        {
            foreach (FingerFeature feature in AllFeatures)
            {
                float? value = stateProvider.GetFeatureValue(finger, feature);

                if (!value.HasValue || float.IsNaN(value.Value))
                    continue;

                stateProvider.GetCurrentState(finger, feature, out string state);

                snapshot
                    .Append(finger).Append('.').Append(feature).Append(" = ")
                    .Append(string.IsNullOrEmpty(state) ? "<none>" : state)
                    .Append("  (").Append(value.Value.ToString("F3")).Append(")\n");
            }
        }

        Debug.Log(snapshot.ToString(), this);
    }

    private bool IsSelected(HandFinger finger) =>
        (fingers & (Fingers)(1 << (int)finger)) != 0;

    private bool IsSelected(FingerFeature feature) =>
        (features & (Features)(1 << (int)feature)) != 0;

    private static int Key(HandFinger finger, FingerFeature feature) =>
        (int)finger * AllFeatures.Length + (int)feature;
}