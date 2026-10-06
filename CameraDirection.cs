using UnityEngine;

namespace EFTNewCamera;

internal static class CameraDirection
{
    internal static float Pitch;
    internal static float Yaw;
    internal static float Roll;

    internal static Quaternion Resolve(Quaternion headRotation)
    {
        return headRotation * Alignment();
    }

    internal static Quaternion Unresolve(Quaternion cameraRotation)
    {
        return cameraRotation * Quaternion.Inverse(Alignment());
    }

    private static Quaternion Alignment()
    {
        return Quaternion.AngleAxis(Pitch, Vector3.right) *
            Quaternion.AngleAxis(Yaw, Vector3.up) *
            Quaternion.AngleAxis(Roll, Vector3.forward);
    }
}
