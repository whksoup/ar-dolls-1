using UnityEngine;

public class DynamicParenting : MonoBehaviour
{
    [SerializeField] private KeyCode anchorButton = KeyCode.Space; // Button to trigger unparenting
    [SerializeField] private float distanceThreshold = 2.0f; // Distance at which to reparent
    [SerializeField] private Vector3 localPosition; // Stores the local position relative to parent
    [SerializeField] private Vector3 localEulerAngles; // Stores the local rotation relative to parent
    public OVRInput.RawButton anchorButtonOVR = OVRInput.RawButton.Y; 

    private Transform originalParent; // Stores the original parent
    public bool isParented = true; // Tracks parenting state
    private Vector3 originalWorldPosition; // Stores world position when unparented

    void Start()
    {
        // Store initial parent, local position, and rotation
        originalParent = transform.parent;
        localPosition = transform.localPosition;
        localEulerAngles = transform.localEulerAngles;
    }

    void Update()
    {
        if (OVRInput.GetDown(anchorButtonOVR)||Input.GetKeyDown(anchorButton))
        {
            ToggleParenting();
        }

        if (!isParented)
        {
            CheckDistanceAndReparent();
        }
    }

    private void ToggleParenting()
    {
        if (isParented)
        {
            // Unparent the object
            originalWorldPosition = transform.position;
            originalParent = transform.parent;
            transform.SetParent(null);
            isParented = false;
            Debug.Log("Unparented from " + originalParent.name);
        }
        else
        {
            // Reparent the object
            ReparentToOriginal();
        }
    }

    private void CheckDistanceAndReparent()
    {
        if (originalParent != null)
        {
            float currentDistance = Vector3.Distance(transform.position, originalParent.TransformPoint(localPosition));
            
            if (currentDistance > distanceThreshold)
            {
                ReparentToOriginal();
            }
        }
    }

    private void ReparentToOriginal()
    {
        if (originalParent != null)
        {
            transform.SetParent(originalParent);
            transform.localPosition = localPosition;
            transform.localEulerAngles = localEulerAngles;
            isParented = true;
            Debug.Log("Reparented to " + originalParent.name + " with original position and rotation");
        }
    }

    // Optional: Draw a gizmo to visualize the distance threshold
    private void OnDrawGizmosSelected()
    {
        if (!isParented && originalParent != null)
        {
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(originalParent.TransformPoint(localPosition), distanceThreshold);
        }
    }
}