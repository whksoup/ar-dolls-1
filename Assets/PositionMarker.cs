using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class PositionMarker : MonoBehaviour
{
    // Public GameObject variable
    public GameObject targetObject;

    // Variable to store the last known position
    private Vector3 lastKnownPosition;

    // Start is called before the first frame update
    void Start()
    {
        // Initialize lastKnownPosition with the object's current position at start
        lastKnownPosition = transform.position;
    }

    // Update is called once per frame
    void Update()
    {
        // Check if the targetObject is set and active in the hierarchy
        if (targetObject != null && targetObject.activeInHierarchy)
        {
            // Update the object's position to match the target object's position
            transform.position = targetObject.transform.position;

            // Store the current position as the last known position
            lastKnownPosition = transform.position;
        }
        else
        {
            // If targetObject is deactivated or doesn't exist, stay in the last known position
            transform.position = lastKnownPosition;
        }
    }
}
