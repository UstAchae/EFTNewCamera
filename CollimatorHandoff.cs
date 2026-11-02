using Comfort.Common;
using EFT;
using UnityEngine;

namespace EFTNewCamera;

// The red dot or holographic sight's reticle on the gun in hand, handed to the game's upscaler as the game means to:
// the upscaler keeps it out of the frame it blends over frames (the reticle has no motion vectors, so under DLSS it
// smears, all the more under night vision's gain) and draws it itself once the upscale is done, with the renderer's
// own properties (BRNVG's dimming under night vision). The game hands it over only when it calibrates the sight
// (ProceduralWeaponAnimation.Calibrate, CollimatorSight.LookAt, SSAA.SetCollimatorRenderer), which seldom happens.
// Not while thermal goggles show: their picture is a camera of ours, which would no longer see it.
internal static class CollimatorHandoff
{
    private static Renderer? _given;
    private static SSAA? _givenTo;

    // As the FPS camera culls.
    internal static void Update(Camera camera)
    {
        SSAA? ssaa = camera.GetComponent<SSAA>();
        if (ssaa == null)
            return;
        Renderer? wanted = null;
        int id = 0;
        Player? player = Singleton<GameWorld>.Instance?.MainPlayer;
        ScopePrefabCache? cache = Scopes.Current(player?.ProceduralWeaponAnimation)?.ScopePrefabCache;
        if (cache != null && cache.HasCollimators && !NightVisionEyepieces.ThermalShown)
        {
            for (int i = 0; i < cache.ModesCount; i++)
            {
                CollimatorSight? sight = cache.GetScopeModeInfo(i).CollimatorSight;
                MeshRenderer? renderer = sight != null ? sight.CollimatorMeshRenderer : null;
                if (sight == null || renderer == null || !sight.isActiveAndEnabled || !renderer.enabled)
                    continue;
                wanted = renderer;
                id = sight.gameObject.GetInstanceID();
                break;
            }
        }

        if (wanted != null)
        {
            if (ssaa.GetCollimatorRenderer() != wanted)
            {
                ssaa.SetCollimatorRenderer(wanted, id);
                _given = wanted;
                _givenTo = ssaa;
            }
        }
        else
            Release();
    }

    // The reticle given back to the frame (the upscaler shows it again): not DisableCollimatorRenderer, which clears
    // the upscaler's optic lens instead and leaves the reticle hidden.
    internal static void Release()
    {
        if (_given != null && _givenTo != null && _givenTo.GetCollimatorRenderer() == _given)
            _givenTo.SetCollimatorRenderer(null, 0);
        _given = null;
        _givenTo = null;
    }
}
