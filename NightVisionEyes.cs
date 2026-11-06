using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;

namespace EFTNewCamera;

// The binocular view's two eyes (see NightVisionEyepieces). The main camera renders the scene once, with all its
// effects, but not the device, which is hidden from it on the frames the eyes draw. Each eye is a camera at its
// own place drawing what is within half a metre (the device, the helmet's edge) over its half of the main view,
// with its coverage (NvgEyeOptics.shader). A lens map from the same place says where the eye looks through a lens
// past the housing; there BRNVG's picture of the main camera's frame shows, in the direction of that line of sight.
// The device goes over the main camera's half; elsewhere the scene is the main camera's. The scene a few metres
// off looks the same from either eye (they are centimetres apart), so the main camera's single picture of it
// serves both. With one camera both eyes stay where it is.
internal static partial class NightVisionEyepieces
{
    private const float EyeNear = 0.01f;          // the eyecups are a couple of centimetres away
    private const float EyeFar = 0.5f;            // the eyes draw only what is close; the scene is the main camera's
    private const float BehindLens = 0.1f;        // through a lens, what is further from the eye than this is beyond it
    private static readonly int LensMapId = Shader.PropertyToID("_LensMap");
    private static readonly int BehindLensId = Shader.PropertyToID("_BehindLens");
    private const float LensMapScale = 1f;   // the lenses' edges are sharp: the map at the eye's own size
    private const float LensMapRange = 1f;
    private const float HistoryWeight = 0.9f;
    private const float Overscan = 0.06f;         // each eye's view past the middle of the frame, as a share of it
    // A tube's field, as the half angle off its axis within which a line of sight through its lens finds the
    // picture: wide enough for the whole "8" from where the camera sits behind it, so the lenses go dark only when
    // seen from well off to the side.
    private const float TubeField = 30f, TubeFieldFade = 5f;

    private static readonly int HistoryId = Shader.PropertyToID("_History");
    private static readonly int HistoryWeightId = Shader.PropertyToID("_HistoryWeight");
    private static readonly int TubeFieldId = Shader.PropertyToID("_TubeField");
    private static readonly int[] TubeAxisIds =
    {
        Shader.PropertyToID("_TubeAxis0"), Shader.PropertyToID("_TubeAxis1"), Shader.PropertyToID("_TubeAxis2"), Shader.PropertyToID("_TubeAxis3"),
    };
    private static readonly int[] LensIds4 =
    {
        Shader.PropertyToID("_Lens0"), Shader.PropertyToID("_Lens1"), Shader.PropertyToID("_Lens2"), Shader.PropertyToID("_Lens3"),
    };
    private static readonly int[] AxisIds =
    {
        Shader.PropertyToID("_Axis0"), Shader.PropertyToID("_Axis1"), Shader.PropertyToID("_Axis2"), Shader.PropertyToID("_Axis3"),
    };
    private static readonly int[] LensFloorIds =
    {
        Shader.PropertyToID("_LensFloor0"), Shader.PropertyToID("_LensFloor1"), Shader.PropertyToID("_LensFloor2"), Shader.PropertyToID("_LensFloor3"),
    };
    private static readonly int OpeningId = Shader.PropertyToID("_Opening");
    private static readonly int EyeId = Shader.PropertyToID("_Eye");
    private static readonly int OverscanId = Shader.PropertyToID("_Overscan");
    private static readonly int MonocularId = Shader.PropertyToID("_Monocular");
    private static readonly int SplitLineId = Shader.PropertyToID("_SplitLine");
    private static readonly int EyeSpanId = Shader.PropertyToID("_EyeSpan");
    private static readonly int LensClearId = Shader.PropertyToID("_LensClear");
    private static readonly int OpticsId = Shader.PropertyToID("_Optics");
    private static readonly int[] EyeIds = { Shader.PropertyToID("_LeftEye"), Shader.PropertyToID("_RightEye") };
    private static readonly int[] LensIds = { Shader.PropertyToID("_LeftLens"), Shader.PropertyToID("_RightLens") };
    private static readonly int[] EyeInvVpIds = { Shader.PropertyToID("_EyeInvVP0"), Shader.PropertyToID("_EyeInvVP1") };
    private static readonly int[] EyePosIds = { Shader.PropertyToID("_EyePos0"), Shader.PropertyToID("_EyePos1") };

    private static Material? _eyeOptics;
    private static readonly Camera?[] _eyeCameras = new Camera?[2];   // left, right
    private static readonly RenderTexture?[] _eyeViews = new RenderTexture?[2];
    private static readonly RenderTexture?[] _eyeHistory = new RenderTexture?[2];
    private static readonly int[] _historyFrame = { -100, -100 };
    private static readonly RenderTexture?[] _lensMaps = new RenderTexture?[2];
    private static Material? _lensMapMaterial;
    private static Mesh? _card;
    private static CommandBuffer? _lensMapBuffer;
    private static Renderer[] _blockers = System.Array.Empty<Renderer>();
    private static Transform? _blockersOf;
    private static int _eyesFrame = -100;
    private static Renderer[] _deviceRenderers = System.Array.Empty<Renderer>();
    private static Transform? _deviceRenderersOf;
    private static bool _deviceHidden;

    // Made outside any camera's rendering.
    private static void EnsureEyeCameras()
    {
        for (int side = 0; side < 2; side++)
        {
            if (_eyeCameras[side] != null)
                continue;
            GameObject go = new GameObject(side == 0 ? "Justachae NVG left eye" : "Justachae NVG right eye");
            Object.DontDestroyOnLoad(go);
            Camera camera = go.AddComponent<Camera>();
            camera.enabled = false;
            go.AddComponent<EyeOptics>().Side = side;
            _eyeCameras[side] = camera;
        }
    }

    // The main camera leaves the device to the eyes on the frames they draw it: hidden from the main camera while
    // it culls and renders, shown to each eye while it renders. Other cameras (a free camera, the inventory) and
    // other frames see it as always. Hidden it still casts its shadow (the main camera draws what it falls on).
    private static void ShowDevice(bool shown)
    {
        Renderer[] renderers = DeviceRenderers();
        if (_deviceShadows.Length != renderers.Length)
            _deviceShadows = new ShadowCastingMode[renderers.Length];
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer renderer = renderers[i];
            if (renderer == null)
                continue;
            if (!shown)
            {
                _deviceShadows[i] = renderer.shadowCastingMode;
                renderer.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
            }
            else if (renderer.shadowCastingMode == ShadowCastingMode.ShadowsOnly)
                renderer.shadowCastingMode = _deviceShadows[i];
        }
    }

    private static ShadowCastingMode[] _deviceShadows = System.Array.Empty<ShadowCastingMode>();

    private static Renderer[] DeviceRenderers()
    {
        if (_deviceRenderers.Length > 0 && _deviceRenderersOf == _body && System.Array.TrueForAll(_deviceRenderers, r => r != null))
            return _deviceRenderers;
        Transform root = DeviceSlot();
        _deviceRenderers = root.GetComponentsInChildren<Renderer>(true);
        _deviceRenderersOf = _body;
        return _deviceRenderers;
    }

    // The device's glass (what is not opaque) left out of an eye's view: no reflections on it. The lenses' picture is
    // laid on by the composite, not the glass.
    private static void HideGlass()
    {
        foreach (Renderer renderer in DeviceRenderers())
        {
            if (renderer != null && !Opaque(renderer))
                renderer.forceRenderingOff = true;
        }
    }

    private static bool EyesDraw(Camera camera)
    {
        // Only while our image effect is on to lay the eyes' views over the frame: the device is hidden from the camera
        // for them to draw it.
        if (!EyesOn || _body == null || _lensNodes.Length == 0 || _eyeOptics == null || camera != Camera.main
            || _eyeCameras[0] == null || _eyeCameras[1] == null || _eyepieceImage == null || !_eyepieceImage.enabled)
            return false;
        Vector3 mount = _rotator != null ? _rotator.transform.position : _body.position;
        return (mount - camera.transform.position).sqrMagnitude <= MaxEyeDistance * MaxEyeDistance;
    }

    internal static void OnCameraPreCull(Camera camera)
    {
        LightFace(camera);
        if (camera != Camera.main)
            return;
        HideIrLaserLights(camera);
        if (_body != null)
            AdjustMount(camera);
        _deviceHidden = EyesDraw(camera);
        if (_deviceHidden)
            ShowDevice(false);
    }

    internal static void OnCameraPostRender(Camera camera)
    {
        if (camera != Camera.main)
            return;
        RestoreMount();
        if (!_deviceHidden)
            return;
        _deviceHidden = false;
        if (_body != null)
            ShowDevice(true);
    }

    // Just before the main camera renders, when its pose for the frame is final: each eye, moved apart as far as the
    // device has flipped down; or, with one camera, the lens map from the main camera (only once the filter is on).
    internal static void OnCameraPreRender(Camera camera)
    {
        if (camera != Camera.main || _body == null || _binocularMaterial == null || _lensNodes.Length == 0)
            return;
        Material binocular = _binocularMaterial;
        Transform view = camera.transform;
        Vector3 position = view.position;
        binocular.SetMatrix(NightVisionVpId, camera.nonJitteredProjectionMatrix * camera.worldToCameraMatrix);
        for (int i = 0; i < TubeAxisIds.Length; i++)
            binocular.SetVector(TubeAxisIds[i], -_lensNodes[i].forward);
        binocular.SetVector(TubeFieldId, new Vector4(Mathf.Cos((TubeField + TubeFieldFade) * Mathf.Deg2Rad), Mathf.Cos(TubeField * Mathf.Deg2Rad), 0f, 0f));

        ShadeMain(camera, !_deviceHidden);
        if (!_deviceHidden)
            return;

        // The headwear and the device as they truly stand from the camera, for the eyes (NightVisionPrecise.cs).
        PrepareRelative(camera);
        RenderEyes(camera, binocular, view, position);
        // Thermal goggles' picture, for the lenses (LensPicture).
        if (_device.Thermal && Active)
            RenderThermal(camera);
    }

    private static void RenderEyes(Camera camera, Material binocular, Transform view, Vector3 position)
    {
        Vector3 right = view.right, up = view.up, forward = view.forward;
        Vector3 placeA = FromCamera(_lensNodes[CentreLensA], position).GetColumn(3);
        Vector3 placeB = FromCamera(_lensNodes[CentreLensB], position).GetColumn(3);
        float a = Vector3.Dot(placeA, right);
        float b = Vector3.Dot(placeB, right);
        // The device's own left to right, across the view: the line from one centre lens to the other. Leaning, or the
        // balance turning the view about its line of sight (VestibularView's torsion), turns the view against the head;
        // the eyes, in the head, stay along the device, and the two views meet along the line square to it.
        Vector3 across = Vector3.ProjectOnPlane(a <= b ? placeB - placeA : placeA - placeB, forward);
        if (across.sqrMagnitude < 1e-8f)
            across = right;
        across.Normalize();
        // A monocular is before one eye only: that eye's view covers the whole frame, the further over the further it
        // moves to the eyepiece, and over the other eye's half the device fades out toward the edge, that eye seeing past it.
        int monocular = _device.Monocular && BinocularOn ? (a >= 0f ? 1 : 0) : -1;
        // The other eye's fade as an exponent: 0 none, 1 as the composite has it, up to 4 the fastest.
        float seeThrough = Mathf.Clamp01(MonocularSeeThrough);
        float fade = seeThrough <= 0.5f ? seeThrough * 2f : 1f + (seeThrough - 0.5f) * 6f;
        binocular.SetVector(MonocularId, new Vector4(monocular >= 0 ? 1f : 0f, monocular, fade, 0f));
        binocular.SetFloat(LensClearId, LensReflections ? 0f : 1f);
        _eyesFrame = Time.frameCount;
        // Each eye the set share of the way from the camera to its lens: the camera gone over to the aiming eye
        // (CameraPosition) is that eye, its eyepiece straight ahead in the middle of the frame, the other off to its
        // side. They stay back of the camera when it has come forward to a sight (NightVisionScope.cs).
        Vector3 back = view.forward * ScopePullback;
        Vector3 EyeShiftOf(Vector3 lens) => across * (Vector3.Dot(lens, across) * EyeShift * _split) - back;
        Vector3 leftShift = EyeShiftOf(a <= b ? placeA : placeB), rightShift = EyeShiftOf(a <= b ? placeB : placeA);
        Transform leftLens = _lensNodes[a <= b ? CentreLensA : CentreLensB], rightLens = _lensNodes[a <= b ? CentreLensB : CentreLensA];
        // The two views meet halfway between where each eye sees its own eyepiece, not at the frame's middle: with the
        // camera over at one eye, its eyepiece is in the middle and the other's off to the side.
        // The line passes there, square to the device's left to right as seen (x and y across the frame; a pixel as wide
        // as high). Its normal is given in the frame's 0..1 units (y over the aspect), so that across it the eased strip
        // is the same share of the frame's width (Overscan) at any slant. Each eye draws as far across as its side of the
        // strip reaches anywhere up the frame.
        Vector2 middle = monocular >= 0 ? new Vector2(0.5f, 0.5f)
            : 0.5f * (ScreenAt(camera, position, leftShift, leftLens) + ScreenAt(camera, position, rightShift, rightLens));
        middle.x = Mathf.Clamp(middle.x, 0.2f, 0.8f);
        Vector2 slant = new Vector2(Vector3.Dot(across, right), Vector3.Dot(across, up));
        if (monocular >= 0 || slant.x < 0.2f)
            slant = new Vector2(1f, 0f);
        slant.Normalize();
        float aspect = Mathf.Max(camera.aspect, 0.01f);
        Vector2 normal = new Vector2(slant.x, slant.y / aspect);
        float lean = slant.y / (aspect * slant.x);      // how far across the line moves per unit up the frame
        float top = middle.x - (1f - middle.y) * lean, bottom = middle.x + middle.y * lean;
        float reach = 0.5f * Overscan / slant.x;
        float leftSpan = monocular >= 0 ? 1f : HeldSpan(0, Mathf.Max(top, bottom) + reach);
        float rightSpan = monocular >= 0 ? 1f : HeldSpan(1, 1f - Mathf.Min(top, bottom) + reach);
        binocular.SetVector(SplitLineId, new Vector4(middle.x, middle.y, normal.x, normal.y));
        binocular.SetVector(EyeSpanId, new Vector4(leftSpan, rightSpan, 0f, 0f));
        for (int side = 0; side < 2; side++)
        {
            if (monocular >= 0 && side != monocular)
                continue;
            RenderEye(side, camera, position, side == 0 ? leftShift : rightShift, side == 0 ? leftLens : rightLens, side == 0 ? leftSpan : rightSpan);
        }
    }

    // How much of the frame's width an eye draws: what it needs, rounded up to a step and kept while it still covers
    // the need and is not much more, so that the split, which moves a little with every turn of the head against the
    // view, does not change the eye's targets' size (each change makes them anew) frame after frame.
    private const float SpanStep = 0.05f;
    private static readonly float[] _heldSpans = { 0f, 0f };
    private static float HeldSpan(int side, float needed)
    {
        float held = _heldSpans[side];
        if (needed > held || needed < held - 2f * SpanStep)
            held = _heldSpans[side] = Mathf.Min(1f, Mathf.Ceil(needed / SpanStep) * SpanStep);
        return held;
    }

    // Where in the frame (x 0 left to 1 right, y 0 bottom to 1 top) an eye standing at shift from the camera sees a
    // lens straight on.
    private static Vector2 ScreenAt(Camera camera, Vector3 cameraPlace, Vector3 shift, Transform lens)
    {
        Vector3 to = (Vector3)FromCamera(lens, cameraPlace).GetColumn(3) - shift;
        Transform view = camera.transform;
        float z = Vector3.Dot(to, view.forward);
        if (z <= 1e-4f)
            return new Vector2(0.5f, 0.5f);
        Matrix4x4 p = camera.projectionMatrix;
        return new Vector2(0.5f + 0.5f * p.m00 * Vector3.Dot(to, view.right) / z, 0.5f + 0.5f * p.m11 * Vector3.Dot(to, view.up) / z);
    }

    // Where a transform stands from the head along the world's axes, by the local offsets between them: up from each
    // to the nearest transform they share, and back down to the other. Null if they share none.
    private static Vector3? FromHead(Transform head, Transform target)
    {
        Transform? shared = target;
        while (shared != null && !head.IsChildOf(shared))
            shared = shared.parent;
        if (shared == null)
            return null;
        Matrix4x4 headInShared = Chain(shared, head), targetInShared = Chain(shared, target);
        Vector3 offset = targetInShared.GetColumn(3) - headInShared.GetColumn(3);
        // Along the shared transform's axes, turned (and scaled) to the world's.
        Matrix4x4 sharedTurn = shared.localToWorldMatrix;
        sharedTurn.SetColumn(3, new Vector4(0f, 0f, 0f, 1f));
        return sharedTurn.MultiplyVector(offset);
    }

    // A transform's place and turn within an ancestor's frame, from the local offsets between them.
    private static Matrix4x4 Chain(Transform ancestor, Transform t)
    {
        Matrix4x4 m = Matrix4x4.identity;
        for (Transform? at = t; at != null && at != ancestor; at = at.parent)
            m = Matrix4x4.TRS(at.localPosition, at.localRotation, at.localScale) * m;
        return m;
    }

    // cameraPlace: the main camera's; shift: this eye from it.
    private static void RenderEye(int side, Camera main, Vector3 cameraPlace, Vector3 shift, Transform centreLens, float span)
    {
        Vector3 position = cameraPlace + shift;
        // Half of the frame the game's SSAA stage puts it in, and a little past the middle (Overscan), where the
        // composite eases from one eye to the other; or all of it (a monocular's eye).
        float middle = span >= 1f ? 0f : side == 0 ? span - 1f : 1f - span;
        int width = Mathf.Max(Mathf.CeilToInt(Screen.width * span), 1), height = Mathf.Max(Screen.height, 1);
        RenderTexture view = Target(_eyeViews, side, width, height, 24, RenderTextureFormat.ARGBHalf, "Justachae NVG eye");
        RenderTexture lensMap = Target(_lensMaps, side, Mathf.Max((int)(width * LensMapScale), 8), Mathf.Max((int)(height * LensMapScale), 8), 16,
            RenderTextureFormat.ARGB32, "Justachae NVG lens map");

        Camera camera = _eyeCameras[side]!;

        // The main camera's view cut to this eye's part (normalised device x of that part rescaled to fill it),
        // with a near plane close enough for the eyecups.
        Matrix4x4 crop = Matrix4x4.identity;
        crop.m00 = 1f / span;
        crop.m03 = -middle / span;
        Matrix4x4 projection = crop * Matrix4x4.Perspective(main.fieldOfView, main.aspect, EyeNear, EyeFar);

        camera.CopyFrom(main);
        camera.transform.position = position;
        camera.rect = new Rect(0f, 0f, 1f, 1f);
        camera.nearClipPlane = EyeNear;
        camera.farClipPlane = EyeFar;
        camera.projectionMatrix = projection;
        camera.depthTextureMode |= DepthTextureMode.Depth;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Color.clear;
        // Each pixel's line of sight from this eye, by its view turned but not moved (as if at the world's origin):
        // with the eye's place in it, far out in a map, the inverse loses so much to rounding that the lines of
        // sight, and the lenses' picture with them, jump about from frame to frame.
        Matrix4x4 turned = camera.worldToCameraMatrix;
        turned.SetColumn(3, new Vector4(0f, 0f, 0f, 1f));
        Matrix4x4 inverse = (projection * turned).inverse;
        // Not moved from frame to frame: the main camera's own frame is not either (no anti-aliasing), and the eye's
        // shifting edges shimmered against it. The game's dithered glass still changes its pattern every frame and
        // is evened out over frames (Optics).

        // Which lens each pixel of this eye looks through, past the housing: the device's own meshes drawn as
        // blockers and each lens into its own channel, from this eye. Until the device is on, no light comes through.
        if (Active)
            DrawLensMap(lensMap, cameraPlace, shift, turned, crop * Matrix4x4.Perspective(main.fieldOfView, main.aspect, EyeNear, LensMapRange));
        else
            ClearLensMap(lensMap);

        camera.targetTexture = view;
        LightEye(camera);
        // The eyecups as they stand from this eye: by the device's true offsets where they are known.
        if (_precise)
            ShadeCamera(side, camera, projection, t => (Vector3)FromCamera(t, cameraPlace).GetColumn(3) - shift);
        else
            ShadeCamera(side, camera, projection, t => t.position - position);
        ShowDevice(true);
        if (!LensReflections)
            HideGlass();
        if (_precise)
            PutPieces(side, camera, shift, turned, projection);
        // Which of its pixels are the device's: only those go over the frame (NightVisionSelf).
        PutSelfMask(side, camera, width, height, shift, turned, projection);
        try
        {
            camera.Render();
        }
        finally
        {
            TakeSelfMask(side, camera);
            if (_precise)
                TakePieces(side, camera);
            ShowDevice(false);
            camera.ResetProjectionMatrix();
            camera.targetTexture = null;
        }

        Material binocular = _binocularMaterial!;
        binocular.SetFloat(OverscanId, Overscan);
        binocular.SetTexture(EyeIds[side], view);
        binocular.SetTexture(LensIds[side], lensMap);
        binocular.SetMatrix(EyeInvVpIds[side], inverse);
        // w: how strongly this eye sees the eyepieces' distortion, growing as it stands further back from them.
        float back = Mathf.InverseLerp(DistortionNear, DistortionFar, (centreLens.position - position).magnitude / centreLens.lossyScale.x);
        binocular.SetVector(EyePosIds[side], new Vector4(position.x, position.y, position.z, back));
    }

    // The eye camera's image: the device alone, solid where it was drawn (premultiplied, its coverage in alpha),
    // resolved over frames as the main camera resolves its own.
    internal static void Optics(int side, RenderTexture source, RenderTexture destination)
    {
        if (_eyeOptics == null)
        {
            Graphics.Blit(source, destination);
            return;
        }

        RenderTexture current = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGBHalf);
        _eyeOptics.SetTexture(LensMapId, _lensMaps[side] != null ? _lensMaps[side] : Texture2D.blackTexture);
        _eyeOptics.SetFloat(BehindLensId, BehindLens);
        _eyeOptics.SetTexture(SelfMaskId, SelfMask(side));
        Graphics.Blit(source, current, _eyeOptics, 0);
        RenderTexture history = Target(_eyeHistory, side, source.width, source.height, 0, RenderTextureFormat.ARGBHalf, "Justachae NVG eye history");
        bool continued = _historyFrame[side] == Time.frameCount - 1;
        _eyeOptics.SetTexture(HistoryId, history);
        _eyeOptics.SetFloat(HistoryWeightId, continued ? HistoryWeight : 0f);
        RenderTexture resolved = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGBHalf);
        Graphics.Blit(current, resolved, _eyeOptics, 1);
        Graphics.Blit(resolved, history);
        Graphics.Blit(resolved, destination);
        _historyFrame[side] = Time.frameCount;
        RenderTexture.ReleaseTemporary(current);
        RenderTexture.ReleaseTemporary(resolved);
    }

    // The game's ambient light, which it lays on through command buffers of each camera's own (the bulk of the light
    // in the dark): the eyes have them as the main camera has, or what they draw comes out near black.
    // Kept as the game makes and destroys them (it announces each), so they never have to be searched for.
    private static readonly System.Collections.Generic.List<AmbientLight> _ambientLights = new System.Collections.Generic.List<AmbientLight>();

    private static void TrackAmbientLights()
    {
        OnRenderObjectConnector.OnRenderObjectConnectorAdd += connector =>
        {
            if (connector is AmbientLight ambient && !_ambientLights.Contains(ambient))
                _ambientLights.Add(ambient);
        };
        OnRenderObjectConnector.OnRenderObjectConnectorRemove += connector =>
        {
            if (connector is AmbientLight ambient)
                _ambientLights.Remove(ambient);
        };
    }

    private static void LightEye(Camera camera)
    {
        foreach (AmbientLight ambient in _ambientLights)
        {
            if (ambient != null && ambient.isActiveAndEnabled)
                ambient.ManualOnRenderObject(camera);
        }
    }

    private static void ClearLensMap(RenderTexture target)
    {
        RenderTexture active = RenderTexture.active;
        RenderTexture.active = target;
        GL.Clear(true, true, Color.clear);
        RenderTexture.active = active;
    }

    // Which tube each pixel looks into: the device's own opaque meshes drawn black, then a card over each eyecup's
    // opening, just above its rim, on which each line of sight is followed to the lenses (NvgLensMask.shader).
    // Everything as it stands from the eye (the eye at zero, its view turned but not moved; see NightVisionPrecise).
    private static void DrawLensMap(RenderTexture target, Vector3 cameraPlace, Vector3 shift, Matrix4x4 view, Matrix4x4 projection)
    {
        _lensMapMaterial ??= new Material(_lensMaskShader) { name = "Justachae NVG lens map", hideFlags = HideFlags.DontSave };
        _card ??= CardMesh();
        float scale = _body!.lossyScale.x;
        Matrix4x4 fromEye = Matrix4x4.Translate(-shift);
        Vector3 eye = Vector3.zero;
        for (int i = 0; i < _lensNodes.Length; i++)
        {
            Matrix4x4 node = fromEye * FromCamera(_lensNodes[i], cameraPlace);
            Vector3 point = node.GetColumn(3), floor = node.MultiplyPoint3x4(_lensFloors[i]);
            _lensMapMaterial.SetVector(LensIds4[i], new Vector4(point.x, point.y, point.z, _lensFloors[i].w * scale));
            _lensMapMaterial.SetVector(LensFloorIds[i], floor);
            _lensMapMaterial.SetVector(AxisIds[i], _lensNodes[i].forward);
        }
        _lensMapMaterial.SetVector(OpeningId, new Vector4(_device.OpeningRadius, _device.RimRadius, _device.RimHeight, 0f) * scale);
        _lensMapMaterial.SetVector(EyeId, eye);
        // A thermal goggles' display lit evenly to its edge (the game's T-7 has no falloff); an image tube's dims at its rim.
        _lensMapMaterial.SetVector(OpticsId, new Vector4(EyeRelief * scale, ExitPupil * scale, ExitPupilEdge * scale, _device.Thermal ? 1f : RimLight));

        _lensMapBuffer ??= new CommandBuffer { name = "Justachae NVG lens map" };
        CommandBuffer buffer = _lensMapBuffer;
        buffer.Clear();
        buffer.SetRenderTarget(target);
        buffer.ClearRenderTarget(true, true, Color.clear);
        buffer.SetViewProjectionMatrices(view, projection);
        // Meshes at their transforms as they are now: a renderer hidden from the main camera keeps the pose it had
        // when last drawn, a frame behind the device.
        foreach (Renderer blocker in Blockers())
        {
            Mesh? mesh = blocker.GetComponent<MeshFilter>()?.sharedMesh;
            if (mesh == null)
                continue;
            for (int part = 0; part < mesh.subMeshCount; part++)
                buffer.DrawMesh(mesh, fromEye * FromCamera(blocker.transform, cameraPlace), _lensMapMaterial, part, 0);
        }
        for (int centre = CentreLensA; centre <= CentreLensB; centre += CentreLensB - CentreLensA)
        {
            Transform a = _lensNodes[centre], b = _lensNodes[centre + 1];
            Vector3 normal = (a.forward + b.forward).normalized;
            Vector3 pa = (fromEye * FromCamera(a, cameraPlace)).GetColumn(3), pb = (fromEye * FromCamera(b, cameraPlace)).GetColumn(3);
            Vector3 middle = (pa + pb) * 0.5f + normal * ((_device.RimHeight + _device.CardRaise + 0.001f) * scale);
            Vector2 size = _device.CardSize * scale;
            Matrix4x4 card = Matrix4x4.TRS(middle, Quaternion.LookRotation(normal, _body.forward), new Vector3(size.x, size.y, 1f));
            buffer.DrawMesh(_card, card, _lensMapMaterial, 0, 1);
        }
        Graphics.ExecuteCommandBuffer(buffer);
    }

    private static Mesh CardMesh()
    {
        Mesh mesh = new Mesh
        {
            name = "Justachae NVG lens card",
            vertices = new[] { new Vector3(-1f, -1f, 0f), new Vector3(1f, -1f, 0f), new Vector3(1f, 1f, 0f), new Vector3(-1f, 1f, 0f) },
            triangles = new[] { 0, 1, 2, 0, 2, 3 },
            hideFlags = HideFlags.DontSave,
        };
        mesh.RecalculateBounds();
        return mesh;
    }

    // The device with the mount and adapters it hangs from: all that is in the helmet's slot for it. Left in the main
    // camera's view they would come into the picture through the lenses, from where that camera is; and any of it may
    // stand between an eye and the lenses.
    private static Transform DeviceSlot()
    {
        Transform root = _body!.GetComponentInParent<EFT.Visual.NightVisionDevice>()?.transform
            ?? _body.GetComponentInParent<EFT.Visual.ThermalVisionDevice>()?.transform ?? _body.parent ?? _body;
        for (Transform? t = root.parent; t != null && t != t.root; t = t.parent)
        {
            if (t.name.StartsWith("mod_nvg", System.StringComparison.Ordinal))
                root = t;
        }
        return root;
    }

    // The device's own opaque meshes and its mount's, which stand between the eye and the lenses. The see-through ones let the light
    // by: the GPNVG-18 has a clear plate over each eyecup's two lenses.
    private static Renderer[] Blockers()
    {
        if (_blockers.Length > 0 && _blockersOf == _body && System.Array.TrueForAll(_blockers, r => r != null))
            return _blockers;

        Transform root = DeviceSlot();
        _blockers = System.Array.FindAll(root.GetComponentsInChildren<Renderer>(true), Opaque);
        _blockersOf = _body;
        return _blockers;
    }

    private static bool Opaque(Renderer renderer)
    {
        foreach (Material material in renderer.sharedMaterials)
        {
            if (material == null)
                continue;
            if (material.renderQueue > 2500 || material.name.IndexOf("glass", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
        }
        return true;
    }

    private static RenderTexture Target(RenderTexture?[] targets, int side, int width, int height, int depth, RenderTextureFormat format, string name)
    {
        RenderTexture? target = targets[side];
        if (target != null && target.width == width && target.height == height)
            return target;

        if (target != null)
        {
            target.Release();
            Object.Destroy(target);
        }
        target = new RenderTexture(width, height, depth, format) { name = name + (side == 0 ? " left" : " right") };
        targets[side] = target;
        return target;
    }

    private static void ReleaseEyes()
    {
        for (int i = 0; i < 2; i++)
        {
            Drop(_eyeViews, i);
            Drop(_eyeHistory, i);
            Drop(_lensMaps, i);
            Drop(_selfMasks, i);
            _heldSpans[i] = 0f;
        }
    }

    private static void Drop(RenderTexture?[] targets, int i)
    {
        if (targets[i] == null)
            return;
        targets[i]!.Release();
        Object.Destroy(targets[i]);
        targets[i] = null;
    }
}

internal sealed class EyeOptics : MonoBehaviour
{
    internal int Side;

    private void OnRenderImage(RenderTexture source, RenderTexture destination) => NightVisionEyepieces.Optics(Side, source, destination);
}
