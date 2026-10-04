using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.Animations;
using HarmonyLib;
using UnityEngine;

namespace EFTNewCamera;

internal static class AimSway
{
    internal static float Overall = 0.6f;
    internal static float WalkHorizontal = 0.5f;

    private static bool _patched;
    private static bool _scaled;
    private static float _breath;
    private static float _walk;
    private static float _positionIntensity;
    private static float _rotationIntensity;
    private static Vector3 _swayFactors;

    internal static void Install()
    {
        if (_patched)
            return;

        MethodInfo? target = AccessTools.Method(typeof(ProceduralWeaponAnimation), nameof(ProceduralWeaponAnimation.ProcessEffectors));
        MethodInfo? prefix = typeof(AimSway).GetMethod(nameof(ScalePrefix), BindingFlags.NonPublic | BindingFlags.Static);
        MethodInfo? postfix = typeof(AimSway).GetMethod(nameof(ScalePostfix), BindingFlags.NonPublic | BindingFlags.Static);
        if (target == null || prefix == null || postfix == null)
            return;

        Harmony harmony = new Harmony(NewCameraPlugin.PluginGuid + ".aimsway");
        harmony.Patch(target, prefix: new HarmonyMethod(prefix), postfix: new HarmonyMethod(postfix));
        _patched = true;
    }

    private static void ScalePrefix(ProceduralWeaponAnimation __instance)
    {
        _scaled = false;
        if (!NewCameraSwitch.Enabled || __instance.Breath == null || __instance.Walk == null || __instance.MotionReact == null || __instance.HandsContainer == null)
            return;

        Player? player = Singleton<GameWorld>.Instance?.MainPlayer;
        if (player == null || !player.IsYourPlayer || !ReferenceEquals(player.ProceduralWeaponAnimation, __instance))
            return;
        if (player.HandsController == null || !player.HandsController.IsAiming)
            return;

        float overall = Mathf.Clamp01(Overall);
        float horizontal = Mathf.Clamp01(WalkHorizontal);
        _breath = __instance.Breath.Intensity;
        _walk = __instance.Walk.Intensity;
        _swayFactors = __instance.MotionReact.SwayFactors;
        _positionIntensity = __instance.HandsContainer.HandsPosition.InputIntensity;
        _rotationIntensity = __instance.HandsContainer.HandsRotation.InputIntensity;
        _scaled = true;

        __instance.Breath.Intensity = _breath * overall;
        __instance.Walk.Intensity = _walk * overall * horizontal;
        __instance.MotionReact.SwayFactors = new Vector3(_swayFactors.x * overall * horizontal, _swayFactors.y * overall, _swayFactors.z * overall);
        __instance.HandsContainer.HandsPosition.InputIntensity = _positionIntensity * overall;
        __instance.HandsContainer.HandsRotation.InputIntensity = _rotationIntensity * overall;
    }

    private static void ScalePostfix(ProceduralWeaponAnimation __instance)
    {
        if (!_scaled)
            return;

        __instance.Breath.Intensity = _breath;
        __instance.Walk.Intensity = _walk;
        __instance.MotionReact.SwayFactors = _swayFactors;
        __instance.HandsContainer.HandsPosition.InputIntensity = _positionIntensity;
        __instance.HandsContainer.HandsRotation.InputIntensity = _rotationIntensity;
        _scaled = false;
    }
}
