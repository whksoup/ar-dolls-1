using UnityEngine;

public class FingerRayRectangle : MonoBehaviour
{
    [Header("References")]
    public Transform middleOne;   // start / anchor
    public Transform middleTwo;   // direction reference
    public Transform rectangle;   // rectangle to extend

    [Header("Extension")]
    public float extensionLength = 0.2f; // how far the rectangle extends

    void Update()
    {
        if (middleOne == null || middleTwo == null || rectangle == null)
            return;

        Vector3 startPos = middleOne.position;
        Vector3 direction = middleTwo.position - startPos;

        if (direction.sqrMagnitude < 0.0001f)
            return;

        direction.Normalize();

        // Anchor rectangle at the start
        rectangle.position = startPos;

        // Rotate rectangle to align with the ray direction
        rectangle.rotation = Quaternion.LookRotation(direction);

        // Scale rectangle along its forward axis
        Vector3 scale = rectangle.localScale;
        scale.z = extensionLength; // assumes Z is the length axis
        rectangle.localScale = scale;
    }
}
