using UnityEngine;

public class LookAtCameraYOnly : MonoBehaviour
{
    public bool zAxis = false;
    public Transform cameraTransform;
    [Range(0, 180)] public float maxYRotation = 45f; // Limit Y rotation (optional)

    private Vector3 initialEulerAngles;

    void Start()
    {
        // Save the initial rotation angles
        initialEulerAngles = transform.eulerAngles;
    }

    void Update()
    {
        if (cameraTransform == null) return;

        // Get direction to camera (ignore Y-axis if needed)
        Vector3 lookDir = cameraTransform.position - transform.position;
        lookDir.y = 0; // Optional: Remove Y-component to keep object upright

        // Apply rotation (Y-axis only)
        if (lookDir != Vector3.zero)
        {
            Quaternion targetRot = Quaternion.LookRotation(lookDir);
            float clampedY = ClampAngle(targetRot.eulerAngles.y, -maxYRotation, maxYRotation);
            
            if (zAxis)
            {
                // For z-axis rotation, we need to calculate the angle differently
                float angle = Mathf.Atan2(lookDir.x, lookDir.z) * Mathf.Rad2Deg;
                clampedY = ClampAngle(angle, -maxYRotation, maxYRotation);
                // Use initial X and Y angles, apply only Z rotation
                transform.eulerAngles = new Vector3(initialEulerAngles.x, initialEulerAngles.y, clampedY);
                transform.Rotate(0, 0, 180f);
            }
            else
            {
                // Original behavior for y-axis rotation
                // Use initial X and Z angles, apply only Y rotation
                transform.eulerAngles = new Vector3(initialEulerAngles.x, clampedY, initialEulerAngles.z);
                transform.Rotate(0, 180f, 0);
            }
        }
    }

    // Helper for clamping angles correctly (accounts for 360° wrap)
    private float ClampAngle(float angle, float min, float max)
    {
        if (angle > 180f) angle -= 360f;
        return Mathf.Clamp(angle, min, max);
    }
}