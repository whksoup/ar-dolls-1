using UnityEngine;

public class DirectionalLightControl : MonoBehaviour
{
    [Header("References")]
    public Camera targetCamera; // Reference to the camera to follow

    [Header("Input Settings")]
    public OVRInput.RawButton alignLightButton = OVRInput.RawButton.Y; // Default to RawButton.Y
    public KeyCode anchorAltButton = KeyCode.C; 

    [Header("Offset Settings")]
    public Vector3 rotationOffset = new Vector3(45f, 0f, 0f); // Default offset values for rotation

    private void Update()
    {
        // Check if the configured button is pressed
        if (OVRInput.Get(alignLightButton)||Input.GetKeyDown(anchorAltButton))
        {
            AlignLightToCamera();
        }
    }

    private void AlignLightToCamera()
    {
        if (targetCamera == null)
        {
            Debug.LogWarning("No target camera assigned to DirectionalLightControl!");
            return;
        }

        // Get the forward direction of the camera
        Vector3 cameraForward = targetCamera.transform.forward;

        // Calculate the rotation for the directional light
        Quaternion targetRotation = Quaternion.LookRotation(cameraForward) * Quaternion.Euler(rotationOffset);

        // Apply the rotation to the light
        transform.rotation = targetRotation;

        Debug.Log("Directional light aligned to camera with offset.");
    }
}
