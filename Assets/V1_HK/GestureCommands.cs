using System;
using System.Collections.Generic;
using UnityEngine;
using Oculus.Interaction.Input;

/// <summary>
/// A named, scene-global routing point between Interaction SDK gesture sources
/// and ordinary game code.
///
/// The SDK's own boundary is the *UnityEventWrapper family, which requires the
/// listener to be wired into the emitting prefab's inspector. That inverts the
/// dependency the wrong way for a control suite: the gesture prefab ends up
/// knowing about every consumer. Here the prefab only publishes a key, and
/// consumers bind to that key from wherever they happen to live.
///
/// Not a MonoBehaviour, so this file has no filename constraint — but the
/// MonoBehaviours that use it do. See GestureBinder.cs.
/// </summary>
public static class GestureCommands
{
    private static readonly Dictionary<string, Action<GestureContext>> Began =
        new Dictionary<string, Action<GestureContext>>(StringComparer.Ordinal);

    private static readonly Dictionary<string, Action<GestureContext>> Ended =
        new Dictionary<string, Action<GestureContext>>(StringComparer.Ordinal);

    private static readonly HashSet<string> ActiveKeys =
        new HashSet<string>(StringComparer.Ordinal);

    public static bool IsActive(string key) => ActiveKeys.Contains(key);

    public static void SubscribeBegan(string key, Action<GestureContext> handler)
    {
        if (string.IsNullOrEmpty(key) || handler == null)
            return;

        Began.TryGetValue(key, out Action<GestureContext> existing);
        Began[key] = existing + handler;
    }

    public static void UnsubscribeBegan(string key, Action<GestureContext> handler)
    {
        if (string.IsNullOrEmpty(key) || handler == null)
            return;

        if (Began.TryGetValue(key, out Action<GestureContext> existing))
            Began[key] = existing - handler;
    }

    public static void SubscribeEnded(string key, Action<GestureContext> handler)
    {
        if (string.IsNullOrEmpty(key) || handler == null)
            return;

        Ended.TryGetValue(key, out Action<GestureContext> existing);
        Ended[key] = existing + handler;
    }

    public static void UnsubscribeEnded(string key, Action<GestureContext> handler)
    {
        if (string.IsNullOrEmpty(key) || handler == null)
            return;

        if (Ended.TryGetValue(key, out Action<GestureContext> existing))
            Ended[key] = existing - handler;
    }

    internal static void RaiseBegan(string key, in GestureContext context)
    {
        if (string.IsNullOrEmpty(key))
            return;

        ActiveKeys.Add(key);

        if (Began.TryGetValue(key, out Action<GestureContext> handler))
            handler?.Invoke(context);
    }

    internal static void RaiseEnded(string key, in GestureContext context)
    {
        if (string.IsNullOrEmpty(key))
            return;

        ActiveKeys.Remove(key);

        if (Ended.TryGetValue(key, out Action<GestureContext> handler))
            handler?.Invoke(context);
    }

    /// <summary>Domain reload is often disabled in Quest projects; clear explicitly.</summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        Began.Clear();
        Ended.Clear();
        ActiveKeys.Clear();
    }
}

/// <summary>Which hand fired, and where it was, so a handler can act positionally.</summary>
public readonly struct GestureContext
{
    public readonly string Key;
    public readonly Handedness Handedness;
    public readonly Pose HandPose;
    public readonly float Time;

    public GestureContext(string key, Handedness handedness, Pose handPose, float time)
    {
        Key = key;
        Handedness = handedness;
        HandPose = handPose;
        Time = time;
    }
}
