using System.Collections.Generic;
using Diz.Skinning;
using EFT;
using EFT.Visual;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace EFTNewCamera;

// Swaps the third-person hands for the hands of the game's first-person arm model, and puts on its wristwatch.
// That model is loaded for every player but only drawn in first person, skinned to a separate hands skeleton.
// Here it is skinned to the body skeleton instead, so it follows the third-person animation and the mod's arm
// IK. Its sleeves are a separate drawing that does not match the worn top, so only its hands are drawn from a
// copy of its mesh; the top keeps its own sleeves and loses its hands the same way.
internal static class FineArms
{
    internal static bool Enabled;

    // A triangle counts as hand when its corners lean, on average, at least this much on palm and finger bones.
    private const float HandWeight = 0.5f;

    private static readonly AccessTools.FieldRef<PlayerBody, PluggableBone> WatchField =
        AccessTools.FieldRefAccess<PlayerBody, PluggableBone>("_watches");

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
    private static readonly Dictionary<Mesh, Mesh> CutBySource = new();
    private static readonly Dictionary<Mesh, Mesh> SourceByCut = new();
    private static readonly List<SkinnedMeshRenderer> CutRenderers = new();
    private static PlayerBody? _body;
    private static LoddedSkin? _handsSkin;
    private static bool _failed;
    private static Transform? _watch;
    private static Transform? _watchParent;
    private static bool _watchActive;

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
            Dictionary<string, Transform> bodyBones = BodyBones(body);
            _failed = !Reskin(handsSkin, bodyBones);
            if (!_failed)
                PutOnWatch(body, bodyBones);
        }

        if (_failed)
            return;

        foreach (HandsState hands in Hands)
        {
            if (hands.Renderer == null)
                continue;

            hands.Renderer.enabled = true;
            hands.Renderer.shadowCastingMode = ShadowCastingMode.On;
            Cut(hands.Renderer, true);
        }

        // The game turns the watch off whenever it redraws the body for third person.
        if (_watch != null && !_watch.gameObject.activeSelf)
            _watch.gameObject.SetActive(true);

        // The body top swaps its mesh when armor, a vest or a backpack goes on, so its cut copy is checked
        // every time.
        foreach (Renderer renderer in bodySkin.GetRenderers())
        {
            if (renderer is SkinnedMeshRenderer skinned && skinned != null)
                Cut(skinned, false);
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

        foreach (SkinnedMeshRenderer renderer in CutRenderers)
        {
            if (renderer != null && renderer.sharedMesh != null
                && SourceByCut.TryGetValue(renderer.sharedMesh, out Mesh source))
                renderer.sharedMesh = source;
        }

        foreach (Mesh cut in SourceByCut.Keys)
        {
            if (cut != null)
                Object.Destroy(cut);
        }

        if (_watch != null && _watchParent != null)
        {
            _watch.SetParent(_watchParent, false);
            _watch.gameObject.SetActive(_watchActive);
        }

        Hands.Clear();
        CutRenderers.Clear();
        CutBySource.Clear();
        SourceByCut.Clear();
        _body = null;
        _handsSkin = null;
        _failed = false;
        _watch = null;
        _watchParent = null;
    }

    private static Dictionary<string, Transform> BodyBones(PlayerBody body)
    {
        Dictionary<string, Transform> bones = new();
        if (body.SkeletonRootJoint == null)
            return bones;

        foreach (Transform bone in body.SkeletonRootJoint.Bones.Values)
        {
            if (bone != null && !bones.ContainsKey(bone.name))
                bones.Add(bone.name, bone);
        }

        return bones;
    }

    // Points every bone of the arm model at the body bone of the same name.
    private static bool Reskin(LoddedSkin handsSkin, Dictionary<string, Transform> bodyBones)
    {
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

    // The watch hangs on a bone of the hands skeleton; it moves, with its place on that bone, to the body bone
    // of the same name. Its own script keeps turning the hands.
    private static void PutOnWatch(PlayerBody body, Dictionary<string, Transform> bodyBones)
    {
        PluggableBone? watch = WatchField(body);
        Transform? parent = watch != null ? watch.transform.parent : null;
        if (watch == null || parent == null || !bodyBones.TryGetValue(parent.name, out Transform bone))
            return;

        _watch = watch.transform;
        _watchParent = parent;
        _watchActive = watch.gameObject.activeSelf;
        _watch.SetParent(bone, false);
        _watch.gameObject.SetActive(true);
    }

    // Draws the renderer from a copy of its mesh that keeps only the hands, or everything but the hands.
    private static void Cut(SkinnedMeshRenderer renderer, bool keepHands)
    {
        Mesh? mesh = renderer.sharedMesh;
        if (mesh == null || SourceByCut.ContainsKey(mesh))
            return;

        if (!CutBySource.TryGetValue(mesh, out Mesh cut) || cut == null)
        {
            Mesh? made = CutCopy(renderer, mesh, keepHands);
            if (made == null)
                return;

            cut = made;
            CutBySource[mesh] = cut;
            SourceByCut[cut] = mesh;
        }

        renderer.sharedMesh = cut;
        if (!CutRenderers.Contains(renderer))
            CutRenderers.Add(renderer);
    }

    private static Mesh? CutCopy(SkinnedMeshRenderer renderer, Mesh source, bool keepHands)
    {
        if (!source.isReadable)
            return null;

        Transform[] bones = renderer.bones;
        bool[] hand = new bool[bones.Length];
        for (int i = 0; i < bones.Length; i++)
            hand[i] = bones[i] != null && IsHand(bones[i].name);

        BoneWeight[] weights = source.boneWeights;
        if (weights.Length != source.vertexCount)
            return null;

        float[] handWeight = new float[weights.Length];
        for (int i = 0; i < weights.Length; i++)
        {
            BoneWeight w = weights[i];
            handWeight[i] = Weight(hand, w.boneIndex0, w.weight0) + Weight(hand, w.boneIndex1, w.weight1)
                + Weight(hand, w.boneIndex2, w.weight2) + Weight(hand, w.boneIndex3, w.weight3);
        }

        Mesh cut = Object.Instantiate(source);
        cut.name = source.name + (keepHands ? "_hands" : "_handless");
        List<int> kept = new();
        for (int sub = 0; sub < source.subMeshCount; sub++)
        {
            int[] triangles = source.GetTriangles(sub);
            kept.Clear();
            for (int t = 0; t + 2 < triangles.Length; t += 3)
            {
                int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
                bool isHand = (handWeight[a] + handWeight[b] + handWeight[c]) / 3f >= HandWeight;
                if (isHand != keepHands)
                    continue;

                kept.Add(a);
                kept.Add(b);
                kept.Add(c);
            }

            cut.SetTriangles(kept, sub, false);
        }

        return cut;
    }

    private static float Weight(bool[] hand, int bone, float weight)
    {
        return bone >= 0 && bone < hand.Length && hand[bone] ? weight : 0f;
    }

    private static bool IsHand(string name)
    {
        return name.Contains("Palm") || name.Contains("Digit");
    }
}
