using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

/// <summary>
/// A saved arrangement of the descendants of some root transform.
///
/// Everything is stored in LOCAL space, deliberately: the root (a tag anchor)
/// has its world pose rewritten every frame by the tracking solve, so world
/// values would be meaningless the moment the tag is seen from a new angle.
///
/// The layout records existence as well as placement. An entry carrying a
/// SpawnKey can be recreated from a <see cref="LayoutCatalogue"/> if it is
/// missing, which is what lets objects added at runtime survive.
///
/// Two things are never captured:
///   - anything marked <see cref="LayoutGenerated"/>, or anything beneath it.
///     Generated objects are derived data; storing them would duplicate state
///     that can then disagree with its source.
///   - the root itself. Its pose belongs to the tracker.
/// </summary>
[CreateAssetMenu(menuName = "AprilTag/Transform Layout", fileName = "TransformLayout")]
public sealed class TransformLayout : ScriptableObject
{
    [Serializable]
    public struct ComponentState
    {
        [Tooltip("Assembly-qualified type name.")]
        public string TypeName;

        public string Json;
    }

    [Serializable]
    public struct Entry
    {
        [Tooltip("Hierarchy path relative to the root, e.g. \"Panel/Label\".")]
        public string Path;

        [Tooltip("Path of the parent, relative to the root. Empty means the root itself.")]
        public string ParentPath;

        [Tooltip("Empty for hand-authored objects: those are placed, never created or destroyed.")]
        public string SpawnKey;

        public int SiblingIndex;

        public Vector3 LocalPosition;
        public Vector3 LocalEulerAngles;
        public Vector3 LocalScale;
        public bool ActiveSelf;

        public List<ComponentState> Components;

        public string Name
        {
            get
            {
                int slash = string.IsNullOrEmpty(Path) ? -1 : Path.LastIndexOf('/');
                return slash < 0 ? Path : Path.Substring(slash + 1);
            }
        }
    }

    [SerializeField]
    private List<Entry> entries = new List<Entry>();

    [SerializeField, Tooltip("Informational: when this layout was last captured.")]
    private string capturedAt;

    public IReadOnlyList<Entry> Entries => entries;
    public string CapturedAt => capturedAt;
    public int Count => entries.Count;

    /// <summary>
    /// Overwrite this layout with the current state of every non-generated
    /// descendant of <paramref name="root"/>.
    /// </summary>
    public void CaptureFrom(Transform root, bool includeInactive = true)
    {
        if (root == null)
        {
            Debug.LogWarning("[TransformLayout] No root to capture from.", this);
            return;
        }

        entries.Clear();

        var seen = new HashSet<string>(StringComparer.Ordinal);

        // Depth-first, parents before children — so apply can rely on the order.
        foreach (Transform child in root.GetComponentsInChildren<Transform>(includeInactive))
        {
            if (child == root)
                continue;

            if (LayoutGenerated.IsUnderGenerated(root, child))
                continue;

            string path = GetRelativePath(root, child);

            if (string.IsNullOrEmpty(path))
                continue;

            if (!seen.Add(path))
            {
                Debug.LogWarning(
                    $"[TransformLayout] Duplicate path \"{path}\" — only the first is stored.",
                    this
                );

                continue;
            }

            var member = child.GetComponent<LayoutMember>();

            entries.Add(new Entry
            {
                Path = path,
                ParentPath = GetRelativePath(root, child.parent) ?? string.Empty,
                SpawnKey = member != null ? member.SpawnKey : string.Empty,
                SiblingIndex = child.GetSiblingIndex(),
                LocalPosition = child.localPosition,
                LocalEulerAngles = child.localEulerAngles,
                LocalScale = child.localScale,
                ActiveSelf = child.gameObject.activeSelf,
                Components = CaptureComponents(child)
            });
        }

        capturedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        MarkDirty();
    }

    /// <summary>
    /// Rebuild the hierarchy under <paramref name="root"/> to match this layout:
    /// spawn what is missing, place everything, and (optionally) destroy layout
    /// members that are no longer part of it. Hand-authored objects are never
    /// created or destroyed, only moved.
    /// </summary>
    public int ApplyTo(
        Transform root,
        LayoutCatalogue catalogue = null,
        bool applyActiveState = true,
        bool pruneOrphans = true,
        bool recordUndo = false)
    {
        if (root == null)
            return 0;

        int applied = 0;
        var livePaths = new HashSet<string>(StringComparer.Ordinal);

        foreach (Entry entry in entries)
        {
            Transform child = root.Find(entry.Path);

            if (child == null)
            {
                child = Recreate(root, entry, catalogue, recordUndo);

                if (child == null)
                    continue;
            }

#if UNITY_EDITOR
            if (recordUndo)
            {
                UnityEditor.Undo.RecordObject(child, "Apply Transform Layout");

                if (applyActiveState)
                    UnityEditor.Undo.RecordObject(child.gameObject, "Apply Transform Layout");
            }
#endif

            child.localPosition = entry.LocalPosition;
            child.localEulerAngles = entry.LocalEulerAngles;
            child.localScale = entry.LocalScale;
            child.SetSiblingIndex(entry.SiblingIndex);

            if (applyActiveState)
                child.gameObject.SetActive(entry.ActiveSelf);

            RestoreComponents(child, entry);

#if UNITY_EDITOR
            if (recordUndo)
                UnityEditor.EditorUtility.SetDirty(child);
#endif

            livePaths.Add(entry.Path);
            applied++;
        }

        if (pruneOrphans)
            PruneOrphans(root, livePaths, recordUndo);

        return applied;
    }

    private Transform Recreate(Transform root, Entry entry, LayoutCatalogue catalogue, bool recordUndo)
    {
        if (string.IsNullOrEmpty(entry.SpawnKey))
        {
            Debug.LogWarning(
                $"[TransformLayout] \"{entry.Path}\" is missing and has no spawn key " +
                "(it was authored in the scene, not spawned). Skipped.",
                this
            );

            return null;
        }

        if (catalogue == null)
        {
            Debug.LogWarning(
                $"[TransformLayout] \"{entry.Path}\" needs respawning but no catalogue was supplied.",
                this
            );

            return null;
        }

        Transform parent = string.IsNullOrEmpty(entry.ParentPath)
            ? root
            : root.Find(entry.ParentPath);

        if (parent == null)
        {
            Debug.LogWarning(
                $"[TransformLayout] Parent \"{entry.ParentPath}\" of \"{entry.Path}\" does not exist.",
                this
            );

            return null;
        }

        GameObject instance = catalogue.Spawn(entry.SpawnKey, parent, entry.Name);

        if (instance == null)
            return null;

#if UNITY_EDITOR
        if (recordUndo)
            UnityEditor.Undo.RegisterCreatedObjectUndo(instance, "Apply Transform Layout");
#endif

        return instance.transform;
    }

    /// <summary>Destroy layout-spawned objects that the layout no longer mentions.</summary>
    private static void PruneOrphans(Transform root, HashSet<string> livePaths, bool recordUndo)
    {
        var doomed = new List<GameObject>();

        foreach (LayoutMember member in root.GetComponentsInChildren<LayoutMember>(true))
        {
            if (member.transform == root)
                continue;

            if (LayoutGenerated.IsUnderGenerated(root, member.transform))
                continue;

            string path = GetRelativePath(root, member.transform);

            if (path != null && !livePaths.Contains(path))
                doomed.Add(member.gameObject);
        }

        foreach (GameObject target in doomed)
        {
            if (target == null)
                continue;

#if UNITY_EDITOR
            if (recordUndo)
            {
                UnityEditor.Undo.DestroyObjectImmediate(target);
                continue;
            }
#endif
            if (Application.isPlaying)
                Destroy(target);
            else
                DestroyImmediate(target);
        }
    }

    private static List<ComponentState> CaptureComponents(Transform child)
    {
        List<ComponentState> states = null;

        foreach (Component component in child.GetComponents<Component>())
        {
            if (!(component is ILayoutState stateful))
                continue;

            if (states == null)
                states = new List<ComponentState>();

            states.Add(new ComponentState
            {
                TypeName = component.GetType().AssemblyQualifiedName,
                Json = stateful.CaptureState()
            });
        }

        return states;
    }

    private static void RestoreComponents(Transform child, Entry entry)
    {
        if (entry.Components == null)
            return;

        foreach (ComponentState state in entry.Components)
        {
            Type type = Type.GetType(state.TypeName);

            if (type == null)
            {
                Debug.LogWarning($"[TransformLayout] Unknown component type \"{state.TypeName}\".");
                continue;
            }

            Component component = child.GetComponent(type);

            if (component == null)
                component = child.gameObject.AddComponent(type);

            if (component is ILayoutState stateful)
                stateful.RestoreState(state.Json);
        }
    }

    public void Clear()
    {
        entries.Clear();
        capturedAt = string.Empty;
        MarkDirty();
    }

    public string ToJson(bool prettyPrint = true) => JsonUtility.ToJson(this, prettyPrint);

    /// <summary>Build a throwaway layout from JSON. Does not touch any asset on disk.</summary>
    public static TransformLayout FromJson(string json)
    {
        TransformLayout instance = CreateInstance<TransformLayout>();
        JsonUtility.FromJsonOverwrite(json, instance);
        return instance;
    }

    /// <summary>Path from root to child, slash separated. Empty for the root, null if not a descendant.</summary>
    public static string GetRelativePath(Transform root, Transform child)
    {
        if (root == null || child == null)
            return null;

        if (child == root)
            return string.Empty;

        var builder = new StringBuilder(child.name);
        Transform cursor = child.parent;

        while (cursor != null && cursor != root)
        {
            builder.Insert(0, '/').Insert(0, cursor.name);
            cursor = cursor.parent;
        }

        return cursor == root ? builder.ToString() : null;
    }

    private void MarkDirty()
    {
#if UNITY_EDITOR
        UnityEditor.EditorUtility.SetDirty(this);
#endif
    }
}