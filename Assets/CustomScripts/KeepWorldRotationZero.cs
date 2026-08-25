using UnityEngine;

public class KeepWorldRotationZero : MonoBehaviour
{
    void LateUpdate()
    {
        transform.rotation = Quaternion.identity;
    }
}