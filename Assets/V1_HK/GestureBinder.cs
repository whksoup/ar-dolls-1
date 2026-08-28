using UnityEngine;
using UnityEngine.Events;
using Oculus.Interaction;
using Oculus.Interaction.Input;

/// <summary>
/// Drop onto any pose prefab produced by the Hand Pose Selector Recorder, or
/// onto anything else exposing an ISelector or IActiveState.
///
/// Accepts either:
///   - an ISelector    (pinch, a recorded pose selector, a Sequence)
///   - an IActiveState (a raw ShapeRecognizerActiveState / ActiveStateGroup)
///
/// The ISelector path is edge-driven and cheaper; the IActiveState path is
/// polled, because IActiveState is a per-frame boolean with no events. Prefer
/// wrapping an active state in an ActiveStateSelector and using the first.
///
/// Filename must match the class name or Unity will not offer this in the
/// Add Component menu.
/// </summary>
public sealed class GestureBinder : MonoBehaviour
{
    [SerializeField, Tooltip("Key other systems bind to. Keep stable — renaming orphans subscribers.")]
    private string key;

    [Header("Source — fill exactly one")]
    [SerializeField, Interface(typeof(ISelector)), Optional]
    private UnityEngine.Object selectorSource;

    [SerializeField, Interface(typeof(IActiveState)), Optional]
    private UnityEngine.Object activeStateSource;

    [Header("Optional")]
    [SerializeField, Interface(typeof(IHand)), Optional]
    [Tooltip("Supplies the pose carried in GestureContext. Usually a HandRef.")]
    private UnityEngine.Object handSource;

    [Header("Local hooks")]
    [Tooltip("Inspector-wired listeners, in addition to the code bus.")]
    [SerializeField]
    private UnityEvent whenBegan;

    [SerializeField]
    private UnityEvent whenEnded;

    private ISelector selector;
    private IActiveState activeState;
    private IHand hand;

    private bool isActive;

    public string Key => key;

    private void Awake()
    {
        selector = selectorSource as ISelector;
        activeState = activeStateSource as IActiveState;
        hand = handSource as IHand;

        if (selector == null && activeState == null)
        {
            Debug.LogError(
                $"[GestureBinder] \"{key}\" has no ISelector or IActiveState source assigned.",
                this
            );
        }

        if (string.IsNullOrEmpty(key))
            Debug.LogError("[GestureBinder] Key is empty; nothing can subscribe.", this);
    }

    private void OnEnable()
    {
        if (selector != null)
        {
            selector.WhenSelected += OnSelected;
            selector.WhenUnselected += OnUnselected;
        }
    }

    private void OnDisable()
    {
        if (selector != null)
        {
            selector.WhenSelected -= OnSelected;
            selector.WhenUnselected -= OnUnselected;
        }

        // Never leave the bus believing a gesture is still held.
        if (isActive)
            End();
    }

    private void Update()
    {
        // Polled path only. When a selector is present it drives the edges.
        if (selector != null || activeState == null)
            return;

        if (activeState.Active == isActive)
            return;

        if (activeState.Active)
            Begin();
        else
            End();
    }

    private void OnSelected() => Begin();

    private void OnUnselected() => End();

    private void Begin()
    {
        Debug.Log(Key);
        isActive = true;
        GestureCommands.RaiseBegan(key, BuildContext());
        whenBegan?.Invoke();
    }

    private void End()
    {
        isActive = false;
        GestureCommands.RaiseEnded(key, BuildContext());
        whenEnded?.Invoke();
    }

    private GestureContext BuildContext()
    {
        Pose pose = Pose.identity;

        if (hand != null)
            hand.GetRootPose(out pose);

        return new GestureContext(
            key,
            hand?.Handedness ?? Handedness.Right,
            pose,
            Time.unscaledTime
        );
    }
}
