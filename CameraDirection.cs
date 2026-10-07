using UnityEngine;

namespace EFTNewCamera;

internal static class CameraDirection
{
    internal static float Pitch;
    internal static float Yaw;
    internal static float Roll;

    internal static Quaternion Resolve(Quaternion headRotation)
    {
        Quaternion alignment =
            Quaternion.AngleAxis(Pitch, Vector3.right) *
            Quaternion.AngleAxis(Yaw, Vector3.up) *
            Quaternion.AngleAxis(Roll, Vector3.forward);
        return headRotation * alignment;
    }
}
