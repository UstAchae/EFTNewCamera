using EFT;
using UnityEngine;

namespace EFTNewCamera;

internal static class CameraPosition
{
    // Across, as it is now: the set place (Across), or, aimed, the aiming eye's (AimEye).
    internal static float Horizontal;
    internal static float Across;
    // Aimed, the camera goes over to the eye the gun is shouldered for: the right one, or the left one with the gun on
    // the left shoulder (the game's left stance), the gun with it (AimArms places it from the camera).
    internal static bool AimEyeEnabled = true;
    internal static float AimEye = 0.03f;            // the eye from the head's middle (m)
    internal static float Vertical;
    internal static float Forward;
    // How far further ahead the camera is while a magnified sight is aimed through the goggle (NightVisionScope.cs):
    // part of the camera's place from the head like the rest, so that what works from that place (the gun's carry,
    // which takes it off and puts it back at the body's own turn) moves it as it does them.
    internal static float ScopeAhead;

    private const float AimInSeconds = 0.2f;
    private const float AimOutSeconds = 0.35f;       // as long as the gun takes back to the hip (AimArms.LeaveSeconds)
    private const float SideSeconds = 0.3f;
    private static float _aimed;
    private static float _side = 1f;

    // Each frame, before the camera is placed: how far over to the aiming eye, aimed as at the chest (ChestStance).
    internal static void UpdateAimEye(Player player)
    {
        bool aimed = AimEyeEnabled && player.HandsController != null && player.HandsController.IsAiming;
        float target = aimed ? 1f : 0f;
        _aimed = Mathf.MoveTowards(_aimed, target, Time.deltaTime / (target > _aimed ? AimInSeconds : AimOutSeconds));
        bool left = false;
        try { left = player.MovementContext?.LeftStanceController?.LeftStance == true; } catch { }
        _side = Mathf.MoveTowards(_side, left ? -1f : 1f, 2f * Time.deltaTime / SideSeconds);
        float u = 0.5f * (_side + 1f);
        float side = 2f * u * u * (3f - 2f * u) - 1f;
        float w = _aimed * _aimed * (3f - 2f * _aimed);
        Horizontal = Mathf.Lerp(Across, side * AimEye, w);
    }

    // How far over to the aiming eye (0 to 1, eased), and which eye: from -1 the left to 1 the right, eased between.
    internal static float AimedWeight => _aimed * _aimed * (3f - 2f * _aimed);
    internal static float AimSide
    {
        get
        {
            float u = 0.5f * (_side + 1f);
            return 2f * u * u * (3f - 2f * u) - 1f;
        }
    }

    internal static void ResetAimEye()
    {
        _aimed = 0f;
        Horizontal = Across;
    }

    internal static Vector3 Resolve(Vector3 headPosition, Quaternion cameraRotation)
    {
        return headPosition + Offset(cameraRotation);
    }

    // Where the camera stands from the head, along the world's axes.
    internal static Vector3 Offset(Quaternion cameraRotation) => cameraRotation * new Vector3(Horizontal, Vertical, Forward + ScopeAhead);
}
