using System.Collections.Generic;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.Animations;
using EFT.CameraControl;
using EFT.InventoryLogic;
using EFT.InputSystem;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace EFTNewCamera;

internal static class OpticElevation
{
    // Explicit B list, plus C/D as requested. Unconfirmed optics and A remain native.
    private static readonly HashSet<string> Supported = new()
    {
        "56ea70acd2720b844b8b4594", "57c5ac0824597754771e88a9",
        "5a37cb10c4a282329a73b4e7", "5aa66be6e5b5b0214e506e97",
        "617151c1d92c473c770214ab", "61714eec290d254f5e6b2ffc", "62850c28da09541f43158cca",
        "676175789dcee773150c6925", "5b2388675acfc4771e1be0be", "5b3b99475acfc432ff4dcbee",
        "618ba27d9008e4636a67f61d", "6567e7681265c8a131069b0f", "5dfe6104585a0c3e995c7b82",
        "544a3d0a4bdc2d1b388b4567", "68caad7f933990601c07f47c", "6a3ceaff7047a606b90f5321",
        "67999c756e3bd6cbe36d2ba2", "67999c8251c50313d9e21910", "67999c87c061e3d7f3b0ab61",
        "57ac965c24597706be5f975c", "57aca93d2459771f2c7e26db",
        "5a1eaa87fcdbcb001865f75e", "5d1b5e94d7ad1a2b865a96b0", "63fc44e2429a8a166c7f61e6",
        "6478641c19d732620e045e17", "67641b461c2eb66ade05dba6", "609bab8b455afd752b2e6138",
        "606f2696f2cb2e02a42aceb1"
    };
    private static readonly Dictionary<string, float> Offsets = new();
    private static readonly HashSet<string> ReticleMoving = new()
    {
        "5b3f7c1c5acfc40dc5296b1d", // PU 3.5x
        "5c82342f2e221644f31c060e", // PSO-1
        "5c82343a2e221644f31c0611", // PSO-1M2
        "576fd4ec2459777f0b518431"  // PSO-1M2-1
    };
    private static ConfigEntry<bool> _enableA = null!;
    private static Matrix4x4 _reticleProjection, _reticleNonJittered;
    private static ConfigEntry<bool> _enabled = null!;
    private static ConfigEntry<float> _step = null!;
    private static ConfigEntry<float> _limit = null!;
    private static ConfigEntry<bool> _show = null!;
    private static ConfigEntry<float> _repeat = null!;
    private static ConfigEntry<float> _smooth = null!;
    private static readonly Dictionary<string, float> Displayed = new();
    private static int _inputFrame = -1, _smoothFrame = -1;
    private static string _heldKey = "";
    private static int _heldSign;
    private static float _repeatAt;
    private static Camera? _camera;
    private static Matrix4x4 _projection, _nonJittered;
    private static readonly AccessTools.FieldRef<OpticRetrice, CommandBuffer> ReticleBuffer = AccessTools.FieldRefAccess<OpticRetrice, CommandBuffer>("_commandBuffer");
    private static readonly AccessTools.FieldRef<OpticRetrice, Material> ReticleMaterial = AccessTools.FieldRefAccess<OpticRetrice, Material>("_material");
    private static readonly AccessTools.FieldRef<OpticRetrice, float> ReticleScale = AccessTools.FieldRefAccess<OpticRetrice, float>("_scale");
    private static readonly AccessTools.FieldRef<FirearmHandsInputTranslator, IFirearmHandsController> Controller = AccessTools.FieldRefAccess<FirearmHandsInputTranslator, IFirearmHandsController>("_controller");

    internal static void Initialize(ConfigFile config)
    {
        LegacyReticle.Install();
        _enableA = config.Bind("光学密位", "启用A类分划调节", true,
            "PU及PSO-1系列：PageUp分划垂直向上、PageDown分划向下。背景及实际弹道不变。使用相同的步长、连调速度和平滑设置。");
        _enabled = config.Bind("光学密位", "启用B类画面调节", true,
            "仅骨架镜头开启时生效。B类以及合并处理的C/D类接管原版归零上下命令（默认PageUp/PageDown）。分划不动，PageUp背景向上、PageDown背景向下；原版米数档位不再随之切换。A类和未确认型号不接管。");
        _step = config.Bind("光学密位", "每格角度（mrad）", 0.05f,
            new ConfigDescription("暂用统一步长，可修改；不是各型号已核实的真实旋钮刻度。", new AcceptableValueRange<float>(0.01f, 1f)));
        _limit = config.Bind("光学密位", "上下限（mrad）", 30f,
            new ConfigDescription("相对当前原版归零的上下可调范围。每个瞄具实例独立记录，本次游戏会话有效。", new AcceptableValueRange<float>(1f, 100f)));
        _show = config.Bind("光学密位", "显示当前调节量", true, "瞄准支持的瞄具时，显示镜内背景的上下调节量。正值向上，负值向下，单位mrad。");
        _repeat = config.Bind("光学密位", "按住每秒步数", 60f,
            new ConfigDescription("按住PageUp/PageDown，首次单步后等待0.15秒，再按此速度连续调整。", new AcceptableValueRange<float>(1f, 200f)));
        _smooth = config.Bind("光学密位", "画面平滑时间（秒）", 0.04f,
            new ConfigDescription("镜内画面追随调节量的平滑时间，0立即变化。", new AcceptableValueRange<float>(0f, 0.2f)));
        Harmony harmony = new Harmony(NewCameraPlugin.PluginGuid + ".opticElevation");
        harmony.Patch(AccessTools.Method(typeof(FirearmHandsInputTranslator), "TranslateCommand"),
            prefix: new HarmonyMethod(typeof(OpticElevation), nameof(InputPrefix)));
        harmony.Patch(AccessTools.Method(typeof(OpticRetrice), "OnPreCull"),
            postfix: new HarmonyMethod(typeof(OpticElevation), nameof(ReticlePostfix)));
        harmony.Patch(AccessTools.Method(typeof(ProceduralWeaponAnimation), "OpticCalibrationSwitchUp"),
            prefix: new HarmonyMethod(typeof(OpticElevation), nameof(Up)));
        harmony.Patch(AccessTools.Method(typeof(ProceduralWeaponAnimation), "OpticCalibrationSwitchDown"),
            prefix: new HarmonyMethod(typeof(OpticElevation), nameof(Down)));
        harmony.Patch(AccessTools.Method(typeof(ProceduralWeaponAnimation), "OpticCalibrationSwitch"),
            prefix: new HarmonyMethod(typeof(OpticElevation), nameof(Switch)));
    }

    private static bool Up(ProceduralWeaponAnimation __instance) => Adjust(__instance, 1f);
    private static bool Down(ProceduralWeaponAnimation __instance) => Adjust(__instance, -1f);
    private static bool Switch(ProceduralWeaponAnimation __instance, bool __0) => Adjust(__instance, __0 ? 1f : -1f);

    // Intercept before the controller receives a ScopeState[] with a changed native zero index.
    private static bool InputPrefix(FirearmHandsInputTranslator __instance, ECommand __0, ref InputNode.ETranslateResult __result)
    {
        if (__0 != ECommand.OpticCalibrationSwitchUp && __0 != ECommand.OpticCalibrationSwitchDown) return true;
        OpticDiagnostics.Input("TranslateCommand", __0.ToString());
        Player? player = Singleton<GameWorld>.Instance?.MainPlayer;
        if (player == null || !ReferenceEquals(Controller(__instance), player.HandsController)
            || !TryTarget(out SightComponent? mod))
        {
            OpticDiagnostics.Input("native.passthrough", "控制器或目标识别未通过");
            return true;
        }
        if (UnityEngine.Input.GetKey(KeyCode.PageUp) || UnityEngine.Input.GetKey(KeyCode.PageDown))
            UpdateHeld();
        else
            Change(mod!, __0 == ECommand.OpticCalibrationSwitchUp ? 1f : -1f);
        __result = InputNode.ETranslateResult.Block;
        return false;
    }

    private static void Change(SightComponent mod, float sign)
    {
        string key = Key(mod);
        Offsets.TryGetValue(key, out float offset);
        Offsets[key] = Mathf.Clamp(offset + sign * _step.Value, -_limit.Value, _limit.Value);
        BallisticProbe.Adjust(mod.Item.TemplateId, offset, Offsets[key]);
        OpticDiagnostics.Input("custom.changed", $"tpl={mod.Item.TemplateId} item={mod.Item.Id} mrad={Offsets[key]:R}");
    }

    private static bool Adjust(ProceduralWeaponAnimation animation, float sign)
    {
        Player? player = Singleton<GameWorld>.Instance?.MainPlayer;
        if (player == null || player.ProceduralWeaponAnimation != animation || !TryTarget(out SightComponent? mod))
            return true;
        if (UnityEngine.Input.GetKey(KeyCode.PageUp) || UnityEngine.Input.GetKey(KeyCode.PageDown)) UpdateHeld();
        else Change(mod!, sign);
        return false;
    }

    private static string Key(SightComponent mod) => mod.Item.Id + ":" + mod.SelectedScope;

    internal static void UpdateHeld()
    {
        if (!NewCameraSwitch.Enabled) LegacyReticle.Release();
        if (_inputFrame == Time.frameCount) return;
        _inputFrame = Time.frameCount;
        int sign = (UnityEngine.Input.GetKey(KeyCode.PageUp) ? 1 : 0) - (UnityEngine.Input.GetKey(KeyCode.PageDown) ? 1 : 0);
        if (sign == 0 || !TryTarget(out SightComponent? mod))
        {
            _heldSign = 0;
            _heldKey = "";
            return;
        }
        string key = Key(mod!);
        float now = Time.unscaledTime;
        if (_heldSign != sign || _heldKey != key)
        {
            _heldSign = sign;
            _heldKey = key;
            Change(mod!, sign);
            _repeatAt = now + 0.15f;
        }
        else if (now >= _repeatAt)
        {
            int steps = 1 + Mathf.FloorToInt((now - _repeatAt) * _repeat.Value);
            _repeatAt += steps / _repeat.Value;
            Change(mod!, sign * steps);
        }
    }

    internal static void DrawStatus()
    {
        if (_show == null || !_show.Value || !TryTarget(out SightComponent? mod)) return;
        Offsets.TryGetValue(Key(mod!), out float offset);
        string kind = ReticleMoving.Contains(mod!.Item.TemplateId) ? "A类分划" : "B类画面";
        GUI.Box(new Rect(20f, 142f, 400f, 30f), $"{kind} {offset:+0.00;-0.00;0.00} mrad  |  每格 {_step.Value:0.00} mrad");
    }

    private static bool Eligible(ProceduralWeaponAnimation.SightNBone scope, OpticSight? current)
    {
        return scope.Mod != null && ((_enabled.Value && Supported.Contains(scope.Mod.Item.TemplateId))
            || (_enableA.Value && ReticleMoving.Contains(scope.Mod.Item.TemplateId)))
            && scope.Mod.SelectedScope == 0
            && (scope.IsOptic || OpticPicture.ResolveForElevation(scope) != null);
    }

    private static bool TryTarget(out SightComponent? mod)
    {
        mod = null;
        if (_enabled == null || (!_enabled.Value && !_enableA.Value) || !NewCameraSwitch.Enabled) return false;
        Player? player = Singleton<GameWorld>.Instance?.MainPlayer;
        if (player == null || !player.IsYourPlayer || player.PointOfView == EPointOfView.FreeCamera
            || player.HandsController == null || !player.HandsController.IsAiming) return false;
        ProceduralWeaponAnimation? animation = player.ProceduralWeaponAnimation;
        OpticSight? current = CameraManager.Instance?.OpticCameraManager?.CurrentOpticSight;
        if (animation?.ScopeAimTransforms != null && animation.ScopeAimTransforms.Count > 0 && Eligible(animation.CurrentScope, current))
        {
            mod = animation.CurrentScope.Mod;
            return true;
        }
        // The Ultima screen is rendered separately from its iron-sight aiming channel.
        if (animation?.ScopeAimTransforms != null)
            foreach (var scope in animation.ScopeAimTransforms)
                if (scope.Mod?.Item.TemplateId == "606f2696f2cb2e02a42aceb1" && Eligible(scope, current))
                {
                    mod = scope.Mod;
                    return true;
                }
        return false;
    }

    internal static void Apply(Camera camera)
    {
        if (!OpticPicture.IsCamera(camera)) return;
        OpticDiagnostics.Trace("apply.entry", camera);
        // Multiple pre-cull calls must never accumulate the rotation.
        Release();
        bool eligible = TryTarget(out SightComponent? mod);
        bool legacy = eligible && ReticleMoving.Contains(mod!.Item.TemplateId)
            && CameraManager.Instance?.OpticCameraManager?.OpticRetrice?.CanUpdate != true;
        if (!legacy) LegacyReticle.Release();
        float offset = 0f;
        if (eligible) Offsets.TryGetValue(Key(mod!), out offset);
        if (eligible)
        {
            string key = Key(mod!);
            Displayed.TryGetValue(key, out float shown);
            if (_smoothFrame != Time.frameCount)
            {
                float blend = _smooth.Value <= 0f ? 1f : 1f - Mathf.Exp(-Time.unscaledDeltaTime / _smooth.Value);
                shown = Mathf.Lerp(shown, offset, blend);
                if (Mathf.Abs(shown - offset) < 0.00001f) shown = offset;
                Displayed[key] = shown;
                _smoothFrame = Time.frameCount;
            }
            offset = shown;
        }
        if (legacy)
            LegacyReticle.Apply(CameraManager.Instance?.OpticCameraManager?.CurrentOpticSight, camera, offset, mod!.Item.TemplateId);
        if (eligible)
            EquivalentZeroRange.Sample(camera, offset, ReticleMoving.Contains(mod!.Item.TemplateId));
        if (!OpticDiagnostics.Force && (!eligible || Mathf.Abs(offset) < 0.000001f))
        {
            OpticDiagnostics.Trace("apply.skipped", camera, $"eligible={eligible} offset={offset:R}");
            return;
        }
        _camera = camera;
        _projection = camera.projectionMatrix;
        _nonJittered = camera.nonJitteredProjectionMatrix;
        _reticleProjection = _projection;
        _reticleNonJittered = _nonJittered;
        // Off-axis projection translates the scene vertically without rotating any geometry.
        Matrix4x4 shifted = _projection;
        shifted.m12 -= OpticDiagnostics.Force ? 0.4f : Mathf.Tan(offset / 1000f) * shifted.m11;
        Matrix4x4 steady = _nonJittered;
        steady.m12 -= OpticDiagnostics.Force ? 0.4f : Mathf.Tan(offset / 1000f) * steady.m11;
        if (eligible && ReticleMoving.Contains(mod!.Item.TemplateId) && !OpticDiagnostics.Force)
        {
            // Shift only the reticle's clip-space projection. Never rotate/translate its mesh.
            _reticleProjection = shifted;
            _reticleNonJittered = steady;
        }
        else
        {
            camera.projectionMatrix = shifted;
            camera.nonJitteredProjectionMatrix = steady;
        }
        OpticDiagnostics.Trace("apply.after", camera, $"baseM12={_projection.m12:R} wantedM12={shifted.m12:R} eligible={eligible} offset={offset:R}");
    }

    private static void ReticlePostfix(OpticRetrice __instance)
    {
        Camera camera = __instance.GetComponent<Camera>();
        if (!OpticPicture.IsCamera(camera)) return;
        OpticDiagnostics.Trace("reticle.postfix.entry", camera);
        // Ensure the skeleton pose and projection are ready regardless of callback order.
        NewCameraSwitch.OnCameraPreCull(camera);
        if (_camera != camera || !__instance.CanUpdate || __instance.Renderer == null) return;
        CommandBuffer command = ReticleBuffer(__instance);
        Material material = ReticleMaterial(__instance);
        if (command == null || material == null) return;
        command.Clear();
        command.SetGlobalFloat(Shader.PropertyToID("_Scale"), ReticleScale(__instance));
        command.SetGlobalMatrix(Shader.PropertyToID("_NonJitteredProj"), GL.GetGPUProjectionMatrix(_reticleNonJittered, true));
        command.SetViewProjectionMatrices(camera.worldToCameraMatrix, _reticleProjection);
        command.DrawRenderer(__instance.Renderer, material, 0, 0);
        command.SetViewProjectionMatrices(camera.worldToCameraMatrix, camera.projectionMatrix);
        command.SetGlobalMatrix(Shader.PropertyToID("_NonJitteredProj"), GL.GetGPUProjectionMatrix(camera.nonJitteredProjectionMatrix, true));
        OpticDiagnostics.Trace("reticle.buffer.rebuilt", camera);
    }

    internal static void Restore(Camera camera)
    {
        if (OpticPicture.IsCamera(camera)) OpticDiagnostics.Trace("restore.before", camera);
        if (_camera == camera) Release();
    }

    internal static void Release()
    {
        if (_camera != null)
        {
            _camera.projectionMatrix = _projection;
            _camera.nonJitteredProjectionMatrix = _nonJittered;
        }
        _camera = null;
    }

    internal static void Shutdown()
    {
        Release();
        LegacyReticle.Release();
    }
}
