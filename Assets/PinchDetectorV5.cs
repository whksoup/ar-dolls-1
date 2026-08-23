using UnityEngine;

public class PinchDetectorV5 : MonoBehaviour
{
    // GameObjects representing the middle finger and thumb
    public GameObject MiddleFinger;
    public GameObject Thumb;

    // GameObject that acts as the parent for Pinch
    public GameObject PinchParent;

    // The GameObject that will scale (child of PinchParent)
    public GameObject Pinch;

    // GameObject to copy rotation from
    public GameObject RotationSource;

    // Distance threshold to detect a pinch (maximum distance)
    public float distanceThreshold = 0.1f;

    // Distance floor to deactivate the pinch GameObject (minimum distance)
    public float distanceFloor = 0.02f;

    // Scaling multipliers for the x and y axes
    public float maxScaleMultiplier = 1.0f;
    public float minScaleMultiplier = 0.1f;

    // Additional multiplier for scaling from PressureValue script (now a Vector3)
    [HideInInspector]
    public Vector3 pressureMultiplier = new Vector3(1.0f, 1.0f, 1.0f);

    // Variables to store initial scale values
    private Vector3 initialLocalScale;
    private float initialZScale;

    void Start()
    {
        // Store the initial local scale of the Pinch GameObject
        if (Pinch != null)
        {
            initialLocalScale = Pinch.transform.localScale;
            initialZScale = initialLocalScale.z;
        }
    }

    void Update()
    {
        // Check that all required GameObjects are assigned
        if (MiddleFinger != null && Thumb != null && Pinch != null && PinchParent != null && RotationSource != null)
        {
            // Calculate the distance between the middle finger and thumb
            float distance = Vector3.Distance(MiddleFinger.transform.position, Thumb.transform.position);

            if (distance <= distanceThreshold && distance >= distanceFloor)
            {
                // Activate the PinchParent and Pinch GameObjects
                PinchParent.SetActive(true);
                Pinch.SetActive(true);

                // Calculate the midpoint between the MiddleFinger and Thumb
                Vector3 midpoint = (MiddleFinger.transform.position + Thumb.transform.position) / 2;
                PinchParent.transform.position = midpoint;

                // Copy rotation from RotationSource to PinchParent
                PinchParent.transform.rotation = RotationSource.transform.rotation;

                // Remap the distance to a scale multiplier between minScaleMultiplier and maxScaleMultiplier
                // Remap the distance to a scale multiplier between min and max
                float t = (distance - distanceFloor) / (distanceThreshold - distanceFloor);
                float scaleMultiplier = Mathf.Lerp(minScaleMultiplier, maxScaleMultiplier, t);

                // Use pressure as a UNIFORM multiplier (average or choose one axis)
                float pressureUniform =
                    (pressureMultiplier.x + pressureMultiplier.y + pressureMultiplier.z) / 3f;

                // Final uniform scale
                float finalMultiplier = scaleMultiplier * pressureUniform;

                // Apply uniform scale
                Pinch.transform.localScale = initialLocalScale * finalMultiplier;

            }
            else
            {
                // Deactivate the PinchParent GameObject
                PinchParent.SetActive(false);
            }
        }
    }
}
