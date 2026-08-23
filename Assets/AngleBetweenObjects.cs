using UnityEngine;

[RequireComponent(typeof(LineRenderer))]
public class AlignAxisToLineWithRenderer : MonoBehaviour
{
    [Header("References")]
    public Transform objectA;
    public Transform objectB;
    public Transform objectC;


    public enum Axis { X, Y, Z }
    public Axis axisToAlign = Axis.Y;

    private LineRenderer lineRenderer;
    //
    void Awake()
    {
        lineRenderer = GetComponent<LineRenderer>();
        lineRenderer.positionCount = 2;
        lineRenderer.useWorldSpace = true;
        lineRenderer.startWidth = 0.02f;
        lineRenderer.endWidth = 0.02f;
    }

    void Update()
    {
        if (!objectA || !objectB || !objectC)
            return;

        Vector3 direction = objectC.position - objectB.position;

        if (direction.sqrMagnitude < 0.0001f)
            return;

        direction.Normalize();

        // --- Align chosen axis ---
        Quaternion targetRotation = objectA.rotation;

        switch (axisToAlign)
        {
            case Axis.X:
                targetRotation = Quaternion.FromToRotation(objectA.right, direction) * objectA.rotation;
                break;

            case Axis.Y:
                targetRotation = Quaternion.FromToRotation(objectA.up, direction) * objectA.rotation;
                break;

            case Axis.Z:
                targetRotation = Quaternion.FromToRotation(objectA.forward, direction) * objectA.rotation;
                break;
        }

        objectA.rotation = targetRotation;

        // --- Draw line from B to C ---
        lineRenderer.SetPosition(0, objectB.position);
        lineRenderer.SetPosition(1, objectC.position);
    }
}
