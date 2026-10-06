using System;
using System.Collections.Generic;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.Animations;
using HarmonyLib;
using UnityEngine;

namespace EFTNewCamera;

internal static class AimQuiet
{
    private const string BodyLayerName = "Additive_FullBody";
    private const float ArmResidual = 0.1f;
    private const float BlendSeconds = 1f;

    private static readonly Dictionary<int, float> OriginalLayerWeights = new();
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

    internal static void Apply(Player player)
    {
        bool aiming = player.HandsController != null && player.HandsController.IsAiming;
        Animator? animator = ResolveAnimator(player.BodyAnimatorCommon);
        if (!aiming || animator == null)
        {
            Restore();
            return;
        }

        int layer = ResolveLayer(animator);
        if (layer < 0)
            return;

        int key = animator.GetInstanceID() * 397 ^ layer;
        float current = animator.GetLayerWeight(layer);
        if (!OriginalLayerWeights.TryGetValue(key, out float original))
        {
            original = current;
            OriginalLayerWeights[key] = original;
        }

        float target = original * ArmResidual;
        float follow = 1f - Mathf.Exp(-Time.unscaledDeltaTime / BlendSeconds);
        animator.SetLayerWeight(layer, Mathf.Lerp(current, target, follow));
    }

    internal static void Restore()
    {
        if (OriginalLayerWeights.Count == 0)
            return;

        Player? player = Singleton<GameWorld>.Instance?.MainPlayer;
        Animator? animator = player == null ? null : ResolveAnimator(player.BodyAnimatorCommon);
        List<int>? done = null;
        foreach (KeyValuePair<int, float> saved in OriginalLayerWeights)
        {
            if (animator == null || !TryLayer(animator, saved.Key, out int layer))
            {
                done ??= new List<int>();
                done.Add(saved.Key);
                continue;
            }

            float current = animator.GetLayerWeight(layer);
            float follow = 1f - Mathf.Exp(-Time.unscaledDeltaTime / BlendSeconds);
            float next = Mathf.Lerp(current, saved.Value, follow);
            animator.SetLayerWeight(layer, next);
            if (Mathf.Abs(next - saved.Value) <= 0.001f)
            {
                animator.SetLayerWeight(layer, saved.Value);
                done ??= new List<int>();
                done.Add(saved.Key);
            }
        }

        if (done == null)
            return;

        foreach (int key in done)
            OriginalLayerWeights.Remove(key);
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

    private static int ResolveLayer(Animator animator)
    {
        int named = animator.GetLayerIndex(BodyLayerName);
        if (named >= 0)
            return named;

        return animator.layerCount > 8 ? 8 : -1;
    }

    private static bool TryLayer(Animator animator, int key, out int layer)
    {
        layer = -1;
        int animatorId = animator.GetInstanceID();
        for (int candidate = 0; candidate < animator.layerCount; candidate++)
        {
            if ((animatorId * 397 ^ candidate) != key)
                continue;

            layer = candidate;
            return true;
        }

        return false;
    }

    private static Animator? ResolveAnimator(object? animatorLike)
    {
        if (animatorLike == null)
            return null;

        if (animatorLike is Animator animator)
            return animator;

        Type type = animatorLike.GetType();
        foreach (PropertyInfo property in AccessTools.GetDeclaredProperties(type))
        {
            if (!typeof(Animator).IsAssignableFrom(property.PropertyType))
                continue;

            try
            {
                if (property.GetValue(animatorLike, null) is Animator fromProperty)
                    return fromProperty;
            }
            catch
            {
            }
        }

        foreach (FieldInfo field in AccessTools.GetDeclaredFields(type))
        {
            if (!typeof(Animator).IsAssignableFrom(field.FieldType))
                continue;

            try
            {
                if (field.GetValue(animatorLike) is Animator fromField)
                    return fromField;
            }
            catch
            {
            }
        }

        if (animatorLike is Component component)
            return component.GetComponent<Animator>();

        return null;
    }
}
