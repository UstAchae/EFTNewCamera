using System;
using System.IO;
using System.Reflection;
using EFT;
using EFT.CameraControl;
using EFT.InventoryLogic;
using EFT.Visual;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace EFTNewCamera;

// The picture of the player's own head-mounted night vision, shown inside the device's eyepieces instead of over
// the whole screen. The tubes are 1x and afocal: the line from the eye through any point of an eyepiece sees the
// scene in that same direction. So the picture is the scene rendered from the camera, with an oblique near plane
// just past the device's front (the device, helmet and head drop out), cropped to where the eyepieces are on
// screen and run through an image-intensifier model; each lens shows it by the line's direction and goes dark
// beyond half its tube's field of view (the field stop). While it shows, the screen-wide filter (BRNVG's or the
// game's own) is skipped and the device stays drawn. Only the GPNVG-18 is measured so far.
//
// Binocular view: one camera cannot sit behind both eyecups, so while the eyepieces show, the camera splits in
// two where it is, like two eyes: each moved sideways to line up with its own eyecup's centre lens, each with the
// main camera's vertical field of view over half the screen's width. The screen is split down the middle, the
// left eye's view on the left half and the right eye's on the right, put in where the game's own night-vision
// filter would run (the SSAA stage, in HDR before tone mapping), so the main camera's later effects apply to both.
internal static class NightVisionEyepieces
{
    internal static bool Enabled;
    internal static float Brightness = 1f;
    internal static bool Binocular = true;

    internal static bool Active { get; private set; }

    private struct Tube
    {
        internal Vector3 Centre;   // lens centre on the eyepiece glass
        internal Vector3 Normal;   // toward the eye; the tube looks along its opposite
        internal int Neighbour;    // the other lens in the same eyecup
    }

    // GPNVG-18, in its LOD0 mesh's frame (metres), measured from the game's model: the eye side is +Y, the tubes
    // look along -Y, the mount is toward +Z. The centre tubes look straight ahead; the side tubes are turned 26.8
    // degrees outward along their whole length, so their eyepieces face the eye at that angle. Each eyecup holds
    // two lenses side by side 26.5 mm apart; the 15.5 mm clear radius leaves the "8" its waist.
    private const string GpnvgNode = "nvg_l3_gpnvg-18_anvis_LOD0";
    private static readonly Tube[] Gpnvg =
    {
        new Tube { Centre = new Vector3(0.0273f, 0.0395f, -0.0756f), Normal = new Vector3(0f, 1f, 0f), Neighbour = 1 },
        new Tube { Centre = new Vector3(0.0527f, 0.0469f, -0.0756f), Normal = new Vector3(-0.451f, 0.893f, 0f), Neighbour = 0 },
        new Tube { Centre = new Vector3(-0.0273f, 0.0395f, -0.0756f), Normal = new Vector3(0f, 1f, 0f), Neighbour = 3 },
        new Tube { Centre = new Vector3(-0.0527f, 0.0469f, -0.0756f), Normal = new Vector3(0.451f, 0.893f, 0f), Neighbour = 2 },
    };
    private const float ClearRadius = 0.0155f;
    private const float DiscRadius = 0.0175f;     // a dark ring around the clear lens, inside the eyecup
    private const float AboveGlass = 0.0003f;
    private const float HalfField = 20f;          // 40-degree tubes
    private const float Margin = 0.05f;
    private static readonly Color Phosphor = new Color(0.603f, 0.849f, 1f);   // white phosphor (BRNVG's GPNVG-18)
    private static readonly Vector4 Spectral = new Vector4(0.45f, 0.45f, 0.10f, 0f);
    private const float Glow = 0.02f;
    private const float TargetBrightness = 0.5f;
    private const float LowestGain = 1f, HighestGain = 1000f;
    private const float BrighterRate = 30f, DarkerRate = 2f;   // per second, as BRNVG's GPNVG-18
    private const int MeanWidth = 128, MeanHeight = 64, MeanMip = 7;
    private const int CentreLensA = 0, CentreLensB = 2;   // which is left is decided against the camera
    private const float MaxEyeDistance = 0.15f;   // the camera at the device, not a free camera elsewhere

    private static readonly int OnId = Shader.PropertyToID("_On");
    private static readonly int PictureId = Shader.PropertyToID("_Picture");
    private static readonly int PictureVpId = Shader.PropertyToID("_PictureVP");
    private static readonly int AxisId = Shader.PropertyToID("_Axis");
    private static readonly int LensId = Shader.PropertyToID("_Lens");
    private static readonly int OtherId = Shader.PropertyToID("_Other");
    private static readonly int GlowId = Shader.PropertyToID("_Glow");
    private static readonly int TubeGainId = Shader.PropertyToID("_TubeGain");
    private static readonly int TubeNoiseId = Shader.PropertyToID("_TubeNoise");
    private static readonly int TubeAdaptId = Shader.PropertyToID("_TubeAdapt");
    private static readonly int ExposureId = Shader.PropertyToID("_Exposure");
    private static readonly int PreviousId = Shader.PropertyToID("_Previous");
    private static readonly int TubeTintId = Shader.PropertyToID("_TubeTint");
    private static readonly int TubeSpectralId = Shader.PropertyToID("_TubeSpectral");
    private static readonly int[] EyeIds = { Shader.PropertyToID("_LeftEye"), Shader.PropertyToID("_RightEye") };
    private static readonly int SplitId = Shader.PropertyToID("_Split");
    private static readonly int CompositeId = Shader.PropertyToID("_JustachaeBinocularRT");

    private static bool _installed, _rendererPatched, _bundleTried;
    private static Shader? _lensShader, _tubeShader, _binocularShader;
    private static Material? _lensMaterial, _tubeMaterial, _binocularMaterial;
    private static Mesh? _disc;

    private static Transform? _body;
    private static GameObject? _lenses;
    private static Transform[] _lensNodes = Array.Empty<Transform>();
    private static float _frontY;
    private static int _searchedFrame = -100;

    private static Camera? _pictureCamera;
    private static RenderTexture? _raw, _picture, _mean;
    private static readonly RenderTexture?[] _exposure = new RenderTexture?[2];
    private static int _exposureIndex;
    private static Matrix4x4 _viewProjection;
    private static int _pictureFrame = -1;
    private static bool _pictureReady, _rendering;
    private static Behaviour? _hiddenMask;

    private static readonly Camera?[] _eyeCameras = new Camera?[2];   // left, right
    private static readonly RenderTexture?[] _eyeViews = new RenderTexture?[2];
    private static bool BinocularOn => Active && Binocular && _binocularMaterial != null;

    internal static void Install()
    {
        if (_installed)
            return;

        Harmony harmony = new Harmony(NewCameraPlugin.PluginGuid + ".eyepieces");
        MethodInfo? preCull = AccessTools.Method(typeof(BSG.CameraEffects.NightVision), "OnPreCull");
        MethodInfo? postfix = typeof(NightVisionEyepieces).GetMethod(nameof(NightVisionPreCullPostfix), BindingFlags.NonPublic | BindingFlags.Static);
        if (preCull != null && postfix != null)
            harmony.Patch(preCull, postfix: new HarmonyMethod(postfix) { priority = Priority.Last });

        _installed = true;
    }

    // The night-vision device worn on the player's head, if it is one with measured eyepieces.
    internal static bool Shows(GameObject model)
    {
        return Enabled && _body != null && _body.IsChildOf(model.transform);
    }

    internal static void Update(Player player)
    {
        if (!Enabled || !LoadShaders())
        {
            Release();
            return;
        }

        if (_body == null && Time.frameCount - _searchedFrame >= 30)
        {
            _searchedFrame = Time.frameCount;
            Attach(FindDevice(player));
        }

        CameraManager? cameras = BodyVisibility.Cameras;
        bool on = _body != null && _body.gameObject.activeInHierarchy && cameras != null && BodyVisibility.NightFilterVisible(cameras.NightVision);
        if (_lenses != null && _lenses.activeSelf != on)
            _lenses.SetActive(on);
        Active = on;
        EnsureRendererPatch();
        RestoreMask(cameras);
    }

    internal static void Release()
    {
        Active = false;
        RestoreMask(BodyVisibility.Cameras);
        if (_lenses != null)
            UnityEngine.Object.Destroy(_lenses);
        _lenses = null;
        _lensNodes = Array.Empty<Transform>();
        _body = null;
        _searchedFrame = -100;
        if (_raw != null)
        {
            _raw.Release();
            _picture!.Release();
            _raw = null;
            _picture = null;
        }
        if (_mean != null)
        {
            _mean.Release();
            _mean = null;
            for (int i = 0; i < 2; i++)
            {
                _exposure[i]!.Release();
                _exposure[i] = null;
            }
        }
        for (int i = 0; i < 2; i++)
        {
            if (_eyeViews[i] == null)
                continue;
            _eyeViews[i]!.Release();
            _eyeViews[i] = null;
        }
    }

    // After the camera is placed for the frame (NewCameraSwitch, and anything patched onto it, runs first): the
    // picture over the whole screen from where the camera is, then each eye from its own place.
    internal static void OnCameraPreCull(Camera camera)
    {
        if (!BinocularOn || _rendering || _body == null || _lensNodes.Length == 0 || camera != Camera.main)
            return;

        Material binocular = _binocularMaterial!;
        Transform view = camera.transform;
        Vector3 position = view.position;
        if ((_lensNodes[0].position - position).sqrMagnitude > MaxEyeDistance * MaxEyeDistance)
        {
            binocular.SetVector(SplitId, Vector4.zero);   // the main camera's own frame
            return;
        }

        Vector3 right = view.right;
        float a = Vector3.Dot(_lensNodes[CentreLensA].position - position, right);
        float b = Vector3.Dot(_lensNodes[CentreLensB].position - position, right);

        _pictureFrame = Time.frameCount;
        _pictureReady = RenderPicture(camera, true);
        RenderEye(0, camera, position + right * Mathf.Min(a, b));
        RenderEye(1, camera, position + right * Mathf.Max(a, b));
        binocular.SetVector(SplitId, new Vector4(1f, 0f, 0f, 0f));
    }

    private static void RenderEye(int side, Camera main, Vector3 position)
    {
        // Half of the frame the game's SSAA stage puts it in.
        int width = Mathf.Max(Screen.width / 2, 1), height = Mathf.Max(Screen.height, 1);
        RenderTexture? target = _eyeViews[side];
        if (target == null || target.width != width || target.height != height)
        {
            if (target != null)
                target.Release();
            target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGBHalf) { name = side == 0 ? "Justachae NVG left eye" : "Justachae NVG right eye" };
            _eyeViews[side] = target;
        }

        Camera? camera = _eyeCameras[side];
        if (camera == null)
        {
            GameObject go = new GameObject(side == 0 ? "Justachae NVG left eye" : "Justachae NVG right eye");
            UnityEngine.Object.DontDestroyOnLoad(go);
            camera = go.AddComponent<Camera>();
            camera.enabled = false;
            _eyeCameras[side] = camera;
        }

        camera.CopyFrom(main);
        camera.transform.position = position;
        camera.targetTexture = target;
        camera.rect = new Rect(0f, 0f, 1f, 1f);
        // The main camera's vertical field of view over half the screen's width.
        camera.projectionMatrix = Matrix4x4.Perspective(main.fieldOfView, (float)width / height, main.nearClipPlane, main.farClipPlane);
        try
        {
            camera.Render();
        }
        finally
        {
            camera.ResetProjectionMatrix();
            camera.targetTexture = null;
        }

        _binocularMaterial!.SetTexture(EyeIds[side], target);
    }

    private static Transform? FindDevice(Player player)
    {
        PlayerBody? body = player.PlayerBody;
        if (body == null)
            return null;

        foreach (EquipmentSlot slot in new[] { EquipmentSlot.Headwear, EquipmentSlot.FaceCover, EquipmentSlot.Eyewear })
        {
            if (!body.SlotViews.TryGetByKey(slot, out PlayerBody.SlotView? view) || view?.Model == null)
                continue;

            foreach (NightVisionDevice device in view.Model.GetComponentsInChildren<NightVisionDevice>(true))
            {
                foreach (Transform node in device.GetComponentsInChildren<Transform>(true))
                {
                    if (node.name == GpnvgNode)
                        return node;
                }
            }
        }

        return null;
    }

    private static void Attach(Transform? body)
    {
        if (body == null || _lensMaterial == null)
            return;

        _body = body;
        Mesh? mesh = body.GetComponent<MeshFilter>()?.sharedMesh;
        _frontY = (mesh != null ? mesh.bounds.min.y : -0.074f) - 0.002f;
        _disc ??= DiscMesh();
        _lenses = new GameObject("Justachae NVG eyepieces");
        _lenses.layer = body.gameObject.layer;
        _lenses.transform.SetParent(body, false);
        _lensNodes = new Transform[Gpnvg.Length];
        for (int i = 0; i < Gpnvg.Length; i++)
        {
            Tube tube = Gpnvg[i];
            GameObject lens = new GameObject("lens " + i) { layer = body.gameObject.layer };
            lens.transform.SetParent(_lenses.transform, false);
            Vector3 normal = tube.Normal.normalized;
            lens.transform.localPosition = tube.Centre + normal * AboveGlass;
            lens.transform.localRotation = Quaternion.LookRotation(normal, Vector3.forward);
            lens.AddComponent<MeshFilter>().sharedMesh = _disc;
            MeshRenderer renderer = lens.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = _lensMaterial;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            lens.AddComponent<EyepieceLens>().Index = i;
            _lensNodes[i] = lens.transform;
        }
        _lenses.SetActive(false);
    }

    private static Mesh DiscMesh()
    {
        const int segments = 48;
        Vector3[] vertices = new Vector3[segments + 1];
        int[] triangles = new int[segments * 3];
        for (int i = 0; i < segments; i++)
        {
            float a = i * Mathf.PI * 2f / segments;
            vertices[i + 1] = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f) * DiscRadius;
            triangles[i * 3] = 0;
            triangles[i * 3 + 1] = i + 1;
            triangles[i * 3 + 2] = (i + 1) % segments + 1;
        }

        Mesh mesh = new Mesh { name = "Justachae NVG eyepiece", vertices = vertices, triangles = triangles, hideFlags = HideFlags.DontSave };
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    private static bool LoadShaders()
    {
        if (_lensMaterial != null && _tubeMaterial != null)
            return true;
        if (_bundleTried)
            return false;

        _bundleTried = true;
        string path = Path.Combine(Path.GetDirectoryName(typeof(NightVisionEyepieces).Assembly.Location) ?? string.Empty, "nightvision.bundle");
        AssetBundle? bundle = File.Exists(path) ? AssetBundle.LoadFromFile(path) : null;
        if (bundle == null)
            return false;

        foreach (Shader shader in bundle.LoadAllAssets<Shader>())
        {
            if (shader.name == "Justachae/NVG/Eyepiece")
                _lensShader = shader;
            else if (shader.name == "Justachae/NVG/Tube")
                _tubeShader = shader;
            else if (shader.name == "Justachae/NVG/Binocular")
                _binocularShader = shader;
        }

        if (_lensShader == null || _tubeShader == null)
            return false;
        _lensMaterial = new Material(_lensShader) { name = "Justachae NVG eyepiece", hideFlags = HideFlags.DontSave };
        _tubeMaterial = new Material(_tubeShader) { name = "Justachae NVG tube", hideFlags = HideFlags.DontSave };
        if (_binocularShader != null)
            _binocularMaterial = new Material(_binocularShader) { name = "Justachae NVG binocular", hideFlags = HideFlags.DontSave };
        return true;
    }

    internal static void WillRender(EyepieceLens lens, Renderer renderer, MaterialPropertyBlock block)
    {
        Camera eye = Camera.current;
        if (_rendering || eye == null || _body == null)
            return;

        bool on = eye == Camera.main || eye == _eyeCameras[0] || eye == _eyeCameras[1];
        if (on && _pictureFrame != Time.frameCount)
        {
            _pictureFrame = Time.frameCount;
            _pictureReady = RenderPicture(eye, false);
        }

        on &= _pictureReady;
        renderer.GetPropertyBlock(block);
        block.SetFloat(OnId, on ? 1f : 0f);
        Tube tube = Gpnvg[lens.Index];
        Transform node = _lensNodes[lens.Index];
        Transform other = _lensNodes[tube.Neighbour];
        Vector3 axis = -node.forward;
        block.SetVector(AxisId, new Vector4(axis.x, axis.y, axis.z, HalfField));
        Vector3 centre = node.position;
        block.SetVector(LensId, new Vector4(centre.x, centre.y, centre.z, ClearRadius * node.lossyScale.x));
        Vector3 neighbour = other.position;
        block.SetVector(OtherId, new Vector4(neighbour.x, neighbour.y, neighbour.z, 1f));
        block.SetColor(GlowId, Phosphor * Glow);
        if (on)
        {
            block.SetTexture(PictureId, _picture);
            block.SetMatrix(PictureVpId, _viewProjection);
        }
        renderer.SetPropertyBlock(block);
    }

    // wholeScreen: for both eyes, whose lines through the lenses span more than one eye's view of them.
    private static bool RenderPicture(Camera eye, bool wholeScreen)
    {
        if (_body == null || _tubeMaterial == null)
            return false;

        // Only from behind the device, looking through it.
        Vector3 forward = -_body.up;
        Vector3 front = _body.TransformPoint(new Vector3(0f, _frontY, 0f));
        if (Vector3.Dot(eye.transform.position - front, forward) >= 0f)
            return false;

        // The lenses' area on screen, from points around each rim, with a margin.
        Vector2 lo = new Vector2(1f, 1f), hi = new Vector2(0f, 0f);
        bool behindEye = false;
        foreach (Transform node in _lensNodes)
        {
            float radius = ClearRadius * node.lossyScale.x;
            for (int i = 0; i < 12; i++)
            {
                float a = i * Mathf.PI / 6f;
                Vector3 v = eye.WorldToViewportPoint(node.position + node.rotation * new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f) * radius);
                if (v.z <= 0f)
                {
                    behindEye = true;
                    continue;
                }
                lo = Vector2.Min(lo, v);
                hi = Vector2.Max(hi, v);
            }
        }
        if (behindEye || wholeScreen)
        {
            lo = Vector2.zero;
            hi = Vector2.one;
        }
        Vector2 pad = (hi - lo) * Margin;
        lo = Vector2.Max(lo - pad, Vector2.zero);
        hi = Vector2.Min(hi + pad, Vector2.one);
        Vector2 size = hi - lo;
        if (size.x <= 0f || size.y <= 0f)
            return false;

        // About as many pixels as the area has on screen. The area moves on screen whenever the camera turns
        // against the head, so the picture is only reallocated when it is too small or far too big.
        int width = Mathf.Clamp(Mathf.CeilToInt(size.x * eye.pixelWidth), 64, 4096);
        int height = Mathf.Clamp(Mathf.CeilToInt(size.y * eye.pixelHeight), 64, 4096);
        if (_raw == null || _raw.width < width || _raw.height < height || _raw.width > width * 1.6f || _raw.height > height * 1.6f)
        {
            if (_raw != null)
            {
                _raw.Release();
                _picture!.Release();
            }
            width = Mathf.Min(Mathf.CeilToInt(width * 1.2f / 64f) * 64, 4096);
            height = Mathf.Min(Mathf.CeilToInt(height * 1.2f / 64f) * 64, 4096);
            _raw = new RenderTexture(width, height, 24, RenderTextureFormat.ARGBHalf) { name = "Justachae NVG scene" };
            _picture = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32) { name = "Justachae NVG picture" };
        }
        width = _raw.width;
        height = _raw.height;
        if (_mean == null)
        {
            // A small mipmapped copy of the scene for its mean brightness, and the adapted mean, two 1x1 in turn.
            _mean = new RenderTexture(MeanWidth, MeanHeight, 0, RenderTextureFormat.ARGBHalf) { name = "Justachae NVG mean", useMipMap = true, autoGenerateMips = false };
            RenderTexture active = RenderTexture.active;
            for (int i = 0; i < 2; i++)
            {
                // Cleared, so the first frame takes the scene's mean as it is.
                _exposure[i] = new RenderTexture(1, 1, 0, RenderTextureFormat.RFloat) { name = "Justachae NVG exposure " + i };
                RenderTexture.active = _exposure[i];
                GL.Clear(false, true, Color.clear);
            }
            RenderTexture.active = active;
        }

        if (_pictureCamera == null)
        {
            GameObject go = new GameObject("Justachae NVG picture camera");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _pictureCamera = go.AddComponent<Camera>();
            _pictureCamera.enabled = false;
        }

        // The eye's own projection cropped to that area: normalised device x and y rescaled so the area fills it.
        Matrix4x4 projection = eye.nonJitteredProjectionMatrix;
        float cx = lo.x + hi.x - 1f, cy = lo.y + hi.y - 1f;
        Matrix4x4 crop = Matrix4x4.identity;
        crop.m00 = 1f / size.x;
        crop.m03 = -cx / size.x;
        crop.m11 = 1f / size.y;
        crop.m13 = -cy / size.y;
        projection = crop * projection;

        Camera camera = _pictureCamera;
        camera.CopyFrom(eye);
        camera.targetTexture = _raw;
        camera.rect = new Rect(0f, 0f, 1f, 1f);
        camera.projectionMatrix = projection;
        Vector4 plane = new Vector4(forward.x, forward.y, forward.z, -Vector3.Dot(forward, front));
        camera.projectionMatrix = camera.CalculateObliqueMatrix(camera.worldToCameraMatrix.inverse.transpose * plane);
        _viewProjection = projection * camera.worldToCameraMatrix;

        // No shadows in the picture: their maps are redrawn for every camera and were most of its cost.
        float shadowDistance = QualitySettings.shadowDistance;
        QualitySettings.shadowDistance = 0f;
        _rendering = true;
        try
        {
            camera.Render();
        }
        finally
        {
            _rendering = false;
            QualitySettings.shadowDistance = shadowDistance;
            camera.ResetProjectionMatrix();
            camera.targetTexture = null;
        }

        _tubeMaterial.SetVector(TubeGainId, new Vector4(TargetBrightness * Brightness, LowestGain, HighestGain, MeanMip));
        _tubeMaterial.SetColor(TubeTintId, Phosphor);
        _tubeMaterial.SetVector(TubeSpectralId, Spectral);
        Graphics.Blit(_raw, _mean);
        _mean!.GenerateMips();
        _tubeMaterial.SetTexture(PreviousId, _exposure[_exposureIndex]);
        _tubeMaterial.SetVector(TubeAdaptId, new Vector4(Time.unscaledDeltaTime, BrighterRate, DarkerRate, 0f));
        _exposureIndex ^= 1;
        Graphics.Blit(_mean, _exposure[_exposureIndex], _tubeMaterial, 1);

        float grain = width / Mathf.Max(size.x * eye.pixelWidth, 1f);
        _tubeMaterial.SetVector(TubeNoiseId, new Vector4(1f, grain, Time.time, 0f));
        _tubeMaterial.SetTexture(ExposureId, _exposure[_exposureIndex]);
        Graphics.Blit(_raw, _picture, _tubeMaterial, 0);
        return true;
    }

    // While the eyepieces show the picture, the game's own filter adds nothing to the screen; in its place the two
    // eyes' views are joined, on the main camera through the SSAA stage, otherwise by the filter's command buffer.
    private static void NightVisionPreCullPostfix(BSG.CameraEffects.NightVision __instance)
    {
        if (!Active)
            return;

        Material? binocular = BinocularOn ? _binocularMaterial : null;
        if (AccessTools.Field(typeof(BSG.CameraEffects.NightVision), "_nightVisionCB")?.GetValue(__instance) is CommandBuffer buffer)
        {
            buffer.Clear();
            if (binocular != null)
            {
                buffer.GetTemporaryRT(CompositeId, -1, -1, 0, FilterMode.Bilinear, RenderTextureFormat.DefaultHDR);
                buffer.Blit(BuiltinRenderTextureType.CameraTarget, CompositeId);
                buffer.Blit(CompositeId, BuiltinRenderTextureType.CameraTarget, binocular);
                buffer.ReleaseTemporaryRT(CompositeId);
            }
        }
        if (AccessTools.Field(typeof(BSG.CameraEffects.NightVision), "_ssaaPropagator")?.GetValue(__instance) is SSAAPropagator propagator && propagator != null)
            propagator.SetNightVisionMaterial(binocular);
        if (__instance.TextureMask != null && __instance.TextureMask.enabled)
        {
            __instance.TextureMask.enabled = false;
            _hiddenMask = __instance.TextureMask;
        }
    }

    // The game turns its mask on with the filter; BRNVG keeps it off itself.
    private static void RestoreMask(CameraManager? cameras)
    {
        if (Active || _hiddenMask == null)
            return;

        if (cameras?.NightVision != null && cameras.NightVision.On && !BodyVisibility.UsesNightPipeline(cameras.NightVision))
            _hiddenMask.enabled = true;
        _hiddenMask = null;
    }

    // BRNVG draws its picture in OnRenderImage on the main camera; while the eyepieces show, it passes the frame on.
    private static void EnsureRendererPatch()
    {
        if (_rendererPatched || !Active)
            return;

        _rendererPatched = true;
        Type? renderer = BodyVisibility.NightRendererType;
        MethodInfo? render = renderer == null ? null : AccessTools.Method(renderer, "OnRenderImage");
        MethodInfo? prefix = typeof(NightVisionEyepieces).GetMethod(nameof(RenderImagePrefix), BindingFlags.NonPublic | BindingFlags.Static);
        if (render != null && prefix != null)
            new Harmony(NewCameraPlugin.PluginGuid + ".eyepieces.brnvg").Patch(render, prefix: new HarmonyMethod(prefix));
    }

    private static bool RenderImagePrefix(RenderTexture source, RenderTexture destination)
    {
        if (!Active)
            return true;

        Graphics.Blit(source, destination);
        return false;
    }
}

internal sealed class EyepieceLens : MonoBehaviour
{
    internal int Index;

    private Renderer? _renderer;
    private MaterialPropertyBlock? _block;

    private void OnWillRenderObject()
    {
        _renderer ??= GetComponent<Renderer>();
        _block ??= new MaterialPropertyBlock();
        NightVisionEyepieces.WillRender(this, _renderer, _block);
    }
}
