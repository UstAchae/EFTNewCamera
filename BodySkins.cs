using System.Collections.Generic;
using EFT;
using UnityEngine;

namespace EFTNewCamera;

// The player's skinned meshes, and the upper arms among their bones. Bones this mod moves after the animator has
// posed them are drawn only if each skin works its matrices out again for every render, also off screen; how each
// skin had that is kept and handed back.
internal static class BodySkins
{
    private struct SkinState
    {
        public SkinnedMeshRenderer Renderer;
        public bool ForceMatrix;
        public bool UpdateWhenOffscreen;
    }

    private static int _skinOwnerId;
    internal static readonly List<Transform> Upperarms = new();
    private static readonly List<SkinState> Skins = new();

    internal static void EnsureUpperarms(Player player)
    {
        int ownerId = player.GetInstanceID();
        if (_skinOwnerId == ownerId && Upperarms.Count > 0)
            return;

        Restore();
        _skinOwnerId = ownerId;
        HashSet<Transform> seen = new();
        foreach (SkinnedMeshRenderer renderer in player.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (renderer == null)
                continue;

            bool carriesArm = false;
            foreach (Transform bone in renderer.bones)
            {
                if (bone == null || !IsUpperarm(bone.name))
                    continue;
                carriesArm = true;
                if (seen.Add(bone))
                    Upperarms.Add(bone);
            }

            if (!carriesArm)
            {
                RememberSkin(renderer);
                continue;
            }

            RememberSkin(renderer);
        }
    }

    private static int _bodySkinOwner = int.MinValue;

    internal static void EnsureBody(Player player)
    {
        int ownerId = player.GetInstanceID();
        if (_bodySkinOwner == ownerId)
            return;

        _bodySkinOwner = ownerId;
        foreach (SkinnedMeshRenderer renderer in player.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (renderer != null)
                RememberSkin(renderer);
        }
    }

    private static void RememberSkin(SkinnedMeshRenderer renderer)
    {
        foreach (SkinState skin in Skins)
        {
            if (skin.Renderer == renderer)
                return;
        }

        Skins.Add(new SkinState
        {
            Renderer = renderer,
            ForceMatrix = renderer.forceMatrixRecalculationPerRender,
            UpdateWhenOffscreen = renderer.updateWhenOffscreen
        });
        renderer.forceMatrixRecalculationPerRender = true;
        renderer.updateWhenOffscreen = true;
    }

    private static bool IsUpperarm(string name)
    {
        string normalized = name.Replace(" ", string.Empty).Replace("_", string.Empty);
        return normalized.IndexOf("upperarm", System.StringComparison.OrdinalIgnoreCase) >= 0;
    }

    internal static void Restore()
    {
        foreach (SkinState skin in Skins)
        {
            if (skin.Renderer == null)
                continue;
            skin.Renderer.forceMatrixRecalculationPerRender = skin.ForceMatrix;
            skin.Renderer.updateWhenOffscreen = skin.UpdateWhenOffscreen;
        }

        Skins.Clear();
        Upperarms.Clear();
        _skinOwnerId = 0;
        _bodySkinOwner = int.MinValue;
    }
}
