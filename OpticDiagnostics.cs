using System.Collections.Generic;
using BepInEx.Configuration;
using BepInEx.Logging;
using Comfort.Common;
using EFT;
using EFT.CameraControl;
using UnityEngine;
using HarmonyLib;
using UnityEngine.Rendering.PostProcessing;

namespace EFTNewCamera;

// Observe the actual camera callbacks. This deliberately does not try to repair overwritten matrices.
internal sealed class OpticDiagnostics : MonoBehaviour
{
    private static ConfigEntry<bool> _force = null!;
    private static ManualLogSource _log = null!;
    private static readonly Dictionary<string, float> Next = new();
    private static string _last = "等待镜内相机渲染";
    private static string _input = "尚未收到归零命令";
    private float _inventoryNext;

    internal void Initialize(ConfigFile config, ManualLogSource log)
    {
        _log = log;
        _force = config.Bind("光学密位诊断", "强制镜内画面上移测试", false,
            "诊断用：骨架镜头开启并瞄准时，绕过型号白名单和密位数值，对管理器的镜内相机施加画面高度20%的上移。分划仍走原投影。开启后无需按PgUp；测试完成请关闭。");
        _force.SettingChanged += (_, _) => _log.LogInfo("[OpticDiag] force20percent=" + _force.Value);
        Camera.onPreCull += PreCull;
        Camera.onPreRender += PreRender;
        Camera.onPostRender += PostRender;
        var method = AccessTools.Method(typeof(PostProcessLayer), "OnPreCull");
        if (method != null)
            new Harmony(NewCameraPlugin.PluginGuid + ".opticDiagnostics").Patch(method,
                prefix: new HarmonyMethod(typeof(OpticDiagnostics), nameof(PostProcessBefore)),
                postfix: new HarmonyMethod(typeof(OpticDiagnostics), nameof(PostProcessAfter)));
        _log.LogInfo("[OpticDiag] diagnostics ready; force test=" + _force.Value);
    }

    private static bool Aiming => Singleton<GameWorld>.Instance?.MainPlayer?.HandsController?.IsAiming == true;
    internal static bool Force => OpticTest.Enabled && _force != null && _force.Value && NewCameraSwitch.Enabled && Aiming;

    internal static void Input(string stage, string detail)
    {
        if (!OpticTest.Enabled) return;
        _input = stage + " " + detail;
        _log.LogInfo("[OpticDiag] input " + _input);
    }

    private static string Matrix(Matrix4x4 m) =>
        $"[{m.m00:R},{m.m01:R},{m.m02:R},{m.m03:R};{m.m10:R},{m.m11:R},{m.m12:R},{m.m13:R};{m.m20:R},{m.m21:R},{m.m22:R},{m.m23:R};{m.m30:R},{m.m31:R},{m.m32:R},{m.m33:R}]";

    internal static void Trace(string stage, Camera camera, string detail = "")
    {
        if (!OpticTest.Enabled || !Aiming || camera == null) return;
        string key = stage + ":" + camera.GetInstanceID();
        if (Next.TryGetValue(key, out float next) && Time.unscaledTime < next) return;
        Next[key] = Time.unscaledTime + 1f;
        Camera? managed = CameraManager.Instance?.OpticCameraManager?.Camera;
        var animation = Singleton<GameWorld>.Instance?.MainPlayer?.ProceduralWeaponAnimation;
        var scope = animation?.ScopeAimTransforms != null && animation.ScopeAimTransforms.Count > 0 ? animation.CurrentScope : null;
        string target = camera.targetTexture != null ? $"{camera.targetTexture.name}#{camera.targetTexture.GetInstanceID()}:{camera.targetTexture.width}x{camera.targetTexture.height}" : "screen";
        string text = $"stage={stage} frame={Time.frameCount} camera={camera.name}#{camera.GetInstanceID()} managed={(managed != null ? managed.GetInstanceID() : 0)} enabled={camera.enabled} target={target} fov={camera.fieldOfView:R} scope={scope?.Mod?.Item.TemplateId} selected={scope?.Mod?.SelectedScope} isOptic={scope?.IsOptic} force={Force} {detail} projection={Matrix(camera.projectionMatrix)} nonJittered={Matrix(camera.nonJitteredProjectionMatrix)} view={Matrix(camera.worldToCameraMatrix)}";
        _log.LogInfo("[OpticDiag] " + text);
        if (camera == managed) _last = $"{stage}: {camera.name} #{camera.GetInstanceID()}\nm12={camera.projectionMatrix.m12:F5}  RT={target}";
    }

    private static void PreCull(Camera camera) => Trace("event.preCull", camera);
    private static void PreRender(Camera camera) => Trace("event.preRender", camera);
    private static void PostRender(Camera camera) => Trace("event.postRender", camera);
    private static void PostProcessBefore(PostProcessLayer __instance) => Trace("postprocess.preCull.before", __instance.GetComponent<Camera>());
    private static void PostProcessAfter(PostProcessLayer __instance) => Trace("postprocess.preCull.after", __instance.GetComponent<Camera>());

    private void Update()
    {
        if (!OpticTest.Enabled || !Aiming) return;
        if (UnityEngine.Input.GetKeyDown(KeyCode.PageUp)) Input("raw.PgUp", "收到物理按键");
        if (UnityEngine.Input.GetKeyDown(KeyCode.PageDown)) Input("raw.PgDown", "收到物理按键");
        if (Time.unscaledTime < _inventoryNext) return;
        _inventoryNext = Time.unscaledTime + 5f;
        foreach (Camera camera in Camera.allCameras) Trace("inventory", camera);
    }

    private void OnGUI()
    {
        if (!OpticTest.Enabled || !Aiming) return;
        GUI.Box(new Rect(20f, 180f, 760f, 90f), $"镜内诊断 | 强制上移20%：{Force}\n{_last}\n输入：{_input}");
    }

    private void OnDestroy()
    {
        Camera.onPreCull -= PreCull;
        Camera.onPreRender -= PreRender;
        Camera.onPostRender -= PostRender;
        Next.Clear();
    }
}
