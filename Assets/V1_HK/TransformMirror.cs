using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Reflects this object across one or more of its parent's local axis planes,
/// producing sibling clones that track the source continuously.
///
/// The clones are derived data. They are stamped <see cref="LayoutGenerated"/>,
/// carry HideFlags.DontSave, and are therefore invisible to the layout system —
/// never captured, never written to the scene file, never applied. Move the
/// source and the mirrors follow; save the layout and only the source is stored,
/// together with this component's settings via <see cref="ILayoutState"/>.
///
/// A clone is a *renderer shell*, not an Instantiate of the source: a bare
/// skeleton of GameObjects carrying MeshFilter/MeshRenderer and nothing else.
/// Mesh and materials are re-pointed at the source's every tick, so swapping
/// either on the source updates the mirrors in the same frame — they hold the
/// same asset references, not copies of them. It also means the mirrors carry
/// no duplicate scripts, colliders or rigidbodies.
///
/// Rotation is reflected as a proper rotation rather than by negating a scale
/// axis. A true mirror is an improper transform: it would invert triangle
/// winding and light the mesh inside-out. Reflecting the rotation instead —
/// axis mirrored, handedness of the angle flipped — keeps the clone a normal
/// object. Consequence: the *placement* of the clone is mirrored, but the
/// interior of a nested subtree is copied rather than reflected. For a
/// symmetric primitive the two are identical; for an asymmetric assembly,
/// <see cref="negateScale"/> gives you the true reflection.
/// </summary>
[ExecuteAlways]
[AddComponentMenu("AprilTag/Transform Mirror")]
public sealed class TransformMirror : MonoBehaviour, ILayoutState
{
    [Flags]
    public enum Axes
    {
        None = 0,
        X = 1 << 0,
        Y = 1 << 1,
        Z = 1 << 2
    }

    [Serializable]
    private struct State
    {
        public Axes MirrorAcross;
        public bool IncludeCombinations;
        public bool NegateScale;
        public bool MirrorActiveState;
        public bool CopyPropertyBlocks;
    }

    /// <summary>One source transform paired with the shell node standing in for it.</summary>
    private struct Node
    {
        public Transform Source;
        public Transform Clone;
        public MeshFilter SourceFilter;
        public MeshFilter CloneFilter;
        public MeshRenderer SourceRenderer;
        public MeshRenderer CloneRenderer;
    }

    private sealed class Mirror
    {
        public Axes Axes;
        public Transform Root;
        public readonly List<Node> Nodes = new List<Node>();
    }

    [Header("Mirror planes")]
    [Tooltip("Planes to reflect across, defined by the parent's local axes and passing through the parent origin.")]
    [SerializeField]
    private Axes mirrorAcross = Axes.X;

    [Tooltip(
        "Also produce the corner/diagonal mirrors. With X and Z selected: on " +
        "gives three clones (X, Z, XZ), off gives two."
    )]
    [SerializeField]
    private bool includeCombinations = true;

    [Header("Options")]
    [Tooltip(
        "True geometric reflection via a negative scale axis. Correct for chiral " +
        "meshes and asymmetric subtrees, but inverts winding — your material " +
        "needs double-sided rendering."
    )]
    [SerializeField]
    private bool negateScale;

    [Tooltip("Clones follow the source's enabled state.")]
    [SerializeField]
    private bool mirrorActiveState = true;

    [Tooltip("Also mirror per-renderer MaterialPropertyBlocks. Leave on unless you are profiling.")]
    [SerializeField]
    private bool copyPropertyBlocks = true;

    private readonly List<Mirror> mirrors = new List<Mirror>();

    private int builtSignature;

    // Reused across all instances to keep the per-frame sync allocation-free.
    private static readonly List<Transform> ScratchTransforms = new List<Transform>();
    private static readonly List<Material> SourceMaterials = new List<Material>();
    private static readonly List<Material> CloneMaterials = new List<Material>();
    private static MaterialPropertyBlock scratchBlock;

    public int MirrorCount => mirrors.Count;

    private void OnEnable()
    {
        Rebuild();
    }

    private void OnDisable()
    {
        DestroyMirrors();
    }

    private void OnDestroy()
    {
        DestroyMirrors();
    }

    private void LateUpdate()
    {
        int signature = Signature();

        if (builtSignature != signature || HasBrokenMirror())
        {
            Rebuild();
            return;
        }

        Sync();
    }

    private void OnValidate()
    {
        // Deferring is required: Unity forbids creating/destroying objects from
        // inside OnValidate. LateUpdate picks the change up next tick.
        builtSignature = 0;
    }

    /// <summary>Force a full teardown and recreate. Safe to call from anywhere but OnValidate.</summary>
    [ContextMenu("Rebuild Mirrors")]
    public void Rebuild()
    {
        DestroyMirrors();

        if (transform.parent == null)
        {
            Debug.LogWarning(
                "[TransformMirror] Needs a parent — the mirror planes are the parent's axes.",
                this
            );

            return;
        }

        foreach (Axes subset in EnumerateSubsets(mirrorAcross, includeCombinations))
            mirrors.Add(BuildMirror(subset));

        builtSignature = Signature();
        Sync();
    }

    // ---- construction -----------------------------------------------------

    private Mirror BuildMirror(Axes subset)
    {
        var mirror = new Mirror { Axes = subset };

        mirror.Root = BuildShell(transform, transform.parent, mirror);
        mirror.Root.gameObject.name = $"{name} (Mirror {Label(subset)})";

        LayoutGenerated.Stamp(mirror.Root.gameObject, this);

        return mirror;
    }

    /// <summary>
    /// Recreate <paramref name="source"/> as a bare shell under
    /// <paramref name="parent"/>, recursing into its non-generated children.
    /// </summary>
    private Transform BuildShell(Transform source, Transform parent, Mirror mirror)
    {
        var shell = new GameObject(source.name)
        {
            hideFlags = HideFlags.DontSave | HideFlags.NotEditable,
            layer = source.gameObject.layer
        };

        shell.transform.SetParent(parent, false);

        var node = new Node
        {
            Source = source,
            Clone = shell.transform,
            SourceFilter = source.GetComponent<MeshFilter>(),
            SourceRenderer = source.GetComponent<MeshRenderer>()
        };

        // Only give the shell the components the source actually has. A pure
        // grouping transform stays a pure grouping transform.
        if (node.SourceFilter != null)
            node.CloneFilter = shell.AddComponent<MeshFilter>();

        if (node.SourceRenderer != null)
            node.CloneRenderer = shell.AddComponent<MeshRenderer>();

        mirror.Nodes.Add(node);

        foreach (Transform child in source)
        {
            // Never mirror a mirror, or anything else somebody generated.
            if (child.GetComponent<LayoutGenerated>() != null)
                continue;

            BuildShell(child, shell.transform, mirror);
        }

        return shell.transform;
    }

    // ---- per-frame sync ---------------------------------------------------

    private void Sync()
    {
        foreach (Mirror mirror in mirrors)
        {
            for (int index = 0; index < mirror.Nodes.Count; index++)
            {
                Node node = mirror.Nodes[index];

                if (node.Source == null || node.Clone == null)
                    continue;

                if (index == 0)
                    SyncRootPlacement(node, mirror.Axes);
                else
                    SyncChildPlacement(node);

                SyncAppearance(node);
            }

            if (mirrorActiveState &&
                mirror.Root != null &&
                mirror.Root.gameObject.activeSelf != gameObject.activeSelf)
            {
                mirror.Root.gameObject.SetActive(gameObject.activeSelf);
            }
        }
    }

    private void SyncRootPlacement(Node node, Axes axes)
    {
        node.Clone.localPosition = ReflectPosition(node.Source.localPosition, axes);
        node.Clone.localRotation = ReflectRotation(node.Source.localRotation, axes);
        node.Clone.localScale = negateScale
            ? NegateComponents(node.Source.localScale, axes)
            : node.Source.localScale;
    }

    /// <summary>
    /// Descendants copy their local placement verbatim. The reflection is applied
    /// once, at the top — see the class remarks on why the interior is not
    /// reflected unless a negative scale carries it down.
    /// </summary>
    private static void SyncChildPlacement(Node node)
    {
        node.Clone.localPosition = node.Source.localPosition;
        node.Clone.localRotation = node.Source.localRotation;
        node.Clone.localScale = node.Source.localScale;
    }

    /// <summary>
    /// Re-point the shell at whatever mesh and materials the source is currently
    /// using. Assignments are guarded by a reference comparison, so an unchanged
    /// source costs nothing beyond the check.
    /// </summary>
    private void SyncAppearance(Node node)
    {
        if (node.CloneFilter != null && node.SourceFilter != null)
        {
            if (node.CloneFilter.sharedMesh != node.SourceFilter.sharedMesh)
                node.CloneFilter.sharedMesh = node.SourceFilter.sharedMesh;
        }

        if (node.CloneRenderer == null || node.SourceRenderer == null)
            return;

        // sharedMaterials returns the runtime instance once anyone has touched
        // .material, so this tracks instanced materials as well as assets.
        node.SourceRenderer.GetSharedMaterials(SourceMaterials);
        node.CloneRenderer.GetSharedMaterials(CloneMaterials);

        if (!SameMaterials(SourceMaterials, CloneMaterials))
            node.CloneRenderer.sharedMaterials = SourceMaterials.ToArray();

        if (node.CloneRenderer.enabled != node.SourceRenderer.enabled)
            node.CloneRenderer.enabled = node.SourceRenderer.enabled;

        node.CloneRenderer.shadowCastingMode = node.SourceRenderer.shadowCastingMode;
        node.CloneRenderer.receiveShadows = node.SourceRenderer.receiveShadows;
        node.CloneRenderer.lightProbeUsage = node.SourceRenderer.lightProbeUsage;

        if (node.Clone.gameObject.layer != node.Source.gameObject.layer)
            node.Clone.gameObject.layer = node.Source.gameObject.layer;

        if (!copyPropertyBlocks)
            return;

        scratchBlock ??= new MaterialPropertyBlock();

        if (node.SourceRenderer.HasPropertyBlock())
        {
            node.SourceRenderer.GetPropertyBlock(scratchBlock);
            node.CloneRenderer.SetPropertyBlock(scratchBlock);
        }
        else if (node.CloneRenderer.HasPropertyBlock())
        {
            node.CloneRenderer.SetPropertyBlock(null);
        }
    }

    private static bool SameMaterials(List<Material> left, List<Material> right)
    {
        if (left.Count != right.Count)
            return false;

        for (int index = 0; index < left.Count; index++)
        {
            if (left[index] != right[index])
                return false;
        }

        return true;
    }

    // ---- lifecycle bookkeeping -------------------------------------------

    private bool HasBrokenMirror()
    {
        foreach (Mirror mirror in mirrors)
        {
            if (mirror.Root == null)
                return true;

            foreach (Node node in mirror.Nodes)
            {
                if (node.Source == null || node.Clone == null)
                    return true;
            }
        }

        return false;
    }

    private void DestroyMirrors()
    {
        foreach (Mirror mirror in mirrors)
        {
            if (mirror.Root != null)
                DestroyNow(mirror.Root.gameObject);
        }

        mirrors.Clear();
        builtSignature = 0;
    }

    private static void DestroyNow(UnityEngine.Object target)
    {
        if (target == null)
            return;

        if (Application.isPlaying)
            Destroy(target);
        else
            DestroyImmediate(target);
    }

    /// <summary>
    /// Settings plus the shape of the source subtree. A change here means the
    /// shells no longer stand in for the right things and must be rebuilt;
    /// mesh, material and placement changes do not appear, because those are
    /// handled by the cheap per-frame sync.
    /// </summary>
    private int Signature()
    {
        int hash = 17;
        hash = hash * 31 + (int)mirrorAcross;
        hash = hash * 31 + (includeCombinations ? 1 : 0);
        hash = hash * 31 + (negateScale ? 1 : 0);
        hash = hash * 31 + name.GetHashCode();

        GetComponentsInChildren(true, ScratchTransforms);

        foreach (Transform node in ScratchTransforms)
        {
            if (node != transform && node.GetComponent<LayoutGenerated>() != null)
                continue;

            hash = hash * 31 + node.name.GetHashCode();
            hash = hash * 31 + (node.GetComponent<MeshRenderer>() != null ? 1 : 0);
        }

        ScratchTransforms.Clear();

        // Non-zero, so OnValidate's 0 always reads as stale.
        return hash | 1;
    }

    // ---- reflection maths -------------------------------------------------

    /// <summary>Negate the components named by <paramref name="axes"/>.</summary>
    public static Vector3 ReflectPosition(Vector3 local, Axes axes) => NegateComponents(local, axes);

    /// <summary>
    /// Reflect a rotation across the planes named by <paramref name="axes"/>,
    /// staying a proper rotation.
    ///
    /// For a rotation of angle t about unit axis u, reflection maps the axis to
    /// Mu and flips the handedness of the angle, so q = (cos t/2, sin t/2 · u)
    /// becomes (cos t/2, -sin t/2 · Mu). Reflecting across the plane whose
    /// normal is X, that is (w, x, -y, -z): keep the component along the normal,
    /// negate the other two.
    /// </summary>
    public static Quaternion ReflectRotation(Quaternion local, Axes axes)
    {
        Quaternion result = local;

        if ((axes & Axes.X) != 0)
            result = new Quaternion(result.x, -result.y, -result.z, result.w);

        if ((axes & Axes.Y) != 0)
            result = new Quaternion(-result.x, result.y, -result.z, result.w);

        if ((axes & Axes.Z) != 0)
            result = new Quaternion(-result.x, -result.y, result.z, result.w);

        return result;
    }

    private static Vector3 NegateComponents(Vector3 value, Axes axes)
    {
        if ((axes & Axes.X) != 0) value.x = -value.x;
        if ((axes & Axes.Y) != 0) value.y = -value.y;
        if ((axes & Axes.Z) != 0) value.z = -value.z;

        return value;
    }

    /// <summary>
    /// Every non-empty subset of the selected axes, or just the singletons when
    /// combinations are off.
    /// </summary>
    private static IEnumerable<Axes> EnumerateSubsets(Axes selected, bool includeCombinations)
    {
        for (int bits = 1; bits <= 7; bits++)
        {
            var subset = (Axes)bits;

            if ((subset & selected) != subset)
                continue;

            if (!includeCombinations && !IsSingleAxis(subset))
                continue;

            yield return subset;
        }
    }

    private static bool IsSingleAxis(Axes axes) => axes != Axes.None && (axes & (axes - 1)) == 0;

    private static string Label(Axes axes)
    {
        string label = string.Empty;

        if ((axes & Axes.X) != 0) label += "X";
        if ((axes & Axes.Y) != 0) label += "Y";
        if ((axes & Axes.Z) != 0) label += "Z";

        return label;
    }

    // ---- layout persistence ----------------------------------------------

    public string CaptureState()
    {
        return JsonUtility.ToJson(new State
        {
            MirrorAcross = mirrorAcross,
            IncludeCombinations = includeCombinations,
            NegateScale = negateScale,
            MirrorActiveState = mirrorActiveState,
            CopyPropertyBlocks = copyPropertyBlocks
        });
    }

    public void RestoreState(string state)
    {
        if (string.IsNullOrEmpty(state))
            return;

        State restored = JsonUtility.FromJson<State>(state);

        mirrorAcross = restored.MirrorAcross;
        includeCombinations = restored.IncludeCombinations;
        negateScale = restored.NegateScale;
        mirrorActiveState = restored.MirrorActiveState;
        copyPropertyBlocks = restored.CopyPropertyBlocks;

        builtSignature = 0;
    }

    private void OnDrawGizmosSelected()
    {
        if (transform.parent == null)
            return;

        Gizmos.matrix = transform.parent.localToWorldMatrix;
        Gizmos.color = new Color(1f, 0.6f, 0.1f, 0.35f);

        const float extent = 0.5f;

        if ((mirrorAcross & Axes.X) != 0)
            Gizmos.DrawWireCube(Vector3.zero, new Vector3(0f, extent, extent));

        if ((mirrorAcross & Axes.Y) != 0)
            Gizmos.DrawWireCube(Vector3.zero, new Vector3(extent, 0f, extent));

        if ((mirrorAcross & Axes.Z) != 0)
            Gizmos.DrawWireCube(Vector3.zero, new Vector3(extent, extent, 0f));
    }
}