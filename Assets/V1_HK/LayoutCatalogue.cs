using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Maps a string key to something spawnable. A saved layout stores keys, not
/// object references, so it stays a plain data file that can round-trip through
/// JSON on device.
///
/// For primitive work, leave <see cref="Item.Prefab"/> null and pick a
/// <see cref="PrimitiveType"/>; the shared material is applied on spawn.
/// </summary>
[CreateAssetMenu(menuName = "AprilTag/Layout Catalogue", fileName = "LayoutCatalogue")]
public sealed class LayoutCatalogue : ScriptableObject
{
    [Serializable]
    public struct Item
    {
        [Tooltip("Stored in the layout. Keep stable — renaming a key orphans saved objects.")]
        public string Key;

        [Tooltip("Optional. If set, this is instantiated and the primitive settings are ignored.")]
        public GameObject Prefab;

        public PrimitiveType Primitive;

        [Tooltip("Applied to the spawned primitive. Ignored when a prefab is used.")]
        public Material Material;

        [Tooltip("Primitives ship with a collider. Off strips it.")]
        public bool KeepCollider;
    }

    [SerializeField]
    private List<Item> items = new List<Item>();

    [SerializeField, Tooltip("Key used by Spawn(...) when the caller does not name one.")]
    private string defaultKey = "Cube";

    public IReadOnlyList<Item> Items => items;
    public string DefaultKey => defaultKey;

    public bool TryGet(string key, out Item item)
    {
        foreach (Item candidate in items)
        {
            if (string.Equals(candidate.Key, key, StringComparison.Ordinal))
            {
                item = candidate;
                return true;
            }
        }

        item = default;
        return false;
    }

    /// <summary>
    /// Create an instance of <paramref name="key"/> under <paramref name="parent"/>,
    /// tagged as a layout member. Returns null if the key is unknown.
    /// </summary>
    public GameObject Spawn(string key, Transform parent, string name = null)
    {
        if (!TryGet(key, out Item item))
        {
            Debug.LogWarning($"[LayoutCatalogue] Unknown key \"{key}\".", this);
            return null;
        }

        GameObject instance;

        if (item.Prefab != null)
        {
            instance = Instantiate(item.Prefab, parent);
        }
        else
        {
            instance = GameObject.CreatePrimitive(item.Primitive);
            instance.transform.SetParent(parent, false);

            if (item.Material != null)
            {
                var renderer = instance.GetComponent<Renderer>();

                if (renderer != null)
                    renderer.sharedMaterial = item.Material;
            }

            if (!item.KeepCollider)
            {
                var collider = instance.GetComponent<Collider>();

                if (collider != null)
                    DestroyImmediateOrDestroy(collider);
            }
        }

        instance.name = string.IsNullOrEmpty(name)
            ? MakeUniqueName(parent, key)
            : name;

        instance.AddComponent<LayoutMember>().SpawnKey = key;

        return instance;
    }

    /// <summary>Names like "Cube", "Cube 1", "Cube 2" — unique among current siblings.</summary>
    public static string MakeUniqueName(Transform parent, string baseName)
    {
        if (parent == null)
            return baseName;

        int suffix = 0;
        string candidate = baseName;

        while (parent.Find(candidate) != null)
            candidate = $"{baseName} {++suffix}";

        return candidate;
    }

    private static void DestroyImmediateOrDestroy(UnityEngine.Object target)
    {
        if (Application.isPlaying)
            Destroy(target);
        else
            DestroyImmediate(target);
    }
}
