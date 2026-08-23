using UnityEngine;

public class TrackLeftControllerContinuous : MonoBehaviour
{
    public Transform trackingSpace; // The parent tracking space for controller input
    private float accumulatedRotation = 0f; // Track cumulative rotation applied by rotation script


    void Update()
    {
        // Get the position and rotation of the left controller
        Vector3 leftPosition = trackingSpace.TransformPoint(OVRInput.GetLocalControllerPosition(OVRInput.Controller.LTouch));
        Quaternion leftRotation = OVRInput.GetLocalControllerRotation(OVRInput.Controller.LTouch);

        // Apply the accumulated rotation offset
        Quaternion correctionRotation = Quaternion.Euler(0, accumulatedRotation, 0);
        transform.position = leftPosition;
        transform.rotation = correctionRotation * leftRotation; // Apply offset rotation
    }

    void ApplyRotationOffset(float yRotationOffset)
    {
        // Accumulate the total rotation correction
        accumulatedRotation += yRotationOffset;
    }
}