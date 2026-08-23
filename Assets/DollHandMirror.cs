using UnityEngine;

public class DollHandMirror : MonoBehaviour
{
    [Header("Real World References")]
    [Tooltip("Your actual left hand/wrist transform from OVR/XR")]
    public Transform playerLeftHand;

    [Tooltip("Your actual head/camera transform")]
    public Transform playerHead;

    [Header("Doll References")]
    [Tooltip("The totem on the doll representing your head position")]
    public Transform dollTotem;

    [Tooltip("The toy left hand that will mirror your movements")]
    public Transform dollToyLeftHand;

    [Header("Settings")]
    [Tooltip("Scale factor of the doll relative to player (0.1 = 1/10th size)")]
    public float dollScale = 0.1f;

    [Tooltip("Should rotation be mirrored as well?")]
    public bool mirrorRotation = true;

    void LateUpdate()
    {
        // We'll use LateUpdate to ensure all tracking has been updated

        if (!ValidateReferences())
            return;

        // 1. Get the offset from player head to player left hand
        Vector3 positionOffset = playerLeftHand.position - playerHead.position;
        Quaternion rotationOffset = Quaternion.Inverse(playerHead.rotation) * playerLeftHand.rotation;

        // 2. Scale the position offset
        Vector3 scaledPositionOffset = positionOffset * dollScale;

        // 3. Apply to doll toy hand relative to totem
        dollToyLeftHand.position = dollTotem.position + dollTotem.rotation * scaledPositionOffset;

        if (mirrorRotation)
        {
            dollToyLeftHand.rotation = dollTotem.rotation * rotationOffset;
        }
    }

    bool ValidateReferences()
    {
        if (playerLeftHand == null ||
            dollTotem == null || dollToyLeftHand == null)
        {
            Debug.LogWarning("DollHandMirror: Missing references!");
            return false;
        }
        return true;
    }
}