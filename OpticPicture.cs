using EFT;
using EFT.Animations;
using EFT.CameraControl;
using UnityEngine;

namespace EFTNewCamera;

internal static class OpticPicture
{
    private static int _frame = -1;
    private static OpticSight? _held;

    internal static bool IsCamera(Camera camera)
    {
        try
        {
            Camera? optic = CameraManager.Instance?.OpticCameraManager?.Camera;
            return optic != null && optic.GetInstanceID() == camera.GetInstanceID();
        }
        catch
        {
            return false;
        }
    }

    internal static void Apply(Player player)
    {
        if (_frame == Time.frameCount)
            return;
        _frame = Time.frameCount;

        try
        {
            ProceduralWeaponAnimation? animation = player.ProceduralWeaponAnimation;
            OpticCameraManager? manager = CameraManager.Instance?.OpticCameraManager;
            if (animation == null || manager == null)
            {
                ReleaseHeld();
                return;
            }

            bool aiming = player.HandsController != null && player.HandsController.IsAiming;
            ProceduralWeaponAnimation.SightNBone? scope = animation.CurrentScope;
            OpticSight? picture = null;
            if (aiming && scope != null)
            {
                OpticSight? sight = Resolve(animation);
                if (WantsPicture(scope, sight) && sight != null && sight.gameObject.activeInHierarchy)
                    picture = sight;
            }

            // 正在看普通放大镜时，画面留给它。否则打开枪上那块自己出画面的屏幕（MP-155 智能瞄具）。
            OpticSight? screen = picture == null ? FindScreen(animation) : null;
            OpticSight? show = picture ?? screen;
            if (show == null)
            {
                ReleaseHeld();
                return;
            }

            QuietOthers(animation, show);
            Hold(show);
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
                OpticSight.OpticSightState.Value = new OpticSight.GStruct421
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
            OpticSight.GStruct421 state = OpticSight.OpticSightState.Value;
            if (Same(state.OpticSight, _held))
            {
                OpticSight.OpticSightState.Value = new OpticSight.GStruct421
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

    // 普通放大瞄具走 IsOptic。斜边热成像和带专用瞄准面的智能瞄具（MP-155 Ultima）不是这条，
    // 但身上仍有热成像或夜视画面，第三人称下也要打开。
    private static bool WantsPicture(ProceduralWeaponAnimation.SightNBone scope, OpticSight? sight)
    {
        if (scope.IsOptic)
            return true;

        if (sight != null && sight.IsThermalSightAvailableAt45Degrees)
            return true;

        if (scope.ScopePrefabCache != null && scope.ScopePrefabCache.CurrentModHasOptics)
            return true;

        if (scope.Mod != null && !string.IsNullOrEmpty(scope.Mod.CustomAimPlane) && sight != null)
            return true;

        return HasScopeFilter(sight);
    }

    private static bool HasScopeFilter(OpticSight? sight)
    {
        if (sight?.ScopeData == null)
            return false;

        try
        {
            object data = sight.ScopeData;
            if (Flag(data, "ThermalVisionData", "ThermalVision"))
                return true;

            return Flag(data, "NightVisionData", "NightVision");
        }
        catch
        {
            return false;
        }
    }

    private static bool Flag(object owner, string childName, string flagName)
    {
        object? child = owner.GetType().GetProperty(childName)?.GetValue(owner)
            ?? owner.GetType().GetField(childName)?.GetValue(owner);
        if (child == null)
            return false;

        object? flag = child.GetType().GetProperty(flagName)?.GetValue(child)
            ?? child.GetType().GetField(flagName)?.GetValue(child);
        return flag is bool on && on;
    }

    // 第三人称下游戏不会跑第一人称那套开关。智能瞄具的屏幕不占当前瞄准位，也要单独打开。
    private static OpticSight? FindScreen(ProceduralWeaponAnimation animation)
    {
        if (animation.ScopeAimTransforms == null)
            return null;

        OpticSight? screen = null;
        foreach (ProceduralWeaponAnimation.SightNBone sat in animation.ScopeAimTransforms)
        {
            ScopePrefabCache? cache = sat.ScopePrefabCache;
            if (cache == null || !cache.CurrentModHasOptics || cache.CurrentModOpticSight == null)
                continue;

            bool ownScreen = cache.CurrentModIgnoreOpticsForCameraPlane
                || (sat.Mod != null && !string.IsNullOrEmpty(sat.Mod.CustomAimPlane));
            if (!ownScreen || !cache.CurrentModOpticSight.gameObject.activeInHierarchy)
                continue;

            screen = cache.CurrentModOpticSight;
        }

        return screen;
    }

    private static void QuietOthers(ProceduralWeaponAnimation animation, OpticSight keep)
    {
        if (animation.ScopeAimTransforms == null)
            return;

        foreach (ProceduralWeaponAnimation.SightNBone sat in animation.ScopeAimTransforms)
        {
            OpticSight? sight = sat.ScopePrefabCache != null ? sat.ScopePrefabCache.CurrentModOpticSight : null;
            if (sight == null || Same(sight, keep) || !sight.enabled)
                continue;

            sight.enabled = false;
        }
    }

    private static void Hold(OpticSight sight)
    {
        if (!sight.enabled)
            sight.enabled = true;
        sight.LensFade(false);

        OpticSight? current = CameraManager.Instance?.OpticCameraManager?.CurrentOpticSight;
        if (current != null && !Same(current, sight))
        {
            OpticSight.OpticSightState.Value = new OpticSight.GStruct421
            {
                IsEnabled = false,
                OpticSight = current
            };
        }

        OpticSight.GStruct421 state = OpticSight.OpticSightState.Value;
        if (!state.IsEnabled || !Same(state.OpticSight, sight))
        {
            OpticSight.OpticSightState.Value = new OpticSight.GStruct421
            {
                IsEnabled = true,
                OpticSight = sight
            };
        }

        _held = sight;
    }

    private static OpticSight? Resolve(ProceduralWeaponAnimation animation)
    {
        Transform? bone = animation.CurrentScope?.Bone;
        if (bone == null)
            return null;

        ScopePrefabCache? cache = bone.GetComponentInParent<ScopePrefabCache>(true);
        if (cache != null && cache.CurrentModHasOptics && cache.CurrentModOpticSight != null)
            return cache.CurrentModOpticSight;

        OpticSight? sight = bone.GetComponentInParent<OpticSight>(true);
        if (sight != null)
            return sight;

        return bone.GetComponentInChildren<OpticSight>(true);
    }

    private static bool Same(Object? left, Object? right)
    {
        if (left == null || right == null)
            return left == right;

        return left.GetInstanceID() == right.GetInstanceID();
    }
}
