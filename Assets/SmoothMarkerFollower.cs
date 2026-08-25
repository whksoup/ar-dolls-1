using UnityEngine;

public class SmoothMarkerFollower : MonoBehaviour
{
    [Header("Target")]
    public Transform target;

    [Header("Position")]
    public bool followPosition = true;
    [Min(0f)] public float positionSmoothing = 12f;

    [Header("Rotation")]
    public bool followRotation = true;
    [Min(0f)] public float rotationSmoothing = 8f;

    private void LateUpdate()
    {
        if (target == null)
            return;

        // Frame-rate-independent smoothing factors.
        float positionT = 1f - Mathf.Exp(-positionSmoothing * Time.deltaTime);
        float rotationT = 1f - Mathf.Exp(-rotationSmoothing * Time.deltaTime);

        if (followPosition)
        {
            transform.position = Vector3.Lerp(
                transform.position,
                target.position,
                positionT
            );
        }

        if (followRotation)
        {
            transform.rotation = Quaternion.Slerp(
                transform.rotation,
                target.rotation,
                rotationT
            );
        }
    }

    public void SnapToTarget()
    {
        if (target == null)
            return;

        transform.SetPositionAndRotation(target.position, target.rotation);
    }
}