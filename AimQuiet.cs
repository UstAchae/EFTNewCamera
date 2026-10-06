using System;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.Animations;
using HarmonyLib;

namespace EFTNewCamera;

internal static class AimQuiet
{
    private static FieldInfo? _breathIntensity;
    private static FieldInfo? _shakeIntensity;
    private static FieldInfo? _breathFrequency;
    private static FieldInfo? _cameraSensitivity;
    private static FieldInfo? _processors;
    private static bool _patched;

    internal static void Install()
    {
        if (_patched)
            return;

        MethodInfo? process = AccessTools.Method(typeof(BreathEffector), "Process", new[] { typeof(float) });
        MethodInfo? prefix = typeof(AimQuiet).GetMethod(nameof(BreathPrefix), BindingFlags.NonPublic | BindingFlags.Static);
        if (process == null || prefix == null)
            return;

        _breathIntensity = AccessTools.Field(typeof(BreathEffector), "_breathIntensity");
        _shakeIntensity = AccessTools.Field(typeof(BreathEffector), "_shakeIntensity");
        _breathFrequency = AccessTools.Field(typeof(BreathEffector), "_breathFrequency");
        _cameraSensitivity = AccessTools.Field(typeof(BreathEffector), "_cameraSensetivity");
        _processors = AccessTools.Field(typeof(BreathEffector), "_processors");
        Harmony harmony = new Harmony(NewCameraPlugin.PluginGuid + ".quiet");
        harmony.Patch(process, prefix: new HarmonyMethod(prefix));
        _patched = true;
    }

    private static bool BreathPrefix(BreathEffector __instance)
    {
        if (!AimingThisPlayer(__instance))
            return true;

        SetFloat(_breathIntensity, __instance, 0f);
        SetFloat(_shakeIntensity, __instance, 0f);
        SetFloat(_breathFrequency, __instance, 0f);
        SetFloat(_cameraSensitivity, __instance, 0f);
        ClearProcessors(__instance);
        return false;
    }

    private static bool AimingThisPlayer(BreathEffector effector)
    {
        if (!NewCameraSwitch.Enabled || effector == null)
            return false;

        Player? player = Singleton<GameWorld>.Instance?.MainPlayer;
        if (player == null || !player.IsYourPlayer || player.Physical == null || player.HandsController == null || !player.HandsController.IsAiming)
            return false;

        try
        {
            return ReferenceEquals(player.Physical, effector.Physical);
        }
        catch
        {
            return false;
        }
    }

    private static void ClearProcessors(BreathEffector effector)
    {
        try
        {
            if (_processors?.GetValue(effector) is not Array processors)
                return;

            foreach (object processor in processors)
            {
                if (processor == null)
                    continue;

                MethodInfo? processRaw = AccessTools.Method(processor.GetType(), "ProcessRaw", new[] { typeof(float), typeof(float) });
                processRaw?.Invoke(processor, new object[] { 0f, 0f });
            }
        }
        catch
        {
        }
    }

    private static void SetFloat(FieldInfo? field, object instance, float value)
    {
        try
        {
            field?.SetValue(instance, value);
        }
        catch
        {
        }
    }
}
