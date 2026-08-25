using UnityEngine;
using System;

public class ObjectSnapToController : MonoBehaviour
{
    public static event Action<Transform> OnObjectSnapped; // Event for snap updates

    [Header("References")]
    public Transform controller;      // The actual VR controller
    public Transform altController;   // The alternate VR controller
    public Transform targetObject;    // The object to snap to controller

    [Header("Input Settings")]
    public OVRInput.RawButton anchorButton = OVRInput.RawButton.Y; // Default: Y
    public KeyCode altAnchorKey = KeyCode.A;
    public KeyCode anchorAltButton = KeyCode.C;

    [Header("Snap Settings")]
    public bool snapOnPress = true;   // Snap on button down vs hold
    public Vector3 positionOffset = Vector3.zero; // Optional offset from controller

    private void Update()
    {
        // Check for button down to snap object
        if (OVRInput.GetDown(anchorButton) || Input.GetKeyDown(anchorAltButton))
        {
            SnapObjectToController(controller);
        }

        // Check for alternate key down
        if (Input.GetKeyDown(altAnchorKey))
        {
            SnapObjectToController(altController);
        }

        // Optional: Continuous tracking while button held
        if (!snapOnPress)
        {
            if (OVRInput.Get(anchorButton) || Input.GetKey(anchorAltButton))
            {
                SnapObjectToController(controller);
            }
            else if (Input.GetKey(altAnchorKey))
            {
                SnapObjectToController(altController);
            }
        }
    }

    private void SnapObjectToController(Transform targetController)
    {
        if (targetController == null || targetObject == null) return;

        // Snap position to controller (with optional offset)
        targetObject.position = targetController.position + positionOffset;

        // Take only Y-axis rotation from controller (no tilt)
        float yRotation = targetController.eulerAngles.y;
        targetObject.rotation = Quaternion.Euler(0f, yRotation, 0f);

        // Notify other scripts about the snap
        OnObjectSnapped?.Invoke(targetObject);
    }
}