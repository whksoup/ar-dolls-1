using UnityEngine;

/// <summary>
/// Stamped onto anything a generator produced. Such objects are derived data:
/// never captured, never saved into the scene, and rebuilt on demand by whatever
/// owns them.
///
/// This marker — not a name suffix — is what the layout system keys off. A name
/// is a display string; provenance is a fact about the object, and encoding it in
/// the name means a rename silently changes behaviour.
/// </summary>
[DisallowMultipleComponent]
[AddComponentMenu("")]
public sealed class LayoutGenerated : MonoBehaviour
{
    [SerializeField]
    private Component owner;

    public Component Owner => owner;

    public static LayoutGenerated Stamp(GameObject target, Component owner)
    {
        LayoutGenerated marker = target.GetComponent<LayoutGenerated>();

        if (marker == null)
            marker = target.AddComponent<LayoutGenerated>();

        marker.owner = owner;

        target.hideFlags = HideFlags.DontSave | HideFlags.NotEditable;

        return marker;
    }

    /// <summary>True if this transform, or any ancestor below <paramref name="root"/>, is generated.</summary>
    public static bool IsUnderGenerated(Transform root, Transform child)
    {
        for (Transform cursor = child; cursor != null && cursor != root; cursor = cursor.parent)
        {
            if (cursor.GetComponent<LayoutGenerated>() != null)
                return true;
        }

        return false;
    }
}

/// <summary>
/// Stamped onto anything the layout system itself spawned. Distinguishes
/// "the layout owns this object's existence" from "someone authored this in the
/// scene and the layout only remembers where it sits".
///
/// Only members are respawned when missing, and only members are destroyed when
/// absent from the layout. Hand-authored children are never deleted.
/// </summary>
[DisallowMultipleComponent]
public sealed class LayoutMember : MonoBehaviour
{
    [SerializeField, Tooltip("Key into the LayoutCatalogue used to recreate this object.")]
    private string spawnKey;

    public string SpawnKey
    {
        get => spawnKey;
        set => spawnKey = value;
    }
}

/// <summary>
/// Implement on any component whose settings should ride along in a saved layout.
/// The layout stores the assembly-qualified type name plus whatever string you
/// hand back, and re-adds the component on apply.
///
/// JsonUtility over a small serializable struct is the path of least resistance.
/// </summary>
public interface ILayoutState
{
    string CaptureState();
    void RestoreState(string state);
}
