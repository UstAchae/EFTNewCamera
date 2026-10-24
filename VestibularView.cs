using EFT;
using UnityEngine;

namespace EFTNewCamera;

// Works like the vestibulo-ocular reflex: the camera stays where the head puts it, and only turns so it keeps
// looking at one point on the line the player means to look along (the mouse look plus free look), the set
// distance ahead of a steady eye. The head's bob, its turn and its tilt from each step are turned out of view.
// Slow tilt (leaning) is kept. Aiming, mounting, scene guns, vaulting, climbing and the BTR are left as they are.
internal static class VestibularView
{
    internal static float Strength;
    internal static float FixationDistance = 5f;

    private const float FadeSeconds = 0.2f;
    // Two of these in a row separate a step (about two a second) from leaning or changing pose.
    private const float StepSeconds = 0.25f;

    private static float _weight;
    private static bool _has;
    private static int _owner;
    private static Vector3 _eyeFirst;
    private static Vector3 _eyeSteady;
    private static float _rollFirst;
    private static float _rollSteady;

    internal static void Reset()
    {
        _has = false;
        _weight = 0f;
    }

    internal static Quaternion Stabilize(Player player, Vector3 cameraPosition, Quaternion cameraRotation)
    {
        MovementContext? movement = player.MovementContext;
        if (movement == null || Strength <= 0f)
        {
            Reset();
            return cameraRotation;
        }

        Quaternion intended = AimArms.FreeLookTurn(player) * player.HandsRotation;
        Vector3 forward = intended * Vector3.forward;
        // The head sits on the body, so its steady place is kept in the body's own turn, not the mouse's.
        Quaternion yawFrame = Quaternion.Euler(0f, player.Transform.rotation.eulerAngles.y, 0f);
        Vector3 root = player.Transform.position;
        Vector3 eye = Quaternion.Inverse(yawFrame) * (cameraPosition - root);
        float roll = Roll(Quaternion.Inverse(intended) * cameraRotation);

        // The filters run every frame, also while the effect is off, so nothing jumps when it comes back.
        int owner = player.GetInstanceID();
        if (!_has || owner != _owner)
        {
            _eyeFirst = _eyeSteady = eye;
            _rollFirst = _rollSteady = roll;
            _owner = owner;
            _has = true;
        }
        else
        {
            float follow = 1f - Mathf.Exp(-Time.deltaTime / StepSeconds);
            _eyeFirst = Vector3.Lerp(_eyeFirst, eye, follow);
            _eyeSteady = Vector3.Lerp(_eyeSteady, _eyeFirst, follow);
            _rollFirst += Mathf.DeltaAngle(_rollFirst, roll) * follow;
            _rollSteady += Mathf.DeltaAngle(_rollSteady, _rollFirst) * follow;
        }

        bool free = !(player.HandsController != null && player.HandsController.IsAiming)
            && !movement.IsInMountedState
            && !movement.IsStationaryWeaponInHands
            && movement.RotationAction == MovementContext.DefaultRotationFunction
            && player.BtrState == EPlayerBtrState.Outside;
        _weight = Mathf.MoveTowards(_weight, free ? 1f : 0f, Time.deltaTime / FadeSeconds);
        float amount = Strength * _weight * _weight * (3f - 2f * _weight);
        if (amount <= 0.0001f)
            return cameraRotation;

        Vector3 target = root + yawFrame * _eyeSteady + forward * FixationDistance;
        Vector3 toTarget = target - cameraPosition;
        if (toTarget.sqrMagnitude < 0.0001f)
            toTarget = forward;
        Vector3 up = Quaternion.AngleAxis(_rollSteady, forward) * (intended * Vector3.up);
        Quaternion steady = Quaternion.LookRotation(toTarget, up);
        return Quaternion.Slerp(cameraRotation, steady, amount);
    }

    // Turn about the view axis, in degrees, of a rotation given against the intended look.
    private static float Roll(Quaternion relative)
    {
        Vector3 forward = relative * Vector3.forward;
        if (forward.sqrMagnitude < 0.0001f)
            return 0f;
        Vector3 up = Quaternion.Inverse(Quaternion.LookRotation(forward)) * (relative * Vector3.up);
        return Mathf.Atan2(-up.x, up.y) * Mathf.Rad2Deg;
    }
}
