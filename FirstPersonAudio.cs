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

        string[] playerAudioMethods =
        {
            nameof(Player.PlaySpeechFromTime),
            nameof(Player.UpdateMuffledState),
            nameof(Player.CreateNestedSource),
            nameof(Player.CreateSpeechSource),
            nameof(Player.CreateGearSource),
            nameof(Player.PlayInteractionSound),
            nameof(Player.StopInteractionSound),
            nameof(Player.DefaultPlay),
            nameof(Player.PlayStepSound),
            nameof(Player.PlaySoundBank),
            nameof(Player.SpawnInHands)
        };

        MethodInfo? transpiler = AccessTools.Method(typeof(FirstPersonAudio), nameof(ReplacePlayerPointOfViewReads));
        if (transpiler == null)
            return;

        foreach (string methodName in playerAudioMethods)
        {
            MethodInfo? target = AccessTools.Method(typeof(Player), methodName);
            if (target == null)
            {
                NewCameraPlugin.Log?.LogWarning($"First-person audio: Player.{methodName} not found; its sounds stay as the game has them.");
                continue;
            }

            harmony.Patch(target, transpiler: new HarmonyMethod(transpiler));
        }

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
        Player? player = __instance?._player;
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
}
