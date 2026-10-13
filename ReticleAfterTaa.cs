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
// While aiming, the held weapon's reticles are kept out of the main camera's normal pass and drawn into the
// TAA output instead, before bloom and tone mapping.
internal static class ReticleAfterTaa
{
    internal static bool Enabled = true;

    // Not in the main camera's culling mask, and nothing in the game puts renderers on it.
    private const int HiddenLayer = 19;

    private static readonly FieldInfo? ResolveDisabledField = AccessTools.Field(typeof(PostProcessLayer), "_isTAAResolveDisabled");
    private static readonly List<CollimatorSight> Sights = new List<CollimatorSight>();
    private static readonly List<(GameObject Target, int Layer)> Hidden = new List<(GameObject, int)>();
    private static bool _installed;
    private static bool _logged;

    internal static void Install()
    {
        if (_installed)
            return;

        Harmony harmony = new Harmony(NewCameraPlugin.PluginGuid + ".reticle");
        MethodInfo? render = AccessTools.Method(typeof(TemporalAntialiasing), "Render");
        MethodInfo? postfix = typeof(ReticleAfterTaa).GetMethod(nameof(RenderPostfix), BindingFlags.NonPublic | BindingFlags.Static);
        if (render != null && postfix != null)
            harmony.Patch(render, postfix: new HarmonyMethod(postfix));

        _installed = true;
    }

    internal static void LateUpdate()
    {
        // Hide before any camera culls. Restoring here covers a frame where the main camera never finished.
        Restore();
        HideHeldReticles();
    }

    internal static void OnCameraPostRender(Camera camera)
    {
        if (camera != null && camera.CompareTag("MainCamera"))
            Restore();
    }

    private static void RenderPostfix(PostProcessRenderContext context)
    {
        Camera camera = context.camera;
        if (!Enabled || camera == null || !camera.CompareTag("MainCamera") || Hidden.Count == 0)
            return;

        PostProcessLayer layer = camera.GetComponent<PostProcessLayer>();
        if (layer == null || ResolveDisabledField?.GetValue(layer) is true)
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
            if (renderer == null || renderer.sharedMaterial == null)
                continue;
            // The transform is read when the buffer runs, after the gun has been aligned to the camera.
            context.command.DrawRenderer(renderer, renderer.sharedMaterial, 0, 0);
        }
        context.command.SetViewProjectionMatrices(view, jittered);
    }

    private static void HideHeldReticles()
    {
        if (!Enabled)
            return;

        Camera? camera = Camera.main;
        PostProcessLayer? layer = camera != null ? camera.GetComponent<PostProcessLayer>() : null;
        if (layer == null || layer.antialiasingMode != PostProcessLayer.Antialiasing.TemporalAntialiasing || ResolveDisabledField?.GetValue(layer) is true)
            return;

        Player? player = Singleton<GameWorld>.Instance?.MainPlayer;
        if (player == null || !player.IsYourPlayer || player.HandsController == null || !player.HandsController.IsAiming)
            return;

        Transform? muzzle = (player.HandsController as Player.FirearmController)?.CurrentFireport?.Original;
        if (muzzle == null)
            return;

        muzzle.root.GetComponentsInChildren(false, Sights);
        foreach (CollimatorSight sight in Sights)
        {
            MeshRenderer renderer = sight.CollimatorMeshRenderer;
            if (!sight.isActiveAndEnabled || renderer == null || !renderer.enabled || renderer.sharedMaterial == null)
                continue;

            GameObject target = renderer.gameObject;
            if (target.layer == HiddenLayer)
                continue;

            Hidden.Add((target, target.layer));
            target.layer = HiddenLayer;
        }
        Sights.Clear();
        if (!_logged && Hidden.Count > 0)
        {
            _logged = true;
            Debug.Log("[EFTNewCamera] collimator reticles are drawn after TAA while aiming");
        }
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
