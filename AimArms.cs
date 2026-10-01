using System.Collections.Generic;
using System.Reflection;
using EFT;
using EFT.Animations;
using HarmonyLib;
using UnityEngine;

namespace EFTNewCamera;

internal static class AimArms
{
    private static bool _suppress;
    private static bool _patched;

    internal static void Install()
    {
        if (_patched)
            return;

        MethodInfo? target = AccessTools.Method(typeof(PlayerAnimator), nameof(PlayerAnimator.SetAiming));
        MethodInfo? prefix = typeof(AimArms).GetMethod(nameof(SetAimingPrefix), BindingFlags.NonPublic | BindingFlags.Static);
        if (target == null || prefix == null)
            return;

        Harmony harmony = new Harmony(NewCameraPlugin.PluginGuid + ".aimarms");
        harmony.Patch(target, prefix: new HarmonyMethod(prefix));
        _patched = true;
    }

    internal static void Apply(Player player)
    {
        _suppress = true;
        player.MovementContext?.PlayerAnimator?.SetAiming(false);
    }

    internal static void AlignToCamera(Player player, Vector3 cameraPosition)
    {
        if (player.HandsController == null || !player.HandsController.IsAiming)
            return;

        ProceduralWeaponAnimation animation = player.ProceduralWeaponAnimation;
        if (animation == null || animation.ScopeAimTransforms == null || animation.ScopeAimTransforms.Count == 0)
            return;

        Transform? bone = animation.CurrentScope?.Bone;
        PlayerBones bones = player.PlayerBones;
        if (bone == null || bones == null)
            return;

        Vector3 displacement = cameraPosition - bone.position;
        List<Transform> roots = new List<Transform>();
        Consider(roots, animation.HandsContainer?.TrackingTransform);
        Consider(roots, bones.Weapon_Root_Anim);
        Consider(roots, bones.LeftPalm);
        Consider(roots, bones.RightPalm);
        Consider(roots, bones.Shoulders);
        Consider(roots, bones.Shoulders_Anim);
        for (int i = roots.Count - 1; i >= 0; i--)
        {
            for (int j = 0; j < roots.Count; j++)
            {
                if (i != j && roots[i].IsChildOf(roots[j]))
                {
                    roots.RemoveAt(i);
                    break;
                }
            }
        }

        foreach (Transform root in roots)
            root.position += displacement;
    }

    private static void Consider(List<Transform> roots, Transform? transform)
    {
        if (transform != null && !roots.Contains(transform))
            roots.Add(transform);
    }

    private static void Consider(List<Transform> roots, Transform[]? transforms)
    {
        if (transforms == null)
            return;
        foreach (Transform transform in transforms)
            Consider(roots, transform);
    }

    internal static void Release(Player? player)
    {
        if (!_suppress)
            return;

        _suppress = false;
        if (player?.HandsController != null && player.HandsController.IsAiming)
            player.MovementContext?.PlayerAnimator?.SetAiming(true);
    }

    private static void SetAimingPrefix(PlayerAnimator __instance, ref bool isAiming)
    {
        if (!_suppress || !isAiming)
            return;

        Player? player = Comfort.Common.Singleton<GameWorld>.Instance?.MainPlayer;
        if (player != null && player.IsYourPlayer && ReferenceEquals(player.MovementContext?.PlayerAnimator, __instance))
            isAiming = false;
    }
}
