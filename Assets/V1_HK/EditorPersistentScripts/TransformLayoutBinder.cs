using System.IO;
using UnityEngine;

/// <summary>
/// Saves and restores the arrangement of everything parented under
/// <see cref="root"/> (typically the tag anchor), including objects added at
/// runtime.
///
/// Two persistence paths, because they behave differently:
///
///   Editor  — the layout is written straight into the ScriptableObject asset.
///             Assets are NOT rolled back when Play Mode exits, so a capture
///             made while playing survives, and "Apply Layout" then arranges
///             the scene in edit mode without entering play at all.
///
///   Build   — a ScriptableObject on device is a read-only copy baked into the
///             player; writes to it evaporate on quit. So a runtime capture is
///             mirrored to JSON under Application.persistentDataPath, and that
///             file takes precedence at load if present.
///
/// Ownership rules, which are what keep this predictable:
///   - Objects spawned through <see cref="Spawn"/> carry a LayoutMember and a
///     catalogue key. The layout owns their existence: it recreates them when
///     missing and deletes them when they drop out of the layout.
///   - Objects you authored in the scene are placed but never created or
///     destroyed. The layout only remembers where they sit.
///   - Objects marked LayoutGenerated (mirror clones and the like) are ignored
///     entirely. They are rebuilt by whatever generated them.
///
/// Note on the reference tag: if AprilTagAnchor.referenceTag is a descendant of
/// root, including it here means this layout re-authors how the physical tag is
/// assumed to be mounted. Bake the reference pose first, or keep that child out
/// of root.
/// </summary>
[ExecuteAlways]
[DisallowMultipleComponent]
public sealed class TransformLayoutBinder : MonoBehaviour
{
    [Header("Binding")]
    [Tooltip("Parent whose children are managed. Defaults to this transform.")]
    [SerializeField]
    private Transform root;

    [SerializeField]
    private TransformLayout layout;

    [Tooltip("Supplies objects for layout entries whose target no longer exists, and for Spawn().")]
    [SerializeField]
    private LayoutCatalogue catalogue;

    [Header("What to capture")]
    [SerializeField]
    private bool includeInactive = true;

    [Tooltip("Also restore each child's active state. Off = placement only.")]
    [SerializeField]
    private bool applyActiveState = true;

    [Tooltip("Destroy layout-spawned objects the saved layout no longer mentions. Hand-authored children are never touched.")]
    [SerializeField]
    private bool pruneOrphans = true;

    [Header("Runtime")]
    [Tooltip("Apply on Start. Edit mode never auto-applies — it would stomp whatever you are dragging.")]
    [SerializeField]
    private bool applyOnStart = true;

    [Tooltip("In a build, mirror runtime captures to JSON and prefer that file on load.")]
    [SerializeField]
    private bool useRuntimeOverrideFile = true;

    [SerializeField]
    private string overrideFileName = "tag-layout.json";

    [Header("Hotkeys (play mode, legacy input only)")]
    [SerializeField]
    private KeyCode saveKey = KeyCode.F5;

    [SerializeField]
    private KeyCode applyKey = KeyCode.F6;

    [SerializeField]
    private KeyCode spawnKey = KeyCode.F7;

    /// <summary>Where the runtime override lives on the current platform.</summary>
    public string OverridePath =>
        Path.Combine(Application.persistentDataPath, overrideFileName);

    public Transform Root => root != null ? root : transform;
    public LayoutCatalogue Catalogue => catalogue;

    private void Reset()
    {
        root = transform;
    }

    private void Start()
    {
        if (Application.isPlaying && applyOnStart)
            ApplyLayout();
    }

    private void Update()
    {
        if (!Application.isPlaying)
            return;

#if ENABLE_LEGACY_INPUT_MANAGER
        if (saveKey != KeyCode.None && Input.GetKeyDown(saveKey))
            SaveLayout();

        if (applyKey != KeyCode.None && Input.GetKeyDown(applyKey))
            ApplyLayout();

        if (spawnKey != KeyCode.None && Input.GetKeyDown(spawnKey))
            Spawn();
#endif
    }

    /// <summary>
    /// Add a new object under the root. It is tagged as a layout member, so the
    /// next SaveLayout records it and every later ApplyLayout recreates it.
    /// Pass null to use the catalogue's default key.
    /// </summary>
    public GameObject Spawn(string key = null, Transform parent = null)
    {
        if (catalogue == null)
        {
            Debug.LogError("[TransformLayoutBinder] No catalogue assigned.", this);
            return null;
        }

        return catalogue.Spawn(
            string.IsNullOrEmpty(key) ? catalogue.DefaultKey : key,
            parent != null ? parent : Root
        );
    }

    /// <summary>Capture the current arrangement. Single entry point — bind it to whatever you like.</summary>
    [ContextMenu("Save Layout")]
    public void SaveLayout()
    {
        if (layout == null)
        {
            Debug.LogError("[TransformLayoutBinder] No layout asset assigned.", this);
            return;
        }

        layout.CaptureFrom(Root, includeInactive);

#if UNITY_EDITOR
        // The asset write is what makes this outlive Play Mode.
        UnityEditor.EditorUtility.SetDirty(layout);
        UnityEditor.AssetDatabase.SaveAssets();

        Debug.Log(
            $"[TransformLayoutBinder] Captured {layout.Count} transforms into " +
            $"{layout.name}. Asset saved; this survives exiting Play Mode.",
            this
        );

        if (Application.isPlaying && useRuntimeOverrideFile)
            WriteOverrideFile();
#else
        if (useRuntimeOverrideFile)
            WriteOverrideFile();
#endif
    }

    /// <summary>Rebuild the hierarchy to match the stored layout.</summary>
    [ContextMenu("Apply Layout")]
    public void ApplyLayout()
    {
        bool inEditMode = !Application.isPlaying;

        if (useRuntimeOverrideFile && !inEditMode && TryApplyOverrideFile())
            return;

        if (layout == null)
        {
            Debug.LogError("[TransformLayoutBinder] No layout asset assigned.", this);
            return;
        }

        int applied = layout.ApplyTo(
            Root,
            catalogue,
            applyActiveState,
            pruneOrphans,
            recordUndo: inEditMode
        );

#if UNITY_EDITOR
        if (inEditMode)
        {
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(gameObject.scene);
            Debug.Log($"[TransformLayoutBinder] Applied {applied} transforms in edit mode.", this);
        }
#endif
    }

    /// <summary>Delete the on-device override so the baked asset layout is used again.</summary>
    [ContextMenu("Clear Runtime Override")]
    public void ClearRuntimeOverride()
    {
        if (File.Exists(OverridePath))
        {
            File.Delete(OverridePath);
            Debug.Log($"[TransformLayoutBinder] Deleted override at {OverridePath}.", this);
        }
    }

    private void WriteOverrideFile()
    {
        try
        {
            File.WriteAllText(OverridePath, layout.ToJson());
            Debug.Log($"[TransformLayoutBinder] Layout written to {OverridePath}.", this);
        }
        catch (IOException exception)
        {
            Debug.LogError($"[TransformLayoutBinder] Could not write layout: {exception.Message}", this);
        }
    }

    private bool TryApplyOverrideFile()
    {
        if (!File.Exists(OverridePath))
            return false;

        try
        {
            TransformLayout stored = TransformLayout.FromJson(File.ReadAllText(OverridePath));
            stored.ApplyTo(Root, catalogue, applyActiveState, pruneOrphans);
            Destroy(stored);
            return true;
        }
        catch (IOException exception)
        {
            Debug.LogWarning(
                $"[TransformLayoutBinder] Override unreadable ({exception.Message}); " +
                "falling back to the asset.",
                this
            );

            return false;
        }
    }
}