using UnityEngine;
using UnityEngine.Rendering;

namespace EFTNewCamera;

// The eyes draw everything within half a metre of them, but give the frame only the device: all the rest, the hands,
// the gun, the headwear and the world near by, is the main camera's, as everything further is. Which of an eye's
// pixels are the device's is marked after its G-buffer, by drawing the device's meshes again where they lie (depth
// equal, the depth being theirs where they are seen) into a mask of the eye's own. Everything is still drawn, so
// the light and the shadows on the device are as they were.
internal static partial class NightVisionEyepieces
{
    private const int SelfMaskPass = 2;   // NvgEyeOptics.shader

    private static readonly RenderTexture?[] _selfMasks = new RenderTexture?[2];
    private static readonly CommandBuffer?[] _selfMaskBuffers = new CommandBuffer?[2];
    private static readonly int SelfMaskId = Shader.PropertyToID("_SelfMask");
    // The device's drawn meshes (each of the finest level of its LOD group) with their submesh counts, found again
    // only when its renderers are.
    private static Renderer[] _maskOf = System.Array.Empty<Renderer>();
    private static readonly System.Collections.Generic.List<(Renderer renderer, int submeshes)> _maskParts = new();

    private static void FindMaskParts(Renderer[] device)
    {
        if (ReferenceEquals(device, _maskOf))
            return;
        _maskOf = device;
        _maskParts.Clear();
        foreach (Renderer r in device)
        {
            Mesh? mesh = r is SkinnedMeshRenderer skinned ? skinned.sharedMesh
                : r != null && r.TryGetComponent(out MeshFilter filter) ? filter.sharedMesh : null;
            if (mesh != null && FinestLevel(r!))
                _maskParts.Add((r!, mesh.subMeshCount));
        }
    }

    // This eye's mask of the device, drawn after its G-buffer (after the pieces the eye puts in itself,
    // NightVisionPrecise): as the camera draws it, or as the eye put it in.
    private static void PutSelfMask(int side, Camera eye, int width, int height, Vector3 shift, Matrix4x4 turned, Matrix4x4 projection)
    {
        if (_eyeOptics == null || _body == null)
            return;
        Renderer[] device = DeviceRenderers();
        RenderTexture mask = Target(_selfMasks, side, width, height, 0, RenderTextureFormat.R8, "Justachae NVG eye, the device");
        CommandBuffer buffer = _selfMaskBuffers[side] ??= new CommandBuffer { name = "Justachae NVG the device" };
        eye.RemoveCommandBuffer(CameraEvent.AfterGBuffer, buffer);
        buffer.Clear();
        buffer.SetRenderTarget(mask, BuiltinRenderTextureType.CameraTarget);
        buffer.ClearRenderTarget(false, true, Color.clear);
        FindMaskParts(device);
        foreach ((Renderer r, int submeshes) in _maskParts)
        {
            // Only what the camera draws: not the glass left out, nor what the eye puts in itself (left to its
            // shadow while the eye draws).
            if (r == null || !r.enabled || r.forceRenderingOff || r.shadowCastingMode == ShadowCastingMode.ShadowsOnly
                || !r.gameObject.activeInHierarchy)
                continue;
            for (int s = 0; s < submeshes; s++)
                buffer.DrawRenderer(r, _eyeOptics, s, SelfMaskPass);
        }
        if (_precise)
        {
            buffer.SetViewProjectionMatrices(turned, projection);
            Matrix4x4 fromEye = Matrix4x4.Translate(-shift);
            for (int i = 0; i < _headwearPieces.Count; i++)
            {
                HeadwearPiece piece = _headwearPieces[i];
                if (!_pieceShown[i] || System.Array.IndexOf(device, piece.Renderer) < 0)
                    continue;
                Matrix4x4 m = fromEye * piece.FromCamera;
                for (int s = 0; s < piece.Mesh.subMeshCount; s++)
                    buffer.DrawMesh(piece.Mesh, m, _eyeOptics, s, SelfMaskPass);
            }
            buffer.SetViewProjectionMatrices(eye.worldToCameraMatrix, eye.projectionMatrix);
        }
        eye.AddCommandBuffer(CameraEvent.AfterGBuffer, buffer);
    }

    private static void TakeSelfMask(int side, Camera eye)
    {
        if (_selfMaskBuffers[side] != null)
            eye.RemoveCommandBuffer(CameraEvent.AfterGBuffer, _selfMaskBuffers[side]);
    }

    private static Texture SelfMask(int side) => _selfMasks[side] != null ? _selfMasks[side]! : Texture2D.whiteTexture;
}
