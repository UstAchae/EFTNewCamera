using System.Collections.Generic;
using System.Reflection;
using Comfort.Common;
using EFT;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;

namespace EFTNewCamera;

// TAA blends about 99% of the previous frames into every pixel. The collimator reticle is transparent and
// writes no motion vectors, so a reticle that moves a pixel or two inside its housing leaves a copy behind.
// While aiming, the current sight's reticle is kept out of the main camera's normal pass and drawn into the
// TAA output instead, before bloom and tone mapping.
internal static class ReticleAfterTaa
{
    internal static bool Enabled = true;

    // Not in the main camera's culling mask, and nothing in the game puts renderers on it.
    private const int HiddenLayer = 19;

    private static readonly FieldInfo? ResolveDisabledField = AccessTools.Field(typeof(PostProcessLayer), "_isTAAResolveDisabled");
    private static readonly List<(GameObject Target, int Layer)> Hidden = new List<(GameObject, int)>();
    private static bool _installed;
    private static bool _logged;

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
        if (camera == null || !camera.CompareTag("MainCamera") || Hidden.Count == 0 || !Active(camera))
            return;

        // The camera is still on the jittered matrix here. The housing is resolved by TAA, so the reticle has to
        // use the stable matrix or it sits a pixel off the housing. Put the jittered matrix back afterwards so
        // the rest of this buffer (bloom, color) keeps the matrices it expects.
        Matrix4x4 view = camera.worldToCameraMatrix;
        Matrix4x4 jittered = camera.projectionMatrix;
        context.command.SetViewProjectionMatrices(view, camera.nonJitteredProjectionMatrix);
        context.command.SetRenderTarget(context.destination, UnityEngine.Rendering.BuiltinRenderTextureType.Depth);
        foreach ((GameObject target, int _) in Hidden)
        {
            if (target == null)
                continue;
            MeshRenderer renderer = target.GetComponent<MeshRenderer>();
            MeshFilter filter = target.GetComponent<MeshFilter>();
            if (renderer == null || renderer.sharedMaterial == null || filter == null || filter.sharedMesh == null)
                continue;
            context.command.DrawMesh(filter.sharedMesh, target.transform.localToWorldMatrix, renderer.sharedMaterial, 0, 0);
        }
        context.command.SetViewProjectionMatrices(view, jittered);
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

        if (!_logged && Hidden.Count > 0)
        {
            _logged = true;
            Debug.Log("[EFTNewCamera] collimator reticle is drawn after TAA while aiming");
        }
    }

    private static bool Active(Camera camera)
    {
        if (!Enabled)
            return false;

        PostProcessLayer? layer = camera.GetComponent<PostProcessLayer>();
        if (layer == null || layer.antialiasingMode != PostProcessLayer.Antialiasing.TemporalAntialiasing || ResolveDisabledField?.GetValue(layer) is true)
            return false;

        Player? player = Singleton<GameWorld>.Instance?.MainPlayer;
        return player != null && player.IsYourPlayer && player.HandsController != null && player.HandsController.IsAiming;
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
