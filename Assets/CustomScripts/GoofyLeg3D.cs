using UnityEngine;

public class GoofyLegBallHip : MonoBehaviour
{
    [Header("Joints")]
    public ConfigurableJoint hip;   // ball/socket hip
    public HingeJoint knee;         // hinge knee

    [Header("References")]
    public Rigidbody bodyRb;        // kinematic body; we read its velocity if you want speed-coupling

    [Header("State")]
    public bool run = true;
    [Range(0f, 1f)] public float phaseOffset = 0f;

    [Header("Gait")]
    public float baseFrequency = 1.4f;
    public float speedToFrequency = 0.35f;
    public float maxFrequency = 5f;
    public float hipMid = -5f;
    public float hipSwing = 35f;
    public float kneeBase = 10f;
    public float kneeFlex = 30f;
    [Range(0f, 1f)] public float kneeLead = 0.15f;

    [Header("Drives")]
    public float hipXSpringRun = 700f;
    public float hipXDamperRun = 40f;
    public float hipXSpringRest = 30f;
    public float hipXDamperRest = 3f;

    public float hipYZSpringRun = 100f;
    public float hipYZDamperRun = 10f;
    public float hipYZSpringRest = 8f;
    public float hipYZDamperRest = 1.5f;

    public float kneeSpringRun = 650f;
    public float kneeDamperRun = 35f;
    public float kneeSpringRest = 20f;
    public float kneeDamperRest = 3f;

    [Header("Calibration")]
    public float hipDir = 1f;   // flip if swing goes wrong way
    public float kneeDir = 1f;

    float phase;
    Quaternion hipStartLocal; // thigh rotation relative to connected body at Start

    void Start()
    {
        if (!hip || !bodyRb || !hip.connectedBody)
        {
            Debug.LogError("Assign hip (ConfigurableJoint), knee (HingeJoint), bodyRb, and set hip.connectedBody to the body RB.");
            enabled = false;
            return;
        }

        hip.configuredInWorldSpace = false; // local-space joint
        hipStartLocal = Quaternion.Inverse(hip.connectedBody.transform.rotation) * hip.transform.rotation;

        knee.useSpring = true;

        // Ensure linear motions are locked so the joint is attached
        hip.xMotion = ConfigurableJointMotion.Locked;
        hip.yMotion = ConfigurableJointMotion.Locked;
        hip.zMotion = ConfigurableJointMotion.Locked;
        hip.rotationDriveMode = RotationDriveMode.XYAndZ;
    }

    void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime;
        float speed = bodyRb.linearVelocity.magnitude; // kinematic velocity is set by your mover; OK to read

        float freq = run ? Mathf.Min(baseFrequency + speedToFrequency * speed, maxFrequency) : 0f;
        if (run) phase += dt * freq;
        phase %= 1024f;

        float t = (phase + phaseOffset) * Mathf.PI * 2f;

        // Desired angles
        float hipAngleLocal = hipMid + hipSwing * Mathf.Sin(t);
        float s = Mathf.Sin(t + kneeLead * Mathf.PI * 2f);
        float knee01 = Mathf.Clamp01(0.5f * (s + 1f));
        float kneeAngleLocal = kneeBase + kneeFlex * knee01;

        // Hip targetRotation about local X (Axis)
        Quaternion delta = Quaternion.AngleAxis(hipDir * hipAngleLocal, Vector3.right);
        hip.targetRotation = Quaternion.Inverse(hipStartLocal * delta) * hipStartLocal;

        // Drives: strong X (swing), soft YZ for dangly gravity sway
        float move01 = Mathf.Clamp01(speed / 3f);
        hip.angularXDrive = MakeDrive(run ? hipXSpringRun : hipXSpringRest,
                                      run ? hipXDamperRun : hipXDamperRest);
        float yzSpring = Mathf.Lerp(hipYZSpringRest, run ? hipYZSpringRun : hipYZSpringRest, move01);
        float yzDamper = Mathf.Lerp(hipYZDamperRest, run ? hipYZDamperRun : hipYZDamperRest, move01);
        hip.angularYZDrive = MakeDrive(yzSpring, yzDamper);

        // Knee spring toward target
        var sp = knee.spring;
        sp.spring = run ? kneeSpringRun : kneeSpringRest;
        sp.damper = run ? kneeDamperRun : kneeDamperRest;
        sp.targetPosition = knee.angle + kneeDir * (kneeAngleLocal - knee.angle); // drive toward kneeAngleLocal
        knee.spring = sp;
        knee.useSpring = true;
    }

    static JointDrive MakeDrive(float spring, float
damper, float maxForce = 1e9f)
    {
        JointDrive d = new JointDrive();
        d.positionSpring = spring;
        d.positionDamper = damper;
        d.maximumForce = maxForce;
        return d;
    }
}