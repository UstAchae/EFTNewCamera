using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace EFTNewCamera;

// Far out in a map a place in the world is kept only to a few tens of micrometres, and every point of the device and
// of the camera is rounded to that on its own, each frame anew: with the head's least motion (a breath, the mouse)
// the device, centimetres from the eye, shook by a pixel or two against it. So the eyes do not draw the headwear and
// the device where the game keeps them: each eye puts them into its G-buffer itself, placed from the eye by where
// they truly stand from it (from the head down the bones by their small local offsets, less where the camera was put
// from the head: NewCameraSwitch.PoseFromHead; never by the difference of two rounded places in the world), seen from
// the eye turned but not moved. The G-buffer holds only depth, normals and surface, nothing of the world's places, so
// the lights, the shadows and the game's ambient light then fall on them from the eye's true place as on anything.
// They still cast their shadows from where the game keeps them (the device too, which the game has cast none).
internal static partial class NightVisionEyepieces
{
    private struct HeadwearPiece
    {
        internal Renderer Renderer;
        internal Mesh Mesh;
        internal Matrix4x4 FromCamera;        // its mesh's frame from the camera (turned with the world, not moved)
        internal Material[] Materials;
        internal int[] Passes;                // each material's G-buffer pass (-1: none)
    }

    private static readonly List<HeadwearPiece> _headwearPieces = new List<HeadwearPiece>();
    private static readonly Dictionary<Transform, Matrix4x4> _rootsFromCamera = new Dictionary<Transform, Matrix4x4>();
    private static readonly List<(Renderer renderer, bool off, ShadowCastingMode shadows)> _precisePut = new List<(Renderer, bool, ShadowCastingMode)>();
    private static readonly CommandBuffer?[] _preciseBuffers = new CommandBuffer?[2];
    private static readonly MaterialPropertyBlock _pieceBlock = new MaterialPropertyBlock();
    private static readonly List<bool> _pieceShown = new List<bool>();
    private static readonly ShaderTagId LightModeTag = new ShaderTagId("LightMode");
    private static Shader? _stencilMarkShader;
    private static Material? _stencilMark;
    private static bool _precise;

    // The headwear and the device as they stand from the camera this frame; false (the eyes draw them as the game
    // keeps them) away from the player's eyes, where the camera's place from the head is not known.
    private static bool PrepareRelative(Camera camera)
    {
        _precise = false;
        _headwearPieces.Clear();
        _rootsFromCamera.Clear();
        Transform? head = NewCameraSwitch.PoseHead;
        if (head == null || !NewCameraSwitch.AtEyes(camera) || _stencilMarkShader == null)
            return false;

        var roots = new List<Transform>();
        EFT.PlayerBody? body = Comfort.Common.Singleton<EFT.GameWorld>.Instance?.MainPlayer?.PlayerBody;
        if (body != null)
        {
            foreach (EFT.InventoryLogic.EquipmentSlot slot in new[] { EFT.InventoryLogic.EquipmentSlot.Headwear, EFT.InventoryLogic.EquipmentSlot.FaceCover, EFT.InventoryLogic.EquipmentSlot.Eyewear })
            {
                if (body.SlotViews.TryGetByKey(slot, out EFT.PlayerBody.SlotView? slotView) && slotView?.Model != null)
                    roots.Add(slotView.Model.transform);
            }
        }
        if (_body != null)
            roots.Add(DeviceSlot());
        foreach (Transform root in roots)
        {
            bool taken = false;
            foreach (Transform t in _rootsFromCamera.Keys)
                taken |= root.IsChildOf(t);
            if (taken)
                continue;
            Vector3? fromHead = FromHead(head, root);
            if (fromHead == null)
                continue;
            _rootsFromCamera[root] = Matrix4x4.TRS(fromHead.Value - NewCameraSwitch.PoseFromHead, root.rotation, root.lossyScale);
        }
        if (_rootsFromCamera.Count == 0)
            return false;

        foreach (KeyValuePair<Transform, Matrix4x4> root in _rootsFromCamera)
        {
            foreach (MeshRenderer r in root.Key.GetComponentsInChildren<MeshRenderer>(false))
            {
                // Left to its shadow alone: headwear the player hides (BodyVisibility); not the device, left to its
                // shadow only while the main camera draws (ShowDevice).
                bool hidden = r.shadowCastingMode == ShadowCastingMode.ShadowsOnly && System.Array.IndexOf(DeviceRenderers(), r) < 0;
                if (!r.enabled || hidden || !FinestLevel(r)
                    || !r.TryGetComponent(out MeshFilter filter) || filter.sharedMesh == null)
                    continue;
                Material[] materials = r.sharedMaterials;
                var passes = new int[materials.Length];
                bool any = false;
                for (int i = 0; i < materials.Length; i++)
                {
                    passes[i] = GBufferPass(materials[i]);
                    any |= passes[i] >= 0;
                }
                if (!any)
                    continue;
                _headwearPieces.Add(new HeadwearPiece
                {
                    Renderer = r, Mesh = filter.sharedMesh, Materials = materials, Passes = passes,
                    FromCamera = root.Value * Chain(root.Key, r.transform),
                });
            }
        }
        _precise = _headwearPieces.Count > 0;
        return _precise;
    }

    // Where a transform under the headwear stands from the camera (turned with the world); for one elsewhere, from
    // its place in the world (as coarse as that is).
    private static Matrix4x4 FromCamera(Transform t, Vector3 cameraPlace)
    {
        foreach (KeyValuePair<Transform, Matrix4x4> root in _rootsFromCamera)
        {
            if (t.IsChildOf(root.Key))
                return root.Value * Chain(root.Key, t);
        }
        return Matrix4x4.Translate(-cameraPlace) * t.localToWorldMatrix;
    }

    private static int GBufferPass(Material? material)
    {
        if (material == null)
            return -1;
        for (int i = 0; i < material.passCount; i++)
        {
            // The game's shaders spell it in capitals.
            if (string.Equals(material.shader.FindPassTagValue(i, LightModeTag).name, "Deferred", System.StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    // A renderer of the finest level of its LOD group, or of none.
    private static bool FinestLevel(Renderer renderer)
    {
        LODGroup? group = renderer.GetComponentInParent<LODGroup>();
        if (group == null)
            return true;
        LOD[] lods = group.GetLODs();
        if (lods.Length == 0 || System.Array.IndexOf(lods[0].renderers, renderer) >= 0)
            return true;
        for (int i = 1; i < lods.Length; i++)
        {
            if (System.Array.IndexOf(lods[i].renderers, renderer) >= 0)
                return false;
        }
        return true;
    }

    // While this eye draws: the pieces left out of its own drawing (casting their shadows still, those that cast
    // any), and put into its G-buffer from the eye (shift: the eye from the camera) by its buffer.
    private static void PutPieces(int side, Camera eye, Vector3 shift, Matrix4x4 turned, Matrix4x4 projection)
    {
        _precisePut.Clear();
        _pieceShown.Clear();
        foreach (HeadwearPiece piece in _headwearPieces)
        {
            Renderer r = piece.Renderer;
            _pieceShown.Add(!r.forceRenderingOff);
            _precisePut.Add((r, r.forceRenderingOff, r.shadowCastingMode));
            // Its shadow cast all the same, the device's too (the game has it cast none): the eyecups' walls and the
            // housing keep the sun off the lenses and out of the cups.
            if (!r.forceRenderingOff)
                r.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
        }

        _stencilMark ??= new Material(_stencilMarkShader) { name = "Justachae NVG stencil mark", hideFlags = HideFlags.DontSave };
        _preciseBuffers[side] ??= new CommandBuffer { name = "Justachae NVG headwear from the eye" };
        CommandBuffer buffer = _preciseBuffers[side]!;
        eye.RemoveCommandBuffer(CameraEvent.AfterGBuffer, buffer);
        buffer.Clear();
        buffer.SetRenderTarget(new RenderTargetIdentifier[]
        {
            BuiltinRenderTextureType.GBuffer0, BuiltinRenderTextureType.GBuffer1, BuiltinRenderTextureType.GBuffer2, BuiltinRenderTextureType.GBuffer3,
        }, BuiltinRenderTextureType.CameraTarget);
        buffer.SetViewProjectionMatrices(turned, projection);
        Matrix4x4 fromEye = Matrix4x4.Translate(-shift);
        for (int i = 0; i < _headwearPieces.Count; i++)
        {
            HeadwearPiece piece = _headwearPieces[i];
            if (!_pieceShown[i])
                continue;
            piece.Renderer.GetPropertyBlock(_pieceBlock);
            Matrix4x4 m = fromEye * piece.FromCamera;
            int count = Mathf.Min(piece.Materials.Length, piece.Mesh.subMeshCount);
            for (int s = 0; s < count; s++)
            {
                if (piece.Passes[s] >= 0)
                    buffer.DrawMesh(piece.Mesh, m, piece.Materials[s], s, piece.Passes[s], _pieceBlock);
            }
            for (int s = 0; s < piece.Mesh.subMeshCount; s++)
                buffer.DrawMesh(piece.Mesh, m, _stencilMark, s, 0);
        }
        // Where the pieces are, for the eyecups' fading (NightVisionShade): their place from the eye and their normal.
        if (_device.Sealed && _shadeMaterial != null && ShadeKeep < 0.999f)
        {
            // In textures of their own, with a depth of their own: the nearest of the pieces kept, whatever the
            // camera's own depth is like, and there still when the frame is lit.
            RenderTexture place = ShadeTexture(_shadePlaces, side, eye.pixelWidth, eye.pixelHeight, 24, RenderTextureFormat.ARGBFloat);
            RenderTexture facing = ShadeTexture(_shadeFacings, side, eye.pixelWidth, eye.pixelHeight, 0, RenderTextureFormat.ARGBHalf);
            buffer.SetRenderTarget(new RenderTargetIdentifier[] { place, facing }, place.depthBuffer);
            buffer.ClearRenderTarget(true, true, Color.clear);
            buffer.SetGlobalTexture(ShadePlaceId, place);
            buffer.SetGlobalTexture(ShadeFacingId, facing);
            for (int i = 0; i < _headwearPieces.Count; i++)
            {
                if (!_pieceShown[i])
                    continue;
                HeadwearPiece piece = _headwearPieces[i];
                Matrix4x4 m = fromEye * piece.FromCamera;
                for (int s = 0; s < piece.Mesh.subMeshCount; s++)
                    buffer.DrawMesh(piece.Mesh, m, _shadeMaterial, s, 1);
            }
        }
        buffer.SetViewProjectionMatrices(eye.worldToCameraMatrix, eye.projectionMatrix);
        eye.AddCommandBuffer(CameraEvent.AfterGBuffer, buffer);
    }

    private static void TakePieces(int side, Camera eye)
    {
        if (_preciseBuffers[side] != null)
            eye.RemoveCommandBuffer(CameraEvent.AfterGBuffer, _preciseBuffers[side]);
        foreach ((Renderer renderer, bool off, ShadowCastingMode shadows) in _precisePut)
        {
            if (renderer == null)
                continue;
            renderer.forceRenderingOff = off;
            renderer.shadowCastingMode = shadows;
        }
        _precisePut.Clear();
    }
}
