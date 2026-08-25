using UnityEngine;

[RequireComponent(typeof(Collider))]
[RequireComponent(typeof(Rigidbody))]
public class HugSensor : MonoBehaviour
{
    public HugArm arm;
    public LayerMask clingMask = ~0;

    void Awake()
    {
        var col = GetComponent<Collider>();
        col.isTrigger = true;
        var rb = GetComponent<Rigidbody>();
        rb.isKinematic = true; // sensor should be kinematic
    }

    void OnTriggerEnter(Collider other)
    {
        if (((1 << other.gameObject.layer) & clingMask.value) == 0) return;
        if (arm) arm.AcquireTarget(other);
    }

    void OnTriggerExit(Collider other)
    {
        if (((1 << other.gameObject.layer) & clingMask.value) == 0) return;
        if (arm) arm.ClearTarget();
    }
}