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

    internal static void OnCameraPostRender(Camera camera)
    {
        Restore();
    }

    private static void RenderPostfix(PostProcessRenderContext context)
    {
        Restore();
        Camera camera = context.camera;
        if (!Enabled || camera == null || !camera.CompareTag("MainCamera"))
            return;

        PostProcessLayer layer = camera.GetComponent<PostProcessLayer>();
        if (layer == null || ResolveDisabledField?.GetValue(layer) is true)
            return;

        Player? player = Singleton<GameWorld>.Instance?.MainPlayer;
        if (player == null || !player.IsYourPlayer || player.HandsController == null || !player.HandsController.IsAiming)
            return;

        Transform? muzzle = (player.HandsController as Player.FirearmController)?.CurrentFireport?.Original;
        if (muzzle == null)
            return;

        muzzle.root.GetComponentsInChildren(false, Sights);
        bool any = false;
        foreach (CollimatorSight sight in Sights)
        {
            MeshRenderer renderer = sight.CollimatorMeshRenderer;
            if (!sight.isActiveAndEnabled || renderer == null || !renderer.enabled || renderer.sharedMaterial == null)
                continue;

            GameObject target = renderer.gameObject;
            if (target.layer == HiddenLayer)
                continue;

            if (!any)
            {
                context.command.SetRenderTarget(context.destination);
                any = true;
            }
            Hidden.Add((target, target.layer));
            target.layer = HiddenLayer;
            context.command.DrawRenderer(renderer, renderer.sharedMaterial, 0, 0);
        }
        Sights.Clear();
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
