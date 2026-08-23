using UnityEngine;

public class TrackRightControllerContinuous : MonoBehaviour
{
    public Transform trackingSpace; // The parent tracking space for controller input
    private float accumulatedRotation = 0f; // Track cumulative rotation applied by rotation script


    void Update()
    {
        // Get the position and rotation of the RIGHT controller
        Vector3 rightPosition = trackingSpace.TransformPoint(OVRInput.GetLocalControllerPosition(OVRInput.Controller.RTouch));
        Quaternion rightRotation = OVRInput.GetLocalControllerRotation(OVRInput.Controller.RTouch);

        // Apply the accumulated rotation offset
        Quaternion correctionRotation = Quaternion.Euler(0, accumulatedRotation, 0);
        transform.position = rightPosition;
        transform.rotation = correctionRotation * rightRotation; // Apply offset rotation
    }

    void ApplyRotationOffset(float yRotationOffset)
    {
        // Accumulate the total rotation correction
        accumulatedRotation += yRotationOffset;
    }
}