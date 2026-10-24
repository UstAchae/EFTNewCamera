using EFT;
using UnityEngine;

namespace EFTNewCamera;

// Works like the vestibulo-ocular reflex: the camera stays where the head puts it, and only turns to look along
// the line the player means to look along (the mouse look plus free look), as if at a point far away. The head's
// turn from each step is turned out of view; its bob still moves the camera. Yaw and pitch are balanced fully; the
// turn about the view axis (roll) only by the torsion share, as real eyes counter-roll only a small part of a head
// tilt. Aiming, mounting, scene guns, vaulting, climbing and the BTR are left as they are.
// While the hands or body are busy (switching or reloading, meds, inventory, looting, doors) the animation turns
// the head on purpose, to look at the hands or the bag. That turn is then part of where the player looks.
internal static class VestibularView
{
    internal static bool Enabled = true;
    internal static float Strength;
    internal static float Torsion = 0.25f;

    // How quickly the view takes on the head turn of an action, and lets it go after.
    private const float ActionFadeSeconds = 0.25f;
    // The head turn kept during an action is smoothed by this much, which takes out most of a step's shake
    // but follows the animation turning the head (about 0.3 s) closely.
    private const float ActionLookSeconds = 0.15f;
    private static float _action;
    private static Quaternion _actionLook = Quaternion.identity;

    private const float FadeSeconds = 0.2f;
    private static float _weight;

    internal static void Reset()
    {
        _weight = 0f;
        _action = 0f;
        _actionLook = Quaternion.identity;
    }

    internal static Quaternion Stabilize(Player player, Vector3 cameraPosition, Quaternion cameraRotation)
    {
        MovementContext? movement = player.MovementContext;
        if (movement == null || !Enabled || Strength <= 0f)
        {
            Reset();
            return cameraRotation;
        }

        // The bones, and the helmet seen at the edge of the view, are posed with the previous movement update's
        // mouse. Balancing to the newest mouse would put the view a frame ahead of the head, and the helmet
        // would step against it whenever the mouse input comes unevenly. The BTR keeps the game's own rotator.
        Quaternion look = player.BtrState == EPlayerBtrState.Outside ? AimArms.PosedLook(player) : player.HandsRotation;
        Quaternion intended = AimArms.FreeLookTurn(player) * look;

        // The head's own turn against that look; kept, smoothed, while an action animation is turning it.
        _actionLook = Quaternion.Slerp(_actionLook, Quaternion.Inverse(intended) * cameraRotation,
            1f - Mathf.Exp(-Time.deltaTime / ActionLookSeconds)).normalized;
        _action = Mathf.MoveTowards(_action, Busy(player) ? 1f : 0f, Time.deltaTime / ActionFadeSeconds);
        if (_action > 0f)
            intended = intended * Quaternion.Slerp(Quaternion.identity, _actionLook, _action * _action * (3f - 2f * _action));
        Vector3 forward = intended * Vector3.forward;

        bool free = !(player.HandsController != null && player.HandsController.IsAiming)
            && !movement.IsInMountedState
            && !movement.IsStationaryWeaponInHands
            && movement.RotationAction == MovementContext.DefaultRotationFunction
            && player.BtrState == EPlayerBtrState.Outside;
        _weight = Mathf.MoveTowards(_weight, free ? 1f : 0f, Time.deltaTime / FadeSeconds);
        float amount = Strength * _weight * _weight * (3f - 2f * _weight);
        if (amount <= 0.0001f)
            return cameraRotation;

        // The shortest turn from where the camera looks onto the intended line: it has no part about the view axis.
        Quaternion steady = Quaternion.FromToRotation(cameraRotation * Vector3.forward, forward) * cameraRotation;
        if (Torsion > 0f)
        {
            float roll = Roll(Quaternion.Inverse(intended) * steady);
            steady = Quaternion.AngleAxis(-roll * Torsion, steady * Vector3.forward) * steady;
        }
        return Quaternion.Slerp(cameraRotation, steady, amount);
    }

    // Hands or body in an action whose animation looks somewhere on its own.
    private static bool Busy(Player player)
    {
        try
        {
            Player.AbstractHandsController? hands = player.HandsController as Player.AbstractHandsController;
            if (hands != null && hands.IsInventoryOpen())
                return true;
            if (player.HandsController is Player.MedsController || player.HandsController is Player.QuickUseItemController)
                return true;
            // Sprinting is left out: actions stop a sprint, and the sprint pose may not count as the hands' idle.
            if (player.MovementContext?.CurrentState?.Name != EPlayerState.Sprint
                && player.HandsController is Player.FirearmController firearm && firearm.FirearmsAnimator != null
                && (!firearm.FirearmsAnimator.IsIdling() || firearm.FirearmsAnimator.Animator.IsInTransition(FirearmsAnimator.HANDS_LAYER_INDEX)))
                return true;
        }
        catch (System.Exception)
        {
        }

        switch (player.MovementContext?.CurrentState?.Name)
        {
            case EPlayerState.Loot:
            case EPlayerState.Pickup:
            case EPlayerState.Open:
            case EPlayerState.Close:
            case EPlayerState.Unlock:
            case EPlayerState.BreachDoor:
            case EPlayerState.DoorInteraction:
            case EPlayerState.Approach:
            case EPlayerState.Plant:
                return true;
            default:
                return false;
        }
    }

    // Turn about the view axis, in degrees, of a rotation given against the intended look.
    private static float Roll(Quaternion relative)
    {
        Vector3 forward = relative * Vector3.forward;
        Vector3 up = Quaternion.Inverse(Quaternion.LookRotation(forward)) * (relative * Vector3.up);
        return Mathf.Atan2(-up.x, up.y) * Mathf.Rad2Deg;
    }
}
