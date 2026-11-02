using UnityEngine;

namespace EFTNewCamera;

internal static class CameraPosition
{
    internal static float Horizontal;
    internal static float Vertical;
    internal static float Forward;

    internal static Vector3 Resolve(Vector3 headPosition, Quaternion cameraRotation)
    {
        return headPosition + Offset(cameraRotation);
    }

    // Where the camera stands from the head, along the world's axes.
    internal static Vector3 Offset(Quaternion cameraRotation) => cameraRotation * new Vector3(Horizontal, Vertical, Forward);
}
