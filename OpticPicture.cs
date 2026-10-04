using EFT;
using EFT.Animations;
using EFT.CameraControl;
using UnityEngine;

namespace EFTNewCamera;

internal static class OpticPicture
{
    private static int _frame = -1;
    private static OpticSight? _held;

    internal static void Apply(Player player)
    {
        if (_frame == Time.frameCount)
            return;
        _frame = Time.frameCount;

        try
        {
            ProceduralWeaponAnimation? animation = player.ProceduralWeaponAnimation;
            OpticCameraManager? manager = CameraManager.Instance?.OpticCameraManager;
            bool aiming = player.HandsController != null && player.HandsController.IsAiming;
            if (animation == null || manager == null || !aiming || animation.CurrentScope == null || !animation.CurrentScope.IsOptic)
            {
                ReleaseHeld();
                return;
            }

            OpticSight? sight = Resolve(animation);
            if (sight == null || !sight.gameObject.activeInHierarchy)
                return;

            OpticSight? current = manager.CurrentOpticSight;
            if (current != null && !Same(current, sight))
            {
                OpticSight.OpticSightState.Value = new OpticSight.OpticSightStatus
                {
                    IsEnabled = false,
                    OpticSight = current
                };
            }

            OpticSight.OpticSightStatus state = OpticSight.OpticSightState.Value;
            if (!state.IsEnabled || !Same(state.OpticSight, sight))
            {
                OpticSight.OpticSightState.Value = new OpticSight.OpticSightStatus
                {
                    IsEnabled = true,
                    OpticSight = sight
                };
            }

            _held = sight;
        }
        catch
        {
        }
    }

    internal static void Release()
    {
        try
        {
            if (_held != null)
            {
                OpticSight.OpticSightState.Value = new OpticSight.OpticSightStatus
                {
                    IsEnabled = true,
                    OpticSight = _held
                };
            }
        }
        catch
        {
        }

        _held = null;
        _frame = -1;
    }

    private static void ReleaseHeld()
    {
        if (_held == null)
            return;

        try
        {
            OpticSight.OpticSightStatus state = OpticSight.OpticSightState.Value;
            if (Same(state.OpticSight, _held))
            {
                OpticSight.OpticSightState.Value = new OpticSight.OpticSightStatus
                {
                    IsEnabled = false,
                    OpticSight = _held
                };
            }
        }
        catch
        {
        }

        _held = null;
    }

    private static OpticSight? Resolve(ProceduralWeaponAnimation animation)
    {
        Transform? bone = animation.CurrentScope?.Bone;
        if (bone == null)
            return null;

        ScopePrefabCache? cache = bone.GetComponentInParent<ScopePrefabCache>(true);
        if (cache != null && cache.CurrentModHasOptics)
            return cache.CurrentModOpticSight;

        return bone.GetComponentInParent<OpticSight>(true);
    }

    private static bool Same(Object? left, Object? right)
    {
        if (left == null || right == null)
            return left == right;

        return left.GetInstanceID() == right.GetInstanceID();
    }
}
