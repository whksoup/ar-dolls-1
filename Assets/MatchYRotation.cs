using UnityEngine;

public class MatchYRotationFromObjects : MonoBehaviour
{
    public Transform originA;   // A = origin
    public Transform targetB;   // B = target

    void LateUpdate()
    {
        if (originA == null || targetB == null) return;

        // Direction from A to B
        Vector3 direction = targetB.position - originA.position;

        // Flatten to world XZ plane (ignore vertical difference)
        direction.y = 0f;

        if (direction.sqrMagnitude < 0.0001f) return;

        // Calculate world Y angle
        float angle = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;

        // Apply to this object's world rotation (Y only)
        Vector3 currentEuler = transform.rotation.eulerAngles;

        transform.rotation = Quaternion.Euler(
            currentEuler.x,
            angle,
            currentEuler.z
        );
    }
}