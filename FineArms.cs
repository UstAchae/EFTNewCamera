using System.Collections.Generic;
using EFT;
using EFT.Visual;
using UnityEngine;
using UnityEngine.Rendering;

namespace EFTNewCamera;

// Swaps the third-person arms for the game's first-person arm model. That model is loaded for every player but
// only drawn in first person, skinned to a separate hands skeleton. Here it is skinned to the body skeleton
// instead, so it follows the third-person animation and the mod's arm IK, and the body top is drawn from a copy
// of its mesh with the arm triangles taken out.
internal static class FineArms
{
    internal static bool Enabled;

    // A body triangle goes when its corners lean, on average, at least this much on arm bones.
    private const float ArmWeightToCut = 0.5f;

    private sealed class HandsState
    {
        internal SkinnedMeshRenderer Renderer = null!;
        internal Transform[] Bones = null!;
        internal Transform RootBone = null!;
        internal bool Enabled;
        internal ShadowCastingMode Shadows;
        internal bool ForceMatrix;
        internal bool UpdateWhenOffscreen;
    }

    private static readonly List<HandsState> Hands = new();
    private static readonly Dictionary<Mesh, Mesh> ArmlessBySource = new();
    private static readonly Dictionary<Mesh, Mesh> SourceByArmless = new();
    private static readonly List<SkinnedMeshRenderer> BodyRenderers = new();
    private static PlayerBody? _body;
    private static LoddedSkin? _handsSkin;
    private static bool _failed;

    internal static void Apply(PlayerBody body)
    {
        if (!Enabled)
        {
            Restore();
            return;
        }

        if (!body.BodySkins.TryGetValue(EBodyModelPart.Hands, out LoddedSkin? handsSkin) || handsSkin == null
            || !body.BodySkins.TryGetValue(EBodyModelPart.Body, out LoddedSkin? bodySkin) || bodySkin == null)
            return;

        if (!ReferenceEquals(_body, body) || !ReferenceEquals(_handsSkin, handsSkin))
        {
            Restore();
            _body = body;
            _handsSkin = handsSkin;
            _failed = !Reskin(body, handsSkin);
        }

        if (_failed)
            return;

        foreach (HandsState hands in Hands)
        {
            if (hands.Renderer == null)
                continue;

            hands.Renderer.enabled = true;
            hands.Renderer.shadowCastingMode = ShadowCastingMode.On;
        }

        // The body top swaps its mesh when armor, a vest or a backpack goes on, so the armless copy is checked
        // every time.
        foreach (Renderer renderer in bodySkin.GetRenderers())
        {
            if (renderer is not SkinnedMeshRenderer skinned || skinned == null)
                continue;

            Mesh? mesh = skinned.sharedMesh;
            if (mesh == null || SourceByArmless.ContainsKey(mesh))
                continue;

            Mesh? armless = Armless(skinned, mesh);
            if (armless == null)
                continue;

            skinned.sharedMesh = armless;
            if (!BodyRenderers.Contains(skinned))
                BodyRenderers.Add(skinned);
        }
    }

    internal static void Restore()
    {
        foreach (HandsState hands in Hands)
        {
            if (hands.Renderer == null)
                continue;

            hands.Renderer.bones = hands.Bones;
            hands.Renderer.rootBone = hands.RootBone;
            hands.Renderer.enabled = hands.Enabled;
            hands.Renderer.shadowCastingMode = hands.Shadows;
            hands.Renderer.forceMatrixRecalculationPerRender = hands.ForceMatrix;
            hands.Renderer.updateWhenOffscreen = hands.UpdateWhenOffscreen;
        }

        foreach (SkinnedMeshRenderer renderer in BodyRenderers)
        {
            if (renderer != null && renderer.sharedMesh != null
                && SourceByArmless.TryGetValue(renderer.sharedMesh, out Mesh source))
                renderer.sharedMesh = source;
        }

        foreach (Mesh armless in SourceByArmless.Keys)
        {
            if (armless != null)
                Object.Destroy(armless);
        }

        Hands.Clear();
        BodyRenderers.Clear();
        ArmlessBySource.Clear();
        SourceByArmless.Clear();
        _body = null;
        _handsSkin = null;
        _failed = false;
    }

    // Points every bone of the arm model at the body bone of the same name.
    private static bool Reskin(PlayerBody body, LoddedSkin handsSkin)
    {
        Dictionary<string, Transform> bodyBones = new();
        if (body.SkeletonRootJoint != null)
        {
            foreach (Transform bone in body.SkeletonRootJoint.Bones.Values)
            {
                if (bone != null && !bodyBones.ContainsKey(bone.name))
                    bodyBones.Add(bone.name, bone);
            }
        }

        List<(SkinnedMeshRenderer Renderer, Transform[] Bones, Transform Root)> mapped = new();
        foreach (Renderer renderer in handsSkin.GetRenderers())
        {
            if (renderer is not SkinnedMeshRenderer skinned || skinned == null)
                continue;

            Transform[] bones = skinned.bones;
            Transform[] moved = new Transform[bones.Length];
            for (int i = 0; i < bones.Length; i++)
            {
                if (bones[i] == null || !bodyBones.TryGetValue(bones[i].name, out moved[i]))
                    return false;
            }

            Transform? root = skinned.rootBone;
            if (root == null || !bodyBones.TryGetValue(root.name, out Transform movedRoot))
                return false;

            mapped.Add((skinned, moved, movedRoot));
        }

        if (mapped.Count == 0)
            return false;

        foreach ((SkinnedMeshRenderer renderer, Transform[] bones, Transform root) in mapped)
        {
            Hands.Add(new HandsState
            {
                Renderer = renderer,
                Bones = renderer.bones,
                RootBone = renderer.rootBone!,
                Enabled = renderer.enabled,
                Shadows = renderer.shadowCastingMode,
                ForceMatrix = renderer.forceMatrixRecalculationPerRender,
                UpdateWhenOffscreen = renderer.updateWhenOffscreen
            });
            renderer.bones = bones;
            renderer.rootBone = root;
            renderer.forceMatrixRecalculationPerRender = true;
            renderer.updateWhenOffscreen = true;
        }

        return true;
    }

    private static Mesh? Armless(SkinnedMeshRenderer renderer, Mesh source)
    {
        if (ArmlessBySource.TryGetValue(source, out Mesh cached) && cached != null)
            return cached;

        if (!source.isReadable)
            return null;

        Transform[] bones = renderer.bones;
        bool[] arm = new bool[bones.Length];
        for (int i = 0; i < bones.Length; i++)
            arm[i] = bones[i] != null && IsArm(bones[i].name);

        BoneWeight[] weights = source.boneWeights;
        if (weights.Length != source.vertexCount)
            return null;

        float[] armWeight = new float[weights.Length];
        for (int i = 0; i < weights.Length; i++)
        {
            BoneWeight w = weights[i];
            armWeight[i] = Weight(arm, w.boneIndex0, w.weight0) + Weight(arm, w.boneIndex1, w.weight1)
                + Weight(arm, w.boneIndex2, w.weight2) + Weight(arm, w.boneIndex3, w.weight3);
        }

        Mesh armless = Object.Instantiate(source);
        armless.name = source.name + "_armless";
        List<int> kept = new();
        for (int sub = 0; sub < source.subMeshCount; sub++)
        {
            int[] triangles = source.GetTriangles(sub);
            kept.Clear();
            for (int t = 0; t + 2 < triangles.Length; t += 3)
            {
                int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
                if ((armWeight[a] + armWeight[b] + armWeight[c]) / 3f >= ArmWeightToCut)
                    continue;

                kept.Add(a);
                kept.Add(b);
                kept.Add(c);
            }

            armless.SetTriangles(kept, sub, false);
        }

        ArmlessBySource[source] = armless;
        SourceByArmless[armless] = source;
        return armless;
    }

    private static float Weight(bool[] arm, int bone, float weight)
    {
        return bone >= 0 && bone < arm.Length && arm[bone] ? weight : 0f;
    }

    private static bool IsArm(string name)
    {
        return name.Contains("Upperarm") || name.Contains("Forearm") || name.Contains("Palm") || name.Contains("Digit");
    }
}
