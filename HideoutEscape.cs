using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using EFT;
using HarmonyLib;

namespace EFTNewCamera;

// The hideout takes Escape (leave the shooting range, close an area's screen, the menu) only in the first-person view;
// in any other it swallows the key, meant for its overview's free camera. This mod keeps the player in the
// third-person view while walking about, so there Escape counts it as first person. The free camera stays as it was.
internal static class HideoutEscape
{
    private static readonly MethodInfo PointOfViewGetter =
        AccessTools.PropertyGetter(typeof(Player), nameof(Player.PointOfView));

    private static readonly MethodInfo ViewForEscape =
        AccessTools.Method(typeof(HideoutEscape), nameof(PointOfViewForEscape));

    internal static void Install()
    {
        Harmony harmony = new Harmony(NewCameraPlugin.PluginGuid + ".hideoutescape");
        MethodInfo? target = AccessTools.Method(typeof(HideoutPlayerOwner), nameof(HideoutPlayerOwner.TranslateExitScreenInput));
        if (target == null)
        {
            NewCameraPlugin.Log?.LogWarning("Hideout Escape: HideoutPlayerOwner.TranslateExitScreenInput not found");
            return;
        }
        harmony.Patch(target, transpiler: new HarmonyMethod(AccessTools.Method(typeof(HideoutEscape), nameof(Transpiler))));
        InstallNightVision(harmony);
    }

    // The hideout puts its own night vision (a plain filter made for its overview camera) in place of the goggles worn,
    // and no thermal at all, whenever the player is not in the first-person view; this mod keeps the player in the
    // third-person view while walking about, so there the goggles worn count as seen in first person, and work as in a
    // raid. The choice is made in the hideout player's slot observers (compiler-made lambdas, found by what they take and
    // give, not by their names).
    private static void InstallNightVision(Harmony harmony)
    {
        HarmonyMethod viewAsWorn = new HarmonyMethod(AccessTools.Method(typeof(HideoutEscape), nameof(NightVisionTranspiler)));
        int patched = 0;
        foreach (System.Type nested in typeof(HideoutPlayer).GetNestedTypes(AccessTools.all))
        {
            foreach (MethodInfo method in nested.GetMethods(AccessTools.all | BindingFlags.DeclaredOnly))
            {
                ParameterInfo[] parameters = method.GetParameters();
                bool picker = parameters.Length == 1 && parameters[0].ParameterType == method.ReturnType
                    && (method.ReturnType == typeof(EFT.InventoryLogic.NightVisionComponent) || method.ReturnType == typeof(EFT.InventoryLogic.ThermalVisionComponent));
                if (!picker)
                    continue;
                harmony.Patch(method, transpiler: viewAsWorn);
                patched++;
            }
        }
        if (patched == 0)
            NewCameraPlugin.Log?.LogWarning("Hideout night vision: the hideout player's night-vision choice not found; the hideout's own filter stays");
        else
            NewCameraPlugin.Log?.LogInfo($"Hideout night vision: the goggles worn are used ({patched} choices)");
    }

    private static IEnumerable<CodeInstruction> NightVisionTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.Calls(PointOfViewGetter))
            {
                yield return new CodeInstruction(OpCodes.Call, ViewForEscape)
                    .MoveLabelsFrom(instruction)
                    .MoveBlocksFrom(instruction);
                continue;
            }
            yield return instruction;
        }
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        int replaced = 0;
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.Calls(PointOfViewGetter))
            {
                replaced++;
                yield return new CodeInstruction(OpCodes.Call, ViewForEscape)
                    .MoveLabelsFrom(instruction)
                    .MoveBlocksFrom(instruction);
                continue;
            }
            yield return instruction;
        }
        if (replaced == 0)
            NewCameraPlugin.Log?.LogWarning("Hideout Escape: no point-of-view read in TranslateExitScreenInput");
    }

    public static EPointOfView PointOfViewForEscape(Player player)
    {
        EPointOfView view = player.PointOfView;
        return view == EPointOfView.ThirdPerson && NewCameraSwitch.Enabled && player.IsYourPlayer ? EPointOfView.FirstPerson : view;
    }
}
