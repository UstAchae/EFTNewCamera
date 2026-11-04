using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace EFTNewCamera;

// Tritium vials seen through a magnified sight with the goggle down. The sight is focused far off, so a vial a few
// metres away is out of its focus too: its objective spreads the vial into a disc as wide, in the sight's field, as
// the objective's radius over the vial's distance; the sight then magnifies the picture with the disc in it, and the
// goggle, focused at infinity as the sight's picture is, sees that disc sharp. So the discs are drawn into the sight
// camera's own picture (passes 8 and 9 of NvgObjective.shader, as for the frame, with the sight camera's view, its
// depth, and its objective for the aperture), before the light through the sight is counted (pass 11), which dims
// them with the rest. Hidden where something nearer stands in the way, from the sight camera's depth.
internal static partial class NightVisionEyepieces
{
    private const float ScopeFocus = 100f;               // where a sight is focused (m): the game has no figure for it
    private const float ScopeBlurLimit = 24f;            // the most a sight's own blur spreads (quarter-size pixels)
    private static Material? _scopeTritium;
    private static readonly int[] ScopeAllIds = Ids("_JustachaeScopeAll", 5), ScopeNearIds = Ids("_JustachaeScopeNear", 5), ScopeNearBlurIds = Ids("_JustachaeScopeNearBlur", 3);
    private static readonly int ScopeCutId = Shader.PropertyToID("_JustachaeScopeCut"), ScopeAcrossId = Shader.PropertyToID("_JustachaeScopeAcross");
    private static readonly int ScopeSharpId = Shader.PropertyToID("_JustachaeScopeSharp");
    private static readonly int StepShaderId = Shader.PropertyToID("_Step");

    private static int[] Ids(string name, int count)
    {
        var ids = new int[count];
        for (int i = 0; i < count; i++)
            ids[i] = Shader.PropertyToID(name + i);
        return ids;
    }
    private static RenderTexture? _opticOcclusion;
    private static Mesh? _opticDiscs;
    private static float _scopeObjectiveRadius;           // the sight's objective's (m), while one is looked through
    private static DepthTextureMode _opticDepthBefore;
    private static readonly List<VialSeen> _opticSeen = new();
    private static readonly Vector4[] _opticShape = new Vector4[MaxVials], _opticTint = new Vector4[MaxVials], _opticMasks = new Vector4[MaxVials];
    private static readonly float[] _opticTrace = new float[MaxVials];
    private static readonly List<Vector3> _discCorners = new(), _discAcross = new();
    private static readonly List<int> _discIndices = new();
    private static readonly int TanHalfShaderId = Shader.PropertyToID("_TanHalf");
    private static readonly int LensShaderId = Shader.PropertyToID("_Lens");

    // As the sight camera begins to render: the vials it sees, for its picture's discs.
    private static void NoteOptic(Camera optic)
    {
        if (_scopeBufferOn != optic || _scopeTritium == null || _opticDiscs == null || _scopeObjectiveRadius <= 0f)
            return;
        _dgOpticPos = optic.transform.position; // TEMP DIAG
        _dgOpticRot = optic.transform.rotation;
        _dgOpticProj = optic.projectionMatrix;
        _dgOpticNonJittered = optic.nonJitteredProjectionMatrix;
        _dgOpticFrame = Time.frameCount;
        Vector3 from = optic.transform.position;
        Matrix4x4 view = optic.worldToCameraMatrix, viewProjection = optic.nonJitteredProjectionMatrix * view;
        float tanUp = Mathf.Tan(optic.fieldOfView * 0.5f * Mathf.Deg2Rad);
        int width = Mathf.Max(optic.pixelWidth, 1), height = Mathf.Max(optic.pixelHeight, 1);
        float pixelsPerRadian = height / (2f * tanUp);
        _opticSeen.Clear();
        if (TritiumBrightness > 0f)
            foreach (VialSet set in _vialSets)
            {
                if (set.InHand || set.Renderer == null || !set.Renderer.enabled || !set.Renderer.gameObject.activeInHierarchy)
                    continue;                           // the gun's own sights are behind the sight
                Matrix4x4 toWorld = set.Renderer.localToWorldMatrix;
                foreach (var key in set.Submeshes)
                {
                    if (!VialsByMesh.TryGetValue(key, out Vial[]? vials) || vials == null)
                        continue;
                    foreach (Vial vial in vials)
                    {
                        Vector3 at = toWorld.MultiplyPoint3x4(vial.Centre);
                        Vector3 toSight = from - at;
                        float distance = toSight.magnitude;
                        if (distance < 0.05f || distance > VialRange)
                            continue;
                        float facing = vial.Normal.sqrMagnitude < 0.09f ? 1f
                            : Mathf.Clamp01(Vector3.Dot(toWorld.MultiplyVector(vial.Normal).normalized, toSight / distance) * 3f);
                        Vector4 clip = viewProjection * new Vector4(at.x, at.y, at.z, 1f);
                        float depth = -view.MultiplyPoint3x4(at).z;
                        if (facing <= 0f || clip.w <= 0f || depth <= 0f)
                            continue;
                        Vector2 uv = new Vector2(clip.x / clip.w * 0.5f + 0.5f, clip.y / clip.w * 0.5f + 0.5f);
                        float radius = _scopeObjectiveRadius / distance * pixelsPerRadian;
                        if (uv.x < -radius * 3f / width || uv.x > 1f + radius * 3f / width || uv.y < -radius * 3f / height || uv.y > 1f + radius * 3f / height)
                            continue;
                        float glow = VialGlow * TritiumBrightness * facing;
                        if (radius < MinRadius)
                        {
                            glow *= radius * radius / (MinRadius * MinRadius);
                            radius = MinRadius;
                        }
                        if (Time.time - _dgVialTime > 3f) // TEMP DIAG: the vial against its renderer's middle, in the sight camera
                        {
                            _dgVialTime = Time.time;
                            Vector3 middle = set.Renderer.bounds.center;
                            Vector4 mc = viewProjection * new Vector4(middle.x, middle.y, middle.z, 1f);
                            NewCameraPlugin.Log?.LogInfo($"TEMP DIAG sight vial: {set.Renderer.name} vial at {at.ToString("F3")} uv ({uv.x:F3},{uv.y:F3}) radius {radius:F1} px, {distance:F2} m; renderer middle {middle.ToString("F3")} uv ({mc.x / mc.w * 0.5f + 0.5f:F3},{mc.y / mc.w * 0.5f + 0.5f:F3}); renderer {set.Renderer.GetType().Name}, scale {set.Renderer.transform.lossyScale.ToString("F3")}, sight camera at {from.ToString("F3")} looking {optic.transform.forward.ToString("F3")}");
                        }
                        _opticSeen.Add(new VialSeen
                        {
                            Distance = distance, At = at,
                            Shape = new Vector4(uv.x, uv.y, radius, glow),
                            Tint = new Vector4(vial.Tint.r, vial.Tint.g, vial.Tint.b, depth),
                        });
                    }
                }
            }
        _opticSeen.Sort(ByDistance);
        int count = Mathf.Min(_opticSeen.Count, MaxVials);
        _discCorners.Clear();
        _discAcross.Clear();
        _discIndices.Clear();
        for (int k = 0; k < count; k++)
        {
            Vector4 v = _opticSeen[k].Shape;
            _opticShape[k] = v;
            _opticTint[k] = _opticSeen[k].Tint;
            float r = Mathf.Max(v.z, 1f);
            for (int c = 0; c < 4; c++)
            {
                float across = c == 0 || c == 3 ? -3f : 3f, up = c < 2 ? -3f : 3f;
                _discIndices.Add(_discCorners.Count);
                _discCorners.Add(new Vector3(v.x + across * r / width, v.y + up * r / height, 0f));
                _discAcross.Add(new Vector3(across, up, k));
            }
        }
        _opticDiscs.Clear();
        _opticDiscs.SetVertices(_discCorners);
        _opticDiscs.SetUVs(0, _discAcross);
        _opticDiscs.SetIndices(_discIndices, MeshTopology.Quads, 0, false);
        _opticDiscs.bounds = new Bounds(Vector3.zero, Vector3.one * 10f);

        _scopeTritium.SetVectorArray(VialsId, _opticShape);
        _scopeTritium.SetVectorArray(VialTintId, _opticTint);
        _scopeTritium.SetVectorArray(VialMasksId, _opticMasks);
        _scopeTritium.SetFloatArray(VialTraceId, _opticTrace);
        _scopeTritium.SetFloat(VialCountId, count);
        _scopeTritium.SetVector(TargetId, new Vector4(width, height, _scopeObjectiveRadius, 0f));
        _scopeTritium.SetVector(TanHalfShaderId, new Vector2(tanUp * width / height, tanUp));
        // The aperture at the sight camera; its own blur of what is near: focused at ScopeFocus, its objective's radius
        // in quarter-size pixels per radian of the defocus, at most ScopeBlurLimit.
        float quarterPerRadian = height / 4f / (2f * tanUp);
        _scopeTritium.SetVector(LensShaderId, new Vector4(ScopeFocus, _scopeObjectiveRadius * quarterPerRadian, 0f, ScopeBlurLimit));
        _scopeTritium.SetTexture(OcclusionId, _opticOcclusion);
    }

    private static float _dgVialTime; // TEMP DIAG
    // TEMP DIAG: how far the sight's axis is behind the view as the view turns, with and without the goggle.
    private static Vector3 _dgLagLast;
    private static float _dgLagTime, _dgLagSum, _dgLagMax, _dgTurnSum, _dgLagFrames, _dgLagPull;
    private static int _dgLagFrame = -1;
    private static void DiagSightLag(Camera camera)
    {
        Camera? optic = null;
        try { optic = EFT.CameraControl.CameraManager.Instance?.OpticCameraManager?.Camera; } catch { }
        Camera? main = Camera.main;
        if (optic == null || camera != optic || main == null || !optic.enabled || _dgLagFrame == Time.frameCount)
            return;
        _dgLagFrame = Time.frameCount;
        Vector3 view = main.transform.forward;
        float lag = Vector3.Angle(view, optic.transform.forward);
        float turn = Time.unscaledDeltaTime > 0f && _dgLagLast != Vector3.zero ? Vector3.Angle(view, _dgLagLast) / Time.unscaledDeltaTime : 0f;
        _dgLagLast = view;
        _dgLagSum += lag;
        _dgLagMax = Mathf.Max(_dgLagMax, lag);
        _dgTurnSum += turn;
        _dgLagFrames++;
        _dgLagPull = Mathf.Max(_dgLagPull, ScopePullback);
        if (Time.time - _dgLagTime < 3f)
            return;
        float meanLag = _dgLagSum / Mathf.Max(_dgLagFrames, 1f), meanTurn = _dgTurnSum / Mathf.Max(_dgLagFrames, 1f);
        NewCameraPlugin.Log?.LogInfo($"TEMP DIAG sight lag over 3 s ({_dgLagFrames:F0} frames, goggle {(_dgLagPull > 0.01f ? "down, camera ahead " + (_dgLagPull * 100f).ToString("F1") + " cm" : "not used")}): sight behind the view {meanLag:F3} deg on average, at most {_dgLagMax:F3}; view turning {meanTurn:F1} deg/s on average; behind over turning {(meanTurn > 1f ? (meanLag / meanTurn).ToString("F3") + " s" : "-")}");
        _dgLagTime = Time.time;
        _dgLagSum = _dgLagMax = _dgTurnSum = _dgLagFrames = _dgLagPull = 0f;
    }
    private static int _dgLensMaterial; // TEMP DIAG
    // TEMP DIAG: the sight lens's material and its properties, once a material.
    private static void DiagLensMaterial(Renderer lens)
    {
        Material? m = lens.sharedMaterial;
        if (m == null || m.GetInstanceID() == _dgLensMaterial || m.shader == null)
            return;
        _dgLensMaterial = m.GetInstanceID();
        var text = new System.Text.StringBuilder();
        Shader shader = m.shader;
        for (int i = 0; i < shader.GetPropertyCount(); i++)
        {
            string name = shader.GetPropertyName(i);
            var type = shader.GetPropertyType(i);
            string value = type switch
            {
                ShaderPropertyType.Float or ShaderPropertyType.Range => m.GetFloat(name).ToString("F4"),
                ShaderPropertyType.Vector => m.GetVector(name).ToString("F4"),
                ShaderPropertyType.Color => m.GetColor(name).ToString(),
                ShaderPropertyType.Texture => m.GetTexture(name)?.name ?? "none",
                _ => "?"
            };
            text.Append("  ").Append(name).Append(" = ").AppendLine(value);
        }
        NewCameraPlugin.Log?.LogInfo($"TEMP DIAG sight lens {lens.name} material {m.name} shader {shader.name} keywords [{string.Join(",", m.shaderKeywords)}], materials {lens.sharedMaterials.Length}:" + System.Environment.NewLine + text);
    }

    // The sight's sharp picture cut down to a quarter by a pass, then spread at each size, each level the last halved
    // and spread again (as Blurred does for the frame), into the levels' temporaries.
    private static void BlurredInto(CommandBuffer buffer, int pass, int[] levels)
    {
        for (int i = 0; i < levels.Length; i++)
        {
            int size = -(4 << i);
            buffer.GetTemporaryRT(ScopeCutId, size, size, 0, FilterMode.Bilinear, RenderTextureFormat.ARGBHalf);
            if (i == 0)
                buffer.Blit(ScopeSharpId, ScopeCutId, _scopeTritium, pass);
            else
                buffer.Blit(levels[i - 1], ScopeCutId);
            buffer.GetTemporaryRT(ScopeAcrossId, size, size, 0, FilterMode.Bilinear, RenderTextureFormat.ARGBHalf);
            buffer.SetGlobalVector(StepShaderId, new Vector4(1f, 0f, 0f, 0f));
            buffer.Blit(ScopeCutId, ScopeAcrossId, _scopeTritium, 1);
            buffer.GetTemporaryRT(levels[i], size, size, 0, FilterMode.Bilinear, RenderTextureFormat.ARGBHalf);
            buffer.SetGlobalVector(StepShaderId, new Vector4(0f, 1f, 0f, 0f));
            buffer.Blit(ScopeAcrossId, levels[i], _scopeTritium, 2);
            buffer.ReleaseTemporaryRT(ScopeAcrossId);
            buffer.ReleaseTemporaryRT(ScopeCutId);
        }
    }

    // TEMP DIAG: whether the sight camera moved or changed its projection between its pre-render and its post-render.
    private static Vector3 _dgOpticPos;
    private static Quaternion _dgOpticRot;
    private static Matrix4x4 _dgOpticProj, _dgOpticNonJittered;
    private static int _dgOpticFrame = -1, _dgOpticRenders;
    private static float _dgOpticTime, _dgOpticMoved, _dgOpticTurned, _dgOpticShift;
    private static void DiagOpticAfter(Camera optic)
    {
        if (_dgOpticFrame != Time.frameCount)
            return;
        _dgOpticRenders++;
        _dgOpticMoved = Mathf.Max(_dgOpticMoved, (optic.transform.position - _dgOpticPos).magnitude);
        _dgOpticTurned = Mathf.Max(_dgOpticTurned, Quaternion.Angle(optic.transform.rotation, _dgOpticRot));
        Matrix4x4 p = optic.projectionMatrix;
        _dgOpticShift = Mathf.Max(_dgOpticShift, Mathf.Abs(p.m02 - _dgOpticNonJittered.m02) + Mathf.Abs(p.m12 - _dgOpticNonJittered.m12) + Mathf.Abs(p.m11 - _dgOpticNonJittered.m11));
        if (Time.time - _dgOpticTime < 3f)
            return;
        NewCameraPlugin.Log?.LogInfo($"TEMP DIAG sight camera pre- to post-render over 3 s ({_dgOpticRenders} renders): moved up to {_dgOpticMoved * 1000f:F2} mm, turned up to {_dgOpticTurned:F3} deg, projection vs the non-jittered one noted up to {_dgOpticShift:F5}; now proj m02 {p.m02:F5} m12 {p.m12:F5} m11 {p.m11:F4}, non-jittered m02 {optic.nonJitteredProjectionMatrix.m02:F5} m12 {optic.nonJitteredProjectionMatrix.m12:F5} m11 {optic.nonJitteredProjectionMatrix.m11:F4}, pixels {optic.pixelWidth}x{optic.pixelHeight}, rect {optic.rect}");
        _dgOpticTime = Time.time;
        _dgOpticMoved = _dgOpticTurned = _dgOpticShift = 0f;
        _dgOpticRenders = 0;
    }

    // The sight camera's steps after it has rendered: the vials' occlusion and discs, then the light through the
    // sight that reaches the goggle.
    private static CommandBuffer MakeScopeBuffer()
    {
        _scopeTritium ??= new Material(_objectiveShader!) { name = "Justachae NVG sight tritium", hideFlags = HideFlags.DontSave };
        if (_opticOcclusion == null)
        {
            _opticOcclusion = new RenderTexture(4 * OcclusionTile, 4 * OcclusionTile, 0, RenderTextureFormat.RHalf)
            { name = "Justachae NVG sight tritium occlusion", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            _opticOcclusion.Create();
        }
        if (_opticDiscs == null)
        {
            _opticDiscs = new Mesh { name = "Justachae NVG sight tritium discs", hideFlags = HideFlags.DontSave };
            _opticDiscs.MarkDynamic();
        }
        // The sight's picture, its near blurred by the sight's own objective (passes 0 to 5 and 3, as for the frame,
        // with the sight's defocus for the blur: Blur in NvgObjective.shader), its vials' discs laid on (8, 9), and
        // the light through it that reaches the goggle counted (11).
        var buffer = new CommandBuffer { name = "Justachae NVG sight picture" };
        buffer.GetTemporaryRT(ScopeSharpId, -1, -1, 0, FilterMode.Bilinear, RenderTextureFormat.DefaultHDR);
        buffer.Blit(BuiltinRenderTextureType.CameraTarget, ScopeSharpId);
        BlurredInto(buffer, 0, ScopeAllIds);
        BlurredInto(buffer, 4, ScopeNearIds);
        BlurredInto(buffer, 5, ScopeNearBlurIds);
        for (int i = 0; i < 5; i++)
        {
            buffer.SetGlobalTexture(AllIds[i], ScopeAllIds[i]);
            buffer.SetGlobalTexture(NearIds[i], ScopeNearIds[i]);
        }
        buffer.SetGlobalTexture(NearBlurId, ScopeNearBlurIds[2]);
        buffer.GetTemporaryRT(ScopeTempId, -1, -1, 0, FilterMode.Bilinear, RenderTextureFormat.DefaultHDR);
        buffer.Blit(ScopeSharpId, ScopeTempId, _scopeTritium, 3);
        foreach (int[] ids in new[] { ScopeAllIds, ScopeNearIds, ScopeNearBlurIds })
            foreach (int id in ids)
                buffer.ReleaseTemporaryRT(id);
        buffer.ReleaseTemporaryRT(ScopeSharpId);
        buffer.Blit(Texture2D.blackTexture, _opticOcclusion, _scopeTritium, 8);
        buffer.SetRenderTarget(ScopeTempId);
        buffer.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.Ortho(0f, 1f, 0f, 1f, -1f, 1f));  // the buffer makes it the GPUs own
        buffer.DrawMesh(_opticDiscs, Matrix4x4.identity, _scopeTritium, 0, 9);
        buffer.Blit(ScopeTempId, BuiltinRenderTextureType.CameraTarget, _scopeLight, 11);
        buffer.ReleaseTemporaryRT(ScopeTempId);
        return buffer;
    }
}
