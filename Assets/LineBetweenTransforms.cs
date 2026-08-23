using UnityEngine;

[RequireComponent(typeof(LineRenderer))]
public class LineBetweenTransforms : MonoBehaviour
{
    [Header("Points")]
    public Transform pointA;
    public Transform pointB;

    [Header("Line Settings")]
    public float thickness = 0.05f;
    public bool useWorldSpace = true;

    private LineRenderer line;

    void Awake()
    {
        line = GetComponent<LineRenderer>();

        line.positionCount = 2;
        line.useWorldSpace = useWorldSpace;

        // Thickness
        line.startWidth = thickness;
        line.endWidth = thickness;

        // Optional: make it visible by default
        line.material = new Material(Shader.Find("Sprites/Default"));
        line.startColor = Color.white;
        line.endColor = Color.white;
    }

    void Update()
    {
        if (pointA == null || pointB == null)
            return;

        line.SetPosition(0, pointA.position);
        line.SetPosition(1, pointB.position);
    }
}
