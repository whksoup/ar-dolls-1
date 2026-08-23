using UnityEngine;

public class DollTracker : MonoBehaviour
{
    [Header("Tracked Objects")]
    public Transform Doll;
    public Transform IndexFinger;
    //
    public Transform Thumb;
    public Transform Wrist;

    [Header("Rotation Offset (Euler Angles)")]
    [Header("Calibration Object")]
    public Transform offsetReference;
    public Vector3 rotationOffsetEuler;

    void Update()
    {
        if (Doll == null || IndexFinger == null || Thumb == null || Wrist == null)
            return;

        // --- Position: midpoint between index finger and thumb ---
        Vector3 midpoint = (IndexFinger.position + Thumb.position) * 0.5f;
        Doll.position = midpoint;

        // --- Rotation: wrist rotation + offset ---
        Quaternion offsetRotation = Quaternion.Euler(rotationOffsetEuler);
        Doll.rotation = Wrist.rotation * offsetReference.localRotation;
    }
}
