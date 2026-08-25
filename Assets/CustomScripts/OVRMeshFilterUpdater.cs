using UnityEngine;
using System.Collections;

[RequireComponent(typeof(OVRMesh))]
[RequireComponent(typeof(MeshFilter))]
[RequireComponent(typeof(SkinnedMeshRenderer))]
public class SkinnedMeshFilterUpdater : MonoBehaviour
{
    private SkinnedMeshRenderer skinnedMeshRenderer;
    private MeshFilter meshFilter;

    private void Awake()
    {
        // Get references to the SkinnedMeshRenderer and MeshFilter components
        skinnedMeshRenderer = GetComponent<SkinnedMeshRenderer>();
        meshFilter = GetComponent<MeshFilter>();
    }

    private IEnumerator Start()
    {
        // Wait until the SkinnedMeshRenderer has a valid mesh
        while (skinnedMeshRenderer.sharedMesh == null)
        {
            yield return null; // Wait for the next frame
        }

        // Create a new Mesh to hold the baked mesh
        Mesh bakedMesh = new Mesh();

        // Bake the mesh from the SkinnedMeshRenderer
        skinnedMeshRenderer.BakeMesh(bakedMesh);

        // Assign the baked mesh to the MeshFilter
        meshFilter.mesh = bakedMesh;

        // Optional: If the mesh deforms over time, update it continuously
        // StartCoroutine(UpdateMeshContinuously());
    }

    // Optional coroutine to update the mesh each frame
    private IEnumerator UpdateMeshContinuously()
    {
        Mesh bakedMesh = meshFilter.mesh;

        while (true)
        {
            skinnedMeshRenderer.BakeMesh(bakedMesh);
            yield return null; // Wait for the next frame
        }
    }
}
