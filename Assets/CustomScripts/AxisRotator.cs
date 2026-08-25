using UnityEngine;

public class AxisRotator : MonoBehaviour
{
    public enum RotationAxis { X, Y, Z }

    [Header("Rotation Settings")]
    public RotationAxis axis = RotationAxis.Y;     // default: local Y
    public float speed = 100f;                     // degrees per second
    public bool clockwise = true;                  // direction toggle

    void Update()
    {
        float direction = clockwise ? -1f : 1f;    // Unity's clockwise invert

        Vector3 localAxis = axis switch
        {
            RotationAxis.X => Vector3.right,
            RotationAxis.Y => Vector3.up,
            RotationAxis.Z => Vector3.forward,
            _ => Vector3.up
        };
        //

        transform.Rotate(localAxis, speed * direction * Time.deltaTime, Space.Self);
    }
}
