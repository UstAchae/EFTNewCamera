using System.Collections.Generic;
using System.Linq;
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

    private static readonly MethodInfo ProceduralWeaponAnimationGetter =
        AccessTools.PropertyGetter(typeof(Player), nameof(Player.ProceduralWeaponAnimation));

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
            nameof(Player.SpawnInHands),
            // Named in the game since its obfuscated names (method_31..method_80) went: each read of the point of view in
            // these only sets up a sound (volume, rolloff, mixer, EQ, occlusion, spatialisation, prewarming).
            nameof(Player.GetSearchSource),
            nameof(Player.StateChangedHandler),
            nameof(Player.CalculateMovementVolumeDefaultMultiplier),
            nameof(Player.CalculateMovementRolloff),
            nameof(Player.ProcessPlayPrewarmSound),
            nameof(Player.PlayPrewarmSound),
            nameof(Player.SetupSoundsFromFaceShieldComponent),
            nameof(Player.OnHealthEffectAdded),
            nameof(Player.InitSeasonStepLayerController)
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

        MethodInfo? gearSound = AccessTools.Method(typeof(Player), nameof(Player.PlayGearSound), new[] { typeof(SoundBank), typeof(float) });
        if (gearSound != null)
            harmony.Patch(gearSound, transpiler: new HarmonyMethod(transpiler));
        else
            NewCameraPlugin.Log?.LogWarning("First-person audio: Player.PlayGearSound(SoundBank, float) not found; its sounds stay as the game has them.");

        // Coroutines: the reads are in the state machine's MoveNext. The sprint's last read sets the first-person weapon's
        // step frequency, not a sound, and keeps the game's point of view (SprintTranspiler).
        PatchCoroutine(harmony, nameof(Player.SupportAudioSourceCoroutine), transpiler);
        PatchCoroutine(harmony, nameof(Player.Sprint), AccessTools.Method(typeof(FirstPersonAudio), nameof(SprintTranspiler)));

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

    private static void PatchCoroutine(Harmony harmony, string methodName, MethodInfo? transpiler)
    {
        MethodInfo? factory = AccessTools.Method(typeof(Player), methodName);
        MethodInfo? moveNext = factory == null ? null : AccessTools.EnumeratorMoveNext(factory);
        if (moveNext == null || transpiler == null)
        {
            NewCameraPlugin.Log?.LogWarning($"First-person audio: Player.{methodName} coroutine not found; its sounds stay as the game has them.");
            return;
        }
        harmony.Patch(moveNext, transpiler: new HarmonyMethod(transpiler));
    }

    // Player.Sprint: every read of the point of view is for the sprint's sounds, except the one guarding the weapon's
    // step frequency (ProceduralWeaponAnimation.Walk), which is left as the game reads it.
    private static IEnumerable<CodeInstruction> SprintTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> list = instructions.ToList();
        for (int i = 0; i < list.Count; i++)
        {
            CodeInstruction instruction = list[i];
            bool pointOfView = instruction.Calls(PlayerPointOfViewGetter), firstPerson = instruction.Calls(PlayerFirstPersonGetter);
            if (!pointOfView && !firstPerson)
                continue;
            bool forWeapon = false;
            for (int j = i + 1; j < list.Count && j <= i + 8; j++)
            {
                if (list[j].Calls(ProceduralWeaponAnimationGetter))
                {
                    forWeapon = true;
                    break;
                }
            }
            if (forWeapon)
                continue;
            list[i] = new CodeInstruction(OpCodes.Call, pointOfView ? EffectivePointOfViewMethod : EffectiveFirstPersonMethod)
                .MoveLabelsFrom(instruction)
                .MoveBlocksFrom(instruction);
        }
        return list;
    }

    private static bool ShouldUseFirstPersonAudio(Player? player)
    {
        return player != null && player.IsYourPlayer && NewCameraSwitch.Enabled;
    }
}
