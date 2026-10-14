using System.Collections.Generic;
using System.Reflection;
using Comfort.Common;
using EFT;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;

namespace EFTNewCamera;

// TAA and DLSS both blend previous frames into every pixel. The collimator reticle is transparent and writes
// no motion vectors, so a reticle that moves a pixel or two inside its housing leaves a copy behind.
// While aiming, the current sight's reticle is kept out of the main camera's normal pass and drawn after
// whichever of the two did the blending, before bloom and tone mapping.
internal static class ReticleAfterTaa
{
    internal static bool Enabled = true;

    // Not in the main camera's culling mask, and nothing in the game puts renderers on it.
    private const int HiddenLayer = 19;

    private static readonly FieldInfo? ResolveDisabledField = AccessTools.Field(typeof(PostProcessLayer), "_isTAAResolveDisabled");
    private static readonly List<(GameObject Target, int Layer)> Hidden = new List<(GameObject, int)>();
    private static bool _installed;
    internal static void Install()
    {
        if (_installed)
            return;

        Harmony harmony = new Harmony(NewCameraPlugin.PluginGuid + ".reticle");
        MethodInfo? preCull = AccessTools.Method(typeof(PostProcessLayer), "OnPreCull");
        MethodInfo? before = typeof(ReticleAfterTaa).GetMethod(nameof(BeforePostProcess), BindingFlags.NonPublic | BindingFlags.Static);
        if (preCull != null && before != null)
            harmony.Patch(preCull, prefix: new HarmonyMethod(before));

        MethodInfo? render = AccessTools.Method(typeof(TemporalAntialiasing), "Render");
        MethodInfo? after = typeof(ReticleAfterTaa).GetMethod(nameof(RenderPostfix), BindingFlags.NonPublic | BindingFlags.Static);
        if (render != null && after != null)
            harmony.Patch(render, postfix: new HarmonyMethod(after));

        MethodInfo? upscale = AccessTools.Method(typeof(SSAAPropagator), "FillCommandBuffer");
        MethodInfo? afterUpscale = typeof(ReticleAfterTaa).GetMethod(nameof(UpscalePostfix), BindingFlags.NonPublic | BindingFlags.Static);
        if (upscale != null && afterUpscale != null)
            harmony.Patch(upscale, postfix: new HarmonyMethod(afterUpscale));

        _installed = true;
    }

    internal static void OnCameraPostRender(Camera camera)
    {
        // The main camera has already culled and the reticle draw is already in its command buffer.
        if (camera != null && camera.CompareTag("MainCamera"))
            Restore();
    }

    // Runs before this camera builds its post-processing, which is still before it culls.
    private static void BeforePostProcess(PostProcessLayer __instance)
    {
        Camera camera = __instance.GetComponent<Camera>();
        if (camera == null || !camera.CompareTag("MainCamera"))
            return;

        if (!Active(camera))
        {
            Restore();
            return;
        }

        // Putting the gun in its final pose first means the reticle mesh recorded below matches the housing.
        NewCameraSwitch.OnCameraPreCull(camera);
        HideCurrentReticle();
    }

    private static void RenderPostfix(PostProcessRenderContext context)
    {
        Camera camera = context.camera;
        // DLSS turns this pass off and blends the frame itself. Drawing here would miss that blend.
        if (camera == null || !camera.CompareTag("MainCamera") || Hidden.Count == 0 || !Active(camera) || ResolveDisabled(camera))
            return;

        DrawReticle(context.command, camera, context.destination);
    }

    private static void UpscalePostfix(SSAAPropagator __instance, CommandBuffer externalCommandBuffer)
    {
        if (externalCommandBuffer == null || Hidden.Count == 0)
            return;

        Camera camera = __instance.GetComponent<Camera>();
        if (camera == null || !camera.CompareTag("MainCamera") || !Active(camera) || !ResolveDisabled(camera))
            return;

        if (!__instance.GetSourceDestination(out RenderTexture source, out _) || source == null)
            return;

        DrawReticle(externalCommandBuffer, camera, source);
    }

    private static void DrawReticle(CommandBuffer command, Camera camera, RenderTargetIdentifier color)
    {
        // Same matrices the game uses when it redraws a collimator after an upscale, so the dot stays on the
        // housing instead of a pixel off to one side.
        command.EnableShaderKeyword("TAA");
        command.EnableShaderKeyword("NON_JITTERED");
        command.SetGlobalMatrix(Shader.PropertyToID("NonJitteredProj"), GL.GetGPUProjectionMatrix(camera.nonJitteredProjectionMatrix, true));
        command.SetViewProjectionMatrices(camera.worldToCameraMatrix, camera.nonJitteredProjectionMatrix);
        command.SetRenderTarget(color);
        foreach ((GameObject target, int _) in Hidden)
        {
            if (target == null)
                continue;
            MeshRenderer renderer = target.GetComponent<MeshRenderer>();
            MeshFilter filter = target.GetComponent<MeshFilter>();
            if (renderer == null || renderer.sharedMaterial == null || filter == null || filter.sharedMesh == null)
                continue;
            command.DrawMesh(filter.sharedMesh, target.transform.localToWorldMatrix, renderer.sharedMaterial, 0, 0);
        }
        command.SetViewProjectionMatrices(camera.worldToCameraMatrix, camera.projectionMatrix);
        command.DisableShaderKeyword("NON_JITTERED");
        command.DisableShaderKeyword("TAA");
    }

    private static void HideCurrentReticle()
    {
        Player? player = Singleton<GameWorld>.Instance?.MainPlayer;
        ScopePrefabCache? cache = player?.ProceduralWeaponAnimation?.CurrentScope?.ScopePrefabCache;
        if (player == null || cache == null || !cache.HasCollimators)
            return;

        Restore();
        for (int i = 0; i < cache.ModesCount; i++)
        {
            CollimatorSight? sight = cache.GetScopeModeInfo(i).CollimatorSight;
            MeshRenderer? renderer = sight != null ? sight.CollimatorMeshRenderer : null;
            if (sight == null || !sight.isActiveAndEnabled || renderer == null || !renderer.enabled || renderer.sharedMaterial == null)
                continue;

            GameObject target = renderer.gameObject;
            Hidden.Add((target, target.layer));
            target.layer = HiddenLayer;
        }
    }

    private static bool Active(Camera camera)
    {
        if (!Enabled)
            return false;

        // DLSS leaves the mode on TemporalAntialiasing and then disables Unity's own blend, so the mode check
        // covers both. The disabled blend is handled by drawing after the upscale instead.
        PostProcessLayer? layer = camera.GetComponent<PostProcessLayer>();
        if (layer == null || layer.antialiasingMode != PostProcessLayer.Antialiasing.TemporalAntialiasing)
            return false;

        Player? player = Singleton<GameWorld>.Instance?.MainPlayer;
        return player != null && player.IsYourPlayer && player.HandsController != null && player.HandsController.IsAiming;
    }

    private static bool ResolveDisabled(Camera camera)
    {
        PostProcessLayer? layer = camera.GetComponent<PostProcessLayer>();
        return layer != null && ResolveDisabledField?.GetValue(layer) is true;
    }

    private static void Restore()
    {
        foreach ((GameObject target, int layer) in Hidden)
        {
            if (target != null)
                target.layer = layer;
        }
        Hidden.Clear();
    }
}
