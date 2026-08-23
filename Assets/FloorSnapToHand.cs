using UnityEngine;

public class FloorSnapToHand : MonoBehaviour
{
    public Transform Floor;
    public Transform LeftHand;

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.G))
        {
            if (Floor == null || LeftHand == null)
                return;

            // Snap position only (do not affect rotation)
            Floor.position = LeftHand.position;
        }
    }
}
