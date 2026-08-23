using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using Oculus.Interaction.Input; // Namespace containing HandJointId enum
//
[System.Serializable]
public class JointGameObjectPair
{
    public HandJointId jointId;
    public GameObject gameObject;
}

public class HandJointsMapper : MonoBehaviour
{
    [Header("Hand Tracking Settings")]
    [Tooltip("Assign the OVRHand component.")]
    public OVRHand ovrHand; // Assign the OVRHand component via the Inspector

    [Tooltip("Enable to track the rotation of the joints.")]
    public bool trackRotation = true; // Whether to track rotation in addition to position

    [Tooltip("List of hand joints and their corresponding GameObjects.")]
    public List<JointGameObjectPair> jointMappings;

    private OVRSkeleton handSkeleton;

    // Dictionary to map HandJointId to the joint's Transform
    private Dictionary<HandJointId, Transform> jointTransforms = new Dictionary<HandJointId, Transform>();

    void Start()
    {
        if (ovrHand == null)
        {
            Debug.LogError("OVRHand reference is not assigned. Please assign it in the Inspector.");
            return;
        }

        // Get the OVRSkeleton component from the OVRHand
        handSkeleton = ovrHand.GetComponent<OVRSkeleton>();
        if (handSkeleton == null)
        {
            Debug.LogError("OVRSkeleton component not found on the assigned OVRHand.");
            return;
        }

        // Start coroutine to wait until the skeleton data is valid
        StartCoroutine(WaitForSkeletonData());
    }

    IEnumerator WaitForSkeletonData()
    {
        // Wait until the skeleton is initialized and data is valid
        while (!handSkeleton.IsInitialized || !handSkeleton.IsDataValid)
        {
            yield return null;
        }

        // Build the jointTransforms dictionary
        foreach (var bone in handSkeleton.Bones)
        {
            var jointId = (HandJointId)bone.Id;
            if (!jointTransforms.ContainsKey(jointId))
            {
                jointTransforms.Add(jointId, bone.Transform);
            }
        }

        // Validate joint mappings
        foreach (var mapping in jointMappings)
        {
            if (!jointTransforms.ContainsKey(mapping.jointId))
            {
                Debug.LogError($"Joint {mapping.jointId} not found in the hand skeleton.");
            }
        }
    }

    void Update()
    {
        if (jointTransforms.Count == 0)
        {
            return;
        }

        foreach (var mapping in jointMappings)
        {
            if (mapping.gameObject != null && jointTransforms.ContainsKey(mapping.jointId))
            {
                Transform jointTransform = jointTransforms[mapping.jointId];

                // Apply the position of the joint to the assigned GameObject
                mapping.gameObject.transform.position = jointTransform.position;

                if (trackRotation)
                {
                    // Apply the rotation of the joint to the assigned GameObject
                    mapping.gameObject.transform.rotation = jointTransform.rotation;
                }
            }
        }
    }

    // Editor-only code to automatically populate jointMappings with all HandJointIds
#if UNITY_EDITOR
    private void OnValidate()
    {
        if (jointMappings == null || jointMappings.Count == 0)
        {
            InitializeJointMappings();
        }
        else if (jointMappings.Count != (int)HandJointId.HandEnd)
        {
            // Update the list if new joints have been added
            InitializeJointMappings();
        }
    }

    private void InitializeJointMappings()
    {
        jointMappings = new List<JointGameObjectPair>();

        for (int i = 0; i < (int)HandJointId.HandEnd; i++)
        {
            HandJointId jointId = (HandJointId)i;
            JointGameObjectPair mapping = new JointGameObjectPair
            {
                jointId = jointId,
                gameObject = null
            };
            jointMappings.Add(mapping);
        }
    }
#endif
}
