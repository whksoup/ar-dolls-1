using UnityEngine;

public class PuppetStringController : MonoBehaviour
{
    [Header("Puppet Parts")]
    [SerializeField] private Transform puppetHand;
    [SerializeField] private Transform leftFinger;

    [Header("Distance Settings")]
    [SerializeField] private float tetherRange = 2f; // Max distance before string breaks

    [Header("Y Movement Bounds")]
    [SerializeField] private float minLocalY = -1f; // Lower bound for hand movement
    [SerializeField] private float maxLocalY = 1f; // Upper bound for hand movement

    [Header("Movement Settings")]
    [SerializeField] private float movementMultiplier = 2f; // Amplifies finger movement (1 = same speed, 2 = twice as fast)
    [SerializeField] private float returnSpeed = 5f; // Speed to return to base position

    [Header("Visual Settings")]
    [SerializeField] private LineRenderer stringLine;
    [SerializeField] private Color stringColor = Color.white;
    [SerializeField] private float stringWidth = 0.02f;

    private bool isTethered = false;
    private float lastFingerYPosition;
    private Vector3 baseLocalPosition = Vector3.zero;

    void Start()
    {
        // Setup LineRenderer if not assigned
        if (stringLine == null)
        {
            GameObject lineObj = new GameObject("PuppetString");
            stringLine = lineObj.AddComponent<LineRenderer>();
        }

        stringLine.startWidth = stringWidth;
        stringLine.endWidth = stringWidth;
        stringLine.material = new Material(Shader.Find("Sprites/Default"));
        stringLine.startColor = stringColor;
        stringLine.endColor = stringColor;
        stringLine.enabled = false;

        lastFingerYPosition = leftFinger.position.y;
    }

    void Update()
    {
        float distanceToFinger = Vector3.Distance(leftFinger.position, puppetHand.position);

        // Check tether state
        if (distanceToFinger > tetherRange)
        {
            // Out of range - break tether
            if (isTethered)
            {
                BreakTether();
            }
        }
        else
        {
            // Within range - establish tether if not already tethered
            if (!isTethered)
            {
                EstablishTether();
            }
        }

        // Update movement
        if (isTethered)
        {
            MirrorFingerY();
            DrawString();
        }
        else
        {
            ReturnToBase();
        }
    }

    void EstablishTether()
    {
        isTethered = true;
        stringLine.enabled = true;
        lastFingerYPosition = leftFinger.position.y;
        Debug.Log("Tether established!");
    }

    void BreakTether()
    {
        isTethered = false;
        stringLine.enabled = false;
        Debug.Log("Tether broken!");
    }

    void MirrorFingerY()
    {
        // Calculate Y delta
        float fingerYDelta = leftFinger.position.y - lastFingerYPosition;

        // Apply movement multiplier
        float amplifiedDelta = fingerYDelta * movementMultiplier;

        // Get current local position
        Vector3 newLocalPosition = puppetHand.localPosition;

        // Apply amplified Y delta
        newLocalPosition.y += amplifiedDelta;

        // Clamp to bounds
        newLocalPosition.y = Mathf.Clamp(newLocalPosition.y, minLocalY, maxLocalY);

        // Apply new position (XZ remain unchanged)
        puppetHand.localPosition = newLocalPosition;

        // Update last finger position
        lastFingerYPosition = leftFinger.position.y;
    }

    void ReturnToBase()
    {
        // Smoothly return to base position (0, 0, 0)
        if (Vector3.Distance(puppetHand.localPosition, baseLocalPosition) > 0.01f)
        {
            puppetHand.localPosition = Vector3.Lerp(
                puppetHand.localPosition,
                baseLocalPosition,
                Time.deltaTime * returnSpeed
            );
        }
        else
        {
            // Snap to exact base position when close enough
            puppetHand.localPosition = baseLocalPosition;
        }
    }

    void DrawString()
    {
        stringLine.SetPosition(0, leftFinger.position);
        stringLine.SetPosition(1, puppetHand.position);
    }

    void OnDrawGizmos()
    {
        if (puppetHand == null || leftFinger == null) return;

        // Draw tether range sphere around hand
        Gizmos.color = isTethered ? Color.green : Color.red;
        Gizmos.DrawWireSphere(puppetHand.position, tetherRange);

        // Draw Y bounds (visualize in world space relative to hand's parent)
        if (puppetHand.parent != null)
        {
            Vector3 parentPos = puppetHand.parent.position;
            Vector3 minBound = puppetHand.parent.TransformPoint(new Vector3(0, minLocalY, 0));
            Vector3 maxBound = puppetHand.parent.TransformPoint(new Vector3(0, maxLocalY, 0));

            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(minBound, 0.1f);
            Gizmos.DrawWireSphere(maxBound, 0.1f);
            Gizmos.DrawLine(minBound, maxBound);
        }
    }
}