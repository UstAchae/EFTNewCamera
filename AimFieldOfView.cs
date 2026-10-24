using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Comfort.Common;
using EFT;
using EFT.Animations;
using EFT.CameraControl;
using EFT.Settings;
using HarmonyLib;

namespace EFTNewCamera;

// The game narrows the view while aiming: game FOV - CameraManager.AimDeltaFov for iron sights and
// CameraManager.OpticsFov for optics. Both are constants, compiled into each caller as plain numbers,
// so the numbers are swapped where the aim FOV is worked out instead of undoing the result in SetFov.
internal static class AimFieldOfView
{
    private static bool _installed;

    internal static void Install()
    {
        if (_installed)
            return;

        Harmony harmony = new Harmony(NewCameraPlugin.PluginGuid + ".fov");
        HarmonyMethod transpiler = new HarmonyMethod(typeof(AimFieldOfView), nameof(KeepGameFov));
        PatchIfFound(harmony, AccessTools.Method(typeof(ProceduralWeaponAnimation), nameof(ProceduralWeaponAnimation.OnAimOrPoseChanged)), transpiler);
        PatchIfFound(harmony, AccessTools.Method(typeof(ProceduralWeaponAnimation), nameof(ProceduralWeaponAnimation.OnFovChange)), transpiler);
        // Third person only reaches this one: letting go of free look while aiming.
        PatchIfFound(harmony, AccessTools.Method(typeof(Player), nameof(Player.Look)), transpiler);
        _installed = true;
    }

    // Called once when the new camera takes over, in case a first-person aim or sprint FOV is still on.
    internal static void Reset()
    {
        if (!Singleton<SettingsManager>.Instantiated || CameraManager.Instance == null)
            return;

        CameraManager.Instance.SetFov(Singleton<SettingsManager>.Instance.Game.Settings.FieldOfView.Value, 0.3f);
    }

    private static void PatchIfFound(Harmony harmony, MethodInfo? target, HarmonyMethod transpiler)
    {
        if (target != null)
            harmony.Patch(target, transpiler: transpiler);
    }

    private static float IronSightDelta(float delta)
    {
        return NewCameraSwitch.Enabled ? 0f : delta;
    }

    private static float OpticFov(float fov)
    {
        if (!NewCameraSwitch.Enabled || !Singleton<SettingsManager>.Instantiated)
            return fov;

        return Singleton<SettingsManager>.Instance.Game.Settings.FieldOfView.Value;
    }

    // Only the numbers between the IsOptic check and the SetFov call are touched. Player.Look has
    // other 15s that have nothing to do with the view.
    private static IEnumerable<CodeInstruction> KeepGameFov(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo isOptic = AccessTools.PropertyGetter(typeof(ProceduralWeaponAnimation.SightNBone), nameof(ProceduralWeaponAnimation.SightNBone.IsOptic));
        MethodInfo setFov = AccessTools.Method(typeof(CameraManager), nameof(CameraManager.SetFov));
        MethodInfo delta = AccessTools.Method(typeof(AimFieldOfView), nameof(IronSightDelta));
        MethodInfo optic = AccessTools.Method(typeof(AimFieldOfView), nameof(OpticFov));
        List<CodeInstruction> list = new List<CodeInstruction>(instructions);
        bool armed = false;
        for (int i = 0; i < list.Count; i++)
        {
            CodeInstruction instruction = list[i];
            if (instruction.Calls(isOptic))
                armed = true;
            else if (instruction.Calls(setFov))
                armed = false;
            else if (armed && instruction.opcode == OpCodes.Ldc_R4 && instruction.operand is float value)
            {
                if (value == CameraManager.AimDeltaFov && i + 1 < list.Count && list[i + 1].opcode == OpCodes.Sub)
                    list.Insert(++i, new CodeInstruction(OpCodes.Call, delta));
                else if (value == CameraManager.OpticsFov)
                    list.Insert(++i, new CodeInstruction(OpCodes.Call, optic));
            }
        }

        return list;
    }
}
