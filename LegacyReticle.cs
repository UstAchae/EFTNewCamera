using EFT.CameraControl;
using UnityEngine;
using HarmonyLib;

namespace EFTNewCamera;

// Older scopes (including PU) draw their mark in the lens material, not OpticRetrice.
internal static class LegacyReticle
{
    private static Renderer? _renderer;
    private static Material[]? _original;
    private static Material? _mark;
    private static Vector4 _shifts;
    private static RenderTexture? _sourceMark, _shiftedMark;
    private static float _lastUv = float.NaN;
    private static int _materialIndex;
    private static MaterialPropertyBlock? _originalBlock;
    private static readonly MaterialPropertyBlock DrawBlock = new();
    private static Vector4 _wanted;
    private static Vector3 _perV;
    private static bool _hasGeometry;
    private static readonly int ShiftsId = Shader.PropertyToID("_Shifts");
    private static readonly AccessTools.FieldRef<SSAAPropagator, Material> CachedMaterial =
        AccessTools.FieldRefAccess<SSAAPropagator, Material>("_opticLensMaterial");
    private static readonly AccessTools.FieldRef<SSAAPropagator, Renderer> CachedRenderer =
        AccessTools.FieldRefAccess<SSAAPropagator, Renderer>("_opticLensRenderer");

    internal static void Install()
    {
        new Harmony(NewCameraPlugin.PluginGuid + ".legacyReticle").Patch(
            AccessTools.Method(typeof(SSAAPropagator), "FillCommandBuffer"),
            prefix: new HarmonyMethod(typeof(LegacyReticle), nameof(BeforeComposite)),
            postfix: new HarmonyMethod(typeof(LegacyReticle), nameof(AfterComposite)));
    }

    private static void BeforeComposite(SSAAPropagator __instance, out Material? __state)
    {
        __state = null;
        if (_mark == null || _renderer == null || CachedRenderer(__instance) != _renderer) return;
        // Renderer property blocks take precedence over values on the material passed to DrawRenderer.
        // Set the effective per-slot value at the last draw-command preparation point as well.
        _renderer.GetPropertyBlock(DrawBlock, _materialIndex);
        bool hadOverride = DrawBlock.HasVector(ShiftsId);
        Vector4 before = DrawBlock.GetVector(ShiftsId);
        DrawBlock.SetVector(ShiftsId, _wanted);
        if (_shiftedMark != null) DrawBlock.SetTexture("_MarkTex", _shiftedMark);
        _renderer.SetPropertyBlock(DrawBlock, _materialIndex);
        __state = CachedMaterial(__instance);
        CachedMaterial(__instance) = _mark;
        Camera? optic = CameraManager.Instance?.OpticCameraManager?.Camera;
        if (optic != null)
            OpticDiagnostics.Trace("legacy.ssaa.bind", optic,
                $"renderer={_renderer.GetInstanceID()} oldMaterial={(__state != null ? __state.GetInstanceID() : 0)} drawMaterial={_mark.GetInstanceID()} shift={_mark.GetVector("_Shifts").x:R} slot={_materialIndex} hadBlockShift={hadOverride} beforeBlock={before} wantedBlock={_wanted}");
    }

    private static void AfterComposite(SSAAPropagator __instance, Material? __state)
    {
        // DrawRenderer has captured the instance material in its command. Preserve native cache ownership.
        if (__state != null && CachedMaterial(__instance) == _mark) CachedMaterial(__instance) = __state;
    }

    internal static void Apply(OpticSight? sight, Camera optic, float mrad, string template)
    {
        Renderer? lens = sight?.LensRenderer;
        Camera? main = Camera.main;
        if (lens == null || main == null) { Release(); return; }
        if (_renderer != lens)
        {
            Release();
            Material[] materials = lens.sharedMaterials;
            for (int i = 0; i < materials.Length; i++)
            {
                Material material = materials[i];
                if (material == null || !material.HasProperty("_MarkTex") || !material.HasProperty("_Shifts")
                    || !material.HasProperty("_ShiftDirection") || !material.HasProperty("_Scales")) continue;
                _original = (Material[])materials.Clone();
                _renderer = lens;
                _materialIndex = i;
                _originalBlock = new MaterialPropertyBlock();
                lens.GetPropertyBlock(_originalBlock, i);
                _mark = new Material(material) { name = material.name + " (A reticle instance)", hideFlags = HideFlags.HideAndDontSave };
                _shifts = material.GetVector("_Shifts");
                Mesh? mesh = lens.GetComponent<MeshFilter>()?.sharedMesh;
                if (mesh != null && mesh.isReadable)
                {
                    Vector3[] vertices = mesh.vertices;
                    Vector2[] uv = mesh.uv;
                    int[] triangles = mesh.GetTriangles(i);
                    if (uv.Length == vertices.Length)
                        for (int t = 0; t + 2 < triangles.Length && !_hasGeometry; t += 3)
                        {
                            int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
                            _hasGeometry = SightReferenceMath.UvVertical(vertices[a], vertices[b], vertices[c], uv[a], uv[b], uv[c], out _perV);
                        }
                }
                if (!_hasGeometry && mesh != null)
                    _hasGeometry = SightReferenceMath.NativeVertical(template, mesh.name, mesh.vertexCount, mesh.bounds.size, out _perV);
                Texture source = material.GetTexture("_MarkTex");
                if (source != null)
                {
                    _sourceMark = new RenderTexture(source.width, source.height, 0, RenderTextureFormat.ARGB32)
                        { name = "A reticle source", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
                    _shiftedMark = new RenderTexture(source.width, source.height, 0, RenderTextureFormat.ARGB32)
                        { name = "A reticle translated", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
                    RenderTexture active = RenderTexture.active;
                    try { Graphics.Blit(source, _sourceMark); }
                    finally { RenderTexture.active = active; }
                    _mark.SetTexture("_MarkTex", _shiftedMark);
                }
                materials[i] = _mark;
                lens.sharedMaterials = materials;
                break;
            }
        }
        if (_mark == null) return;
        if (_sourceMark == null || _shiftedMark == null) return;
        Vector4 scales = _mark.GetVector("_Scales");
        // Native mark UV is 10*uv-4.5; its virtual quad is Scales.x/Scales.y times the lens quad.
        // Translate the alpha image itself, preserving all native mark/mask geometry and alignment.
        float ratio = Mathf.Abs(scales.x) > 0.000001f ? Mathf.Abs(scales.y / scales.x) : 1f;
        float uvShift = 0.5f * Mathf.Tan(mrad / 1000f) / Mathf.Tan(optic.fieldOfView * Mathf.Deg2Rad * 0.5f) * 10f * ratio;
        if (float.IsNaN(_lastUv) || Mathf.Abs(uvShift - _lastUv) > 0.0000001f)
        {
            RenderTexture active = RenderTexture.active;
            try { Graphics.Blit(_sourceMark, _shiftedMark, Vector2.one, new Vector2(0f, -uvShift)); }
            finally { RenderTexture.active = active; }
            _lastUv = uvShift;
        }
        _wanted = _shifts;
        lens.GetPropertyBlock(DrawBlock, _materialIndex);
        DrawBlock.SetVector(ShiftsId, _shifts);
        DrawBlock.SetTexture("_MarkTex", _shiftedMark);
        lens.SetPropertyBlock(DrawBlock, _materialIndex);
        OpticDiagnostics.Trace("legacy.texture", optic, $"mrad={mrad:R} uvShift={uvShift:R} texture={_shiftedMark.GetInstanceID()} originalGeometryUnchanged=True");
    }

    internal static void Release()
    {
        if (_renderer != null) _renderer.SetPropertyBlock(_originalBlock, _materialIndex);
        if (_renderer != null && _original != null) _renderer.sharedMaterials = _original;
        if (_mark != null) Object.Destroy(_mark);
        _renderer = null; _original = null; _mark = null;
        _originalBlock = null;
        if (_sourceMark != null) { _sourceMark.Release(); Object.Destroy(_sourceMark); }
        if (_shiftedMark != null) { _shiftedMark.Release(); Object.Destroy(_shiftedMark); }
        _sourceMark = null; _shiftedMark = null; _lastUv = float.NaN;
        _hasGeometry = false; _perV = Vector3.zero;
    }

    internal static bool TryGetTranslation(out Vector3 translation)
    {
        translation = Vector3.zero;
        if (_renderer == null || _mark == null || !_hasGeometry || float.IsNaN(_lastUv)) return false;
        translation = SightReferenceMath.LegacyTranslation(_renderer.localToWorldMatrix, _perV, _mark.GetVector("_Scales").x, _lastUv);
        return true;
    }

    internal static bool TryGetAimPoint(Vector3 nativeOrigin, Vector3 nativeAxis, out Vector3 zeroPoint, out Vector3 currentPoint)
    {
        zeroPoint = currentPoint = Vector3.zero;
        if (_renderer == null || _mark == null || !TryGetTranslation(out Vector3 translation)) return false;
        Mesh? mesh = _renderer.GetComponent<MeshFilter>()?.sharedMesh;
        if (mesh == null) return false;
        Matrix4x4 matrix = SightReferenceMath.LegacyMatrix(_renderer.localToWorldMatrix);
        Vector4 axis = _mark.GetVector("_ShiftDirection");
        Vector3 local = mesh.bounds.center*_mark.GetVector("_Scales").x
            + new Vector3(axis.x,axis.y,axis.z)*_shifts.x;
        Vector3 centre = matrix.MultiplyPoint3x4(local);
        Vector3 normal = Vector3.Cross(matrix.MultiplyVector(Vector3.right),matrix.MultiplyVector(Vector3.forward)).normalized;
        // The user-confirmed native axis defines the zero aim point on the actual virtual mark plane.
        // No pixel recognition of an arbitrary texture tip is claimed here.
        if (!SightReferenceMath.AxisPlanePoint(nativeOrigin,nativeAxis,centre,normal,out zeroPoint)) return false;
        currentPoint = zeroPoint+translation;
        return true;
    }
}
