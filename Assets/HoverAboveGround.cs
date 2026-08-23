using UnityEngine;

public class HoverAboveGround : MonoBehaviour
{
    [Header("References")]
    public Transform raySource;   // object casting the ray
    public Transform puppet;      // object that hovers above the hit
    public LayerMask groundLayers;

    [Header("Hover Settings")]
    public float height = 0.5f;   // height above the hit point
    public float rayLength = 10f; // how far down to raycast

    void Update()
    {
        if (raySource == null || puppet == null)
            return;

        Ray ray = new Ray(raySource.position, Vector3.down);
        RaycastHit hit;

        if (Physics.Raycast(ray, out hit, rayLength, groundLayers))
        {
            // Place puppet above the hit point
            puppet.position = hit.point + Vector3.up * height;
        }
    }
}
