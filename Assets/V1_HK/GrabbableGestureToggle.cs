using System.Collections.Generic;
using UnityEngine;
using Oculus.Interaction;

/// <summary>
/// Turns another object's grabbability on and off in response to a gesture key
/// published by <see cref="GestureBinder"/>.
///
/// Sits beside the binder on the ISelector object, and points at the object
/// being controlled. Nothing points back: the gesture prefab knows only its key.
///
/// The lever is the *interactable* component, not <c>Grabbable</c>. Grabbable is
/// the sink that receives pose updates; disabling it mid-grab leaves an
/// interactor holding a dead reference. Disabling an interactable is the
/// supported path — the SDK unselects any interactor first, so a held object is
/// released rather than stranded.
///
/// Discovery is by <see cref="IInteractableView"/>, which every interactable
/// implements. That is deliberately broad: if the target also carries poke or
/// ray interactables you did not want touched, fill <see cref="interactables"/>
/// explicitly and auto-discovery is skipped entirely.
/// </summary>
public sealed class GrabbableGestureToggle : MonoBehaviour
{
    public enum Mode
    {
        /// <summary>Each gesture flips the current state. Ignores the end edge.</summary>
        Toggle,

        /// <summary>Grabbable only while the gesture is held.</summary>
        HoldToEnable,

        /// <summary>Grabbable except while the gesture is held — a hold-to-lock.</summary>
        HoldToDisable
    }

    [Header("Gesture")]
    [SerializeField, Tooltip("Must match the Key on the GestureBinder.")]
    private string listenFor = "Pinch_Right";

    [SerializeField, Tooltip("Ignore the gesture unless it came from this hand. Any = no filter.")]
    private HandFilter acceptFrom = HandFilter.Any;

    [Header("Target")]
    [SerializeField, Tooltip("Object whose grabbability is being switched. Its children are included.")]
    private GameObject target;

    [SerializeField, Tooltip(
        "Optional. Leave empty to auto-discover every interactable under the " +
        "target. Fill it to control exactly which ones are switched."
    )]
    private MonoBehaviour[] interactables;

    [Header("Behaviour")]
    [SerializeField]
    private Mode mode = Mode.Toggle;

    [SerializeField, Tooltip("State applied on enable, before any gesture arrives.")]
    private bool grabbableAtStart = true;

    private readonly List<MonoBehaviour> resolved = new List<MonoBehaviour>();

    private bool isGrabbable;

    public enum HandFilter
    {
        Any,
        Left,
        Right
    }

    /// <summary>Current state. Setting this drives the target directly.</summary>
    public bool IsGrabbable
    {
        get => isGrabbable;
        set => Apply(value);
    }

    private void Awake()
    {
        Collect();
    }

    private void OnEnable()
    {
        Apply(mode == Mode.HoldToEnable ? false : grabbableAtStart);

        GestureCommands.SubscribeBegan(listenFor, OnBegan);
        GestureCommands.SubscribeEnded(listenFor, OnEnded);
    }

    private void OnDisable()
    {
        // Must mirror OnEnable exactly. GestureCommands is static, so a missed
        // unsubscribe outlives this object and will fire into a dead reference.
        GestureCommands.UnsubscribeBegan(listenFor, OnBegan);
        GestureCommands.UnsubscribeEnded(listenFor, OnEnded);

        // Never leave the target stuck in a transient hold state.
        if (mode != Mode.Toggle)
            Apply(RestingState());
    }

    /// <summary>Point at a different object at runtime. Re-applies the current state.</summary>
    public void Retarget(GameObject newTarget)
    {
        target = newTarget;
        interactables = null;
        Collect();
        Apply(isGrabbable);
    }

    private void OnBegan(GestureContext context)
    {
        if (!Accepts(context))
            return;

        switch (mode)
        {
            case Mode.Toggle:
                Apply(!isGrabbable);
                break;

            case Mode.HoldToEnable:
                Apply(true);
                break;

            case Mode.HoldToDisable:
                Apply(false);
                break;
        }
    }

    private void OnEnded(GestureContext context)
    {
        if (mode == Mode.Toggle || !Accepts(context))
            return;

        Apply(RestingState());
    }

    private bool RestingState() => mode != Mode.HoldToEnable;

    private bool Accepts(in GestureContext context)
    {
        switch (acceptFrom)
        {
            case HandFilter.Left:
                return context.Handedness == Oculus.Interaction.Input.Handedness.Left;

            case HandFilter.Right:
                return context.Handedness == Oculus.Interaction.Input.Handedness.Right;

            default:
                return true;
        }
    }

    private void Collect()
    {
        resolved.Clear();

        if (interactables != null && interactables.Length > 0)
        {
            foreach (MonoBehaviour explicitly in interactables)
            {
                if (explicitly != null)
                    resolved.Add(explicitly);
            }

            return;
        }

        if (target == null)
        {
            Debug.LogError("[GrabbableGestureToggle] No target assigned.", this);
            return;
        }

        foreach (MonoBehaviour behaviour in target.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (behaviour is IInteractableView)
                resolved.Add(behaviour);
        }

        if (resolved.Count == 0)
        {
            Debug.LogWarning(
                $"[GrabbableGestureToggle] Found no interactables under \"{target.name}\".",
                this
            );
        }
    }

    private void Apply(bool grabbable)
    {
        isGrabbable = grabbable;

        for (int index = 0; index < resolved.Count; index++)
        {
            MonoBehaviour behaviour = resolved[index];

            if (behaviour == null)
                continue;

            // Disabling drives Interactable.Disable(), which unselects any
            // interactor currently holding this — a grabbed object is released,
            // not frozen in the hand.
            if (behaviour.enabled != grabbable)
                behaviour.enabled = grabbable;
        }
    }
}
