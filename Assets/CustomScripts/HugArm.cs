using UnityEngine;

public class HugArm : MonoBehaviour
{
    public enum ArmState { Dangle, Seek, Latched }

    [Header("Rigidbodies")]
    public Rigidbody bodyRb;           // the body the arm connects to
    public Rigidbody armRb;            // arm rigidbody (upper arm)
    public Rigidbody handTipRb;        // handTip rigidbody (lower arm/hand)

    [Header("Targeting")]
    public LayerMask clingMask = ~0;
    public float releaseDistance = 2.5f;

    [Header("Attraction")]
    public float attractionForce = 50f;
    public float maxAttractionDistance = 1.5f;

    [Header("Hugging")]
    public float latchBreakForce = 200f;
    public float latchBreakTorque = 200f;

    [Header("Debug")]
    public ArmState state = ArmState.Dangle;

    // Internals
    Collider currentTarget;
    Rigidbody currentTargetRb;
    FixedJoint latch;

    void Start()
    {
        if (!armRb || !handTipRb || !bodyRb)
        {
            Debug.LogError("Assign bodyRb, armRb and handTipRb.");
            enabled = false;
            return;
        }

        // Setup shoulder joint (arm to body)
        ConfigurableJoint shoulderJoint = armRb.GetComponent<ConfigurableJoint>();
        if (shoulderJoint)
        {
            shoulderJoint.connectedBody = bodyRb;

            // Lock position
            shoulderJoint.xMotion = ConfigurableJointMotion.Locked;
            shoulderJoint.yMotion = ConfigurableJointMotion.Locked;
            shoulderJoint.zMotion = ConfigurableJointMotion.Locked;

            // Free rotation
            shoulderJoint.angularXMotion = ConfigurableJointMotion.Free;
            shoulderJoint.angularYMotion = ConfigurableJointMotion.Free;
            shoulderJoint.angularZMotion = ConfigurableJointMotion.Free;
        }
        shoulderJoint.rotationDriveMode = RotationDriveMode.Slerp;
        JointDrive drive = new JointDrive();
        drive.positionSpring = 0f;
        drive.positionDamper = 5f;  // Small damping reduces jitter
        shoulderJoint.slerpDrive = drive;
        // Setup elbow joint (handTip to arm) - if it exists
        ConfigurableJoint elbowJoint = handTipRb.GetComponent<ConfigurableJoint>();
        if (elbowJoint)
        {
            elbowJoint.connectedBody = armRb;

            // Lock position
            elbowJoint.xMotion = ConfigurableJointMotion.Locked;
            elbowJoint.yMotion = ConfigurableJointMotion.Locked;
            elbowJoint.zMotion = ConfigurableJointMotion.Locked;

            // Free rotation
            elbowJoint.angularXMotion = ConfigurableJointMotion.Free;
            elbowJoint.angularYMotion = ConfigurableJointMotion.Free;
            elbowJoint.angularZMotion = ConfigurableJointMotion.Free;
        }
    }

    void FixedUpdate()
    {
        if (state == ArmState.Dangle) return;

        if (!currentTarget)
        {
            ReleaseLatch();
            state = ArmState.Dangle;
            return;
        }

        Vector3 shoulderPos = armRb.position;
        Vector3 targetPoint = currentTarget.ClosestPoint(shoulderPos);
        float distFromShoulder = Vector3.Distance(targetPoint, shoulderPos);

        if (distFromShoulder > releaseDistance)
        {
            ClearTarget();
            ReleaseLatch();
            state = ArmState.Dangle;
            return;
        }

        Vector3 tipPos = handTipRb.position;
        float distFromTip = Vector3.Distance(targetPoint, tipPos);

        if (distFromTip < maxAttractionDistance)
        {
            Vector3 toTarget = (targetPoint - tipPos).normalized;
            float forceMagnitude = attractionForce * (1f - distFromTip / maxAttractionDistance);
            handTipRb.AddForce(toTarget * forceMagnitude, ForceMode.Force);
        }
    }

    public void AcquireTarget(Collider col)
    {
        if (!col || state == ArmState.Latched) return;
        if ((clingMask.value & (1 << col.gameObject.layer)) == 0) return;

        currentTarget = col;
        currentTargetRb = col.attachedRigidbody;
        state = ArmState.Seek;
    }

    public void ClearTarget()
    {
        currentTarget = null;
        currentTargetRb = null;
        if (state != ArmState.Latched)
        {
            state = ArmState.Dangle;
        }
    }

    void OnCollisionEnter(Collision c)
    {
        if (state != ArmState.Seek || !currentTarget) return;
        if (!IsSameTarget(c.collider, currentTarget)) return;

        if (!latch)
        {
            latch = handTipRb.gameObject.AddComponent<FixedJoint>();
            latch.connectedBody = currentTargetRb;
            latch.enableCollision = false;
            latch.breakForce = latchBreakForce;
            latch.breakTorque = latchBreakTorque;
        }
        state = ArmState.Latched;
    }

    void OnJointBreak(float breakForce)
    {
        ReleaseLatch();
        state = currentTarget ? ArmState.Seek : ArmState.Dangle;
    }

    void ReleaseLatch()
    {
        if (latch)
        {
            Destroy(latch);
            latch = null;
        }
    }

    static bool IsSameTarget(Collider a, Collider b)
    {
        if (!a || !b) return false;
        if (a == b) return true;
        return a.transform.root == b.transform.root;
    }
}