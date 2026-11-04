using UnityEngine;

namespace EFTNewCamera;

internal static class CameraPosition
{
    internal static float Horizontal;
    internal static float Vertical;
    internal static float Forward;
    // How far further ahead the camera is while a magnified sight is aimed through the goggle (NightVisionScope.cs):
    // part of the camera's place from the head like the rest, so that what works from that place (the gun's carry,
    // which takes it off and puts it back at the body's own turn) moves it as it does them.
    internal static float ScopeAhead;

    internal static Vector3 Resolve(Vector3 headPosition, Quaternion cameraRotation)
    {
        return headPosition + Offset(cameraRotation);
    }

    // Where the camera stands from the head, along the world's axes.
    internal static Vector3 Offset(Quaternion cameraRotation) => cameraRotation * new Vector3(Horizontal, Vertical, Forward + ScopeAhead);
}
