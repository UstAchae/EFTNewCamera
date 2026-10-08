using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace EFTNewCamera;

internal static class VisorFilter
{
    internal static bool Show = true;

    private static readonly FieldInfo? VisorPointField = AccessTools.Field(typeof(VisorEffect), "_visorPoint");
    private static GameObject? _hiddenPoint;
    private static bool _installed;

    internal static void Install()
    {
        if (_installed)
            return;

        Harmony harmony = new Harmony(NewCameraPlugin.PluginGuid + ".visor");
        MethodInfo? render = AccessTools.Method(typeof(VisorEffect), "OnRenderImageImpl");
        MethodInfo? prefix = typeof(VisorFilter).GetMethod(nameof(OnRenderImageImplPrefix), BindingFlags.NonPublic | BindingFlags.Static);
        if (render != null && prefix != null)
            harmony.Patch(render, prefix: new HarmonyMethod(prefix));

        _installed = true;
    }

    private static bool OnRenderImageImplPrefix(VisorEffect __instance, RenderTexture source, RenderTexture destination)
    {
        if (!NewCameraSwitch.Enabled || Show)
        {
            if (_hiddenPoint != null)
            {
                if (_hiddenPoint)
                    _hiddenPoint.SetActive(true);
                _hiddenPoint = null;
            }

            return true;
        }

        if (VisorPointField?.GetValue(__instance) is GameObject point && point.activeSelf)
        {
            point.SetActive(false);
            _hiddenPoint = point;
        }

        RendererExtensions.BlitOrCopy(source, destination);
        return false;
    }
}
