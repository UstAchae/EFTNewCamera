using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using EFT;
using HarmonyLib;

namespace EFTNewCamera;

internal static class FirstPersonAudio
{
    private static readonly MethodInfo PlayerPointOfViewGetter =
        AccessTools.PropertyGetter(typeof(Player), nameof(Player.PointOfView));

    private static readonly MethodInfo PlayerFirstPersonGetter =
        AccessTools.PropertyGetter(typeof(Player), nameof(Player.FirstPersonPointOfView));

    private static readonly MethodInfo EffectivePointOfViewMethod =
        AccessTools.Method(typeof(FirstPersonAudio), nameof(GetEffectivePointOfView));

    private static readonly MethodInfo EffectiveFirstPersonMethod =
        AccessTools.Method(typeof(FirstPersonAudio), nameof(IsEffectiveFirstPerson));

    private static bool _installed;

    internal static void Install()
    {
        if (_installed)
            return;

        Harmony harmony = new Harmony(NewCameraPlugin.PluginGuid + ".audio");
        MethodInfo? bridgeGetter = AccessTools.PropertyGetter(
            typeof(BaseSoundPlayer.PlayerBridge),
            nameof(BaseSoundPlayer.PlayerBridge.PointOfView));
        MethodInfo? bridgePostfix = AccessTools.Method(typeof(FirstPersonAudio), nameof(PlayerBridgePointOfViewPostfix));
        if (bridgeGetter != null && bridgePostfix != null)
            harmony.Patch(bridgeGetter, postfix: new HarmonyMethod(bridgePostfix));

        // 35392 audio methods, verified by their POV reads and decompiled behavior.
        string[] playerAudioMethods =
        {
            "method_31",
            nameof(Player.PlaySpeechFromTime),
            nameof(Player.UpdateMuffledState),
            "method_34",
            "method_40",
            nameof(Player.CreateNestedSource),
            nameof(Player.CreateSpeechSource),
            nameof(Player.CreateGearSource),
            "method_43",
            "method_44",
            "method_46",
            nameof(Player.PlayInteractionSound),
            nameof(Player.StopInteractionSound),
            "method_53",
            nameof(Player.DefaultPlay),
            nameof(Player.PlayStepSound),
            "method_59",
            nameof(Player.PlaySoundBank),
            "method_78",
            nameof(Player.SpawnInHands)
        };

        MethodInfo? transpiler = AccessTools.Method(typeof(FirstPersonAudio), nameof(ReplacePlayerPointOfViewReads));
        if (transpiler == null)
            return;

        foreach (string methodName in playerAudioMethods)
        {
            MethodInfo? target = AccessTools.Method(typeof(Player), methodName);
            if (target == null)
                continue;

            harmony.Patch(target, transpiler: new HarmonyMethod(transpiler));
        }

        PatchIterator(harmony, "method_69", transpiler);
        PatchIterator(harmony, "method_71", transpiler);
        _installed = true;
    }

    public static EPointOfView GetEffectivePointOfView(Player player)
    {
        if (ShouldUseFirstPersonAudio(player))
            return EPointOfView.FirstPerson;

        return player.PointOfView;
    }

    public static bool IsEffectiveFirstPerson(Player player)
    {
        if (ShouldUseFirstPersonAudio(player))
            return true;

        return player.FirstPersonPointOfView;
    }

    private static void PlayerBridgePointOfViewPostfix(BaseSoundPlayer.PlayerBridge __instance, ref EPointOfView __result)
    {
        Player? player = __instance?.iPlayer as Player;
        if (ShouldUseFirstPersonAudio(player))
            __result = EPointOfView.FirstPerson;
    }

    private static IEnumerable<CodeInstruction> ReplacePlayerPointOfViewReads(IEnumerable<CodeInstruction> instructions)
    {
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.Calls(PlayerPointOfViewGetter))
            {
                yield return new CodeInstruction(OpCodes.Call, EffectivePointOfViewMethod)
                    .MoveLabelsFrom(instruction)
                    .MoveBlocksFrom(instruction);
                continue;
            }

            if (instruction.Calls(PlayerFirstPersonGetter))
            {
                yield return new CodeInstruction(OpCodes.Call, EffectiveFirstPersonMethod)
                    .MoveLabelsFrom(instruction)
                    .MoveBlocksFrom(instruction);
                continue;
            }

            yield return instruction;
        }
    }

    private static bool ShouldUseFirstPersonAudio(Player? player)
    {
        return player != null && player.IsYourPlayer && NewCameraSwitch.Enabled;
    }

    private static void PatchIterator(Harmony harmony, string methodName, MethodInfo transpiler)
    {
        MethodInfo? iteratorFactory = AccessTools.Method(typeof(Player), methodName);
        MethodInfo? moveNext = iteratorFactory == null ? null : AccessTools.EnumeratorMoveNext(iteratorFactory);
        if (moveNext == null)
            return;

        harmony.Patch(moveNext, transpiler: new HarmonyMethod(transpiler));
    }
}
