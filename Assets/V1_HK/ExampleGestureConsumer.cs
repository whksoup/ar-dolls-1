using UnityEngine;

/// <summary>
/// Template for anything that wants to react to a gesture. Lives anywhere in
/// the scene, knows nothing about the gesture prefabs, and is not referenced
/// by them.
/// </summary>
public sealed class ExampleGestureConsumer : MonoBehaviour
{
    [SerializeField]
    private string listenFor = "Pinch_Right";

    private void OnEnable()
    {
        GestureCommands.SubscribeBegan(listenFor, OnBegan);
        GestureCommands.SubscribeEnded(listenFor, OnEnded);
    }

    private void OnDisable()
    {
        // Must mirror OnEnable exactly. GestureCommands is static, so a missed
        // unsubscribe outlives this object and will fire into a dead reference.
        GestureCommands.UnsubscribeBegan(listenFor, OnBegan);
        GestureCommands.UnsubscribeEnded(listenFor, OnEnded);
    }

    private void OnBegan(GestureContext context)
    {
        Debug.Log(
            $"{context.Key} began — {context.Handedness} at {context.HandPose.position}",
            this
        );
    }

    private void OnEnded(GestureContext context)
    {
        Debug.Log($"{context.Key} ended", this);
    }
}
