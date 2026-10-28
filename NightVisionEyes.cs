using UnityEngine;
using UnityEngine.Rendering;

namespace EFTNewCamera;

// The binocular view's two eyes (see NightVisionEyepieces): each a camera at its own place rendering the whole
// scene over its half of the main camera's view, then the eye's optics on that image (NvgEyeOptics.shader): the
// near foreground defocused by the pupil, and the sharp picture through the lenses as the eye sees them past the
// housing, from a lens map rendered from the same place and blurred by the lenses' distance.
internal static partial class NightVisionEyepieces
{
    private const float EyeNear = 0.01f;          // the eyecups are a couple of centimetres away
    private const float ForegroundLimit = 0.3f;   // nearer than this the eye's defocus is worth drawing
    private const float LargestDefocus = 0.09f;   // radius, as a share of the image height
    private const float LensMapScale = 0.25f;
    private const float LensMapRange = 1f;

    private static readonly int LensIndexId = Shader.PropertyToID("_LensIndex");
    private static readonly int[] EyeIds = { Shader.PropertyToID("_LeftEye"), Shader.PropertyToID("_RightEye") };
    private static readonly int NearId = Shader.PropertyToID("_Near");
    private static readonly int JitterId = Shader.PropertyToID("_Jitter");
    private static readonly int OutSizeId = Shader.PropertyToID("_OutSize");
    private static readonly int MaskBlurId = Shader.PropertyToID("_MaskBlur");
    private static readonly int ForegroundId = Shader.PropertyToID("_Foreground");
    private static readonly int LensMapId = Shader.PropertyToID("_LensMap");
    private static readonly int EyeInvVpId = Shader.PropertyToID("_EyeInvVP");
    private static readonly int EyePosId = Shader.PropertyToID("_EyePos");
    private static readonly int FieldSoftId = Shader.PropertyToID("_FieldSoft");

    private static Material? _eyeOptics;
    private static readonly Camera?[] _eyeCameras = new Camera?[2];   // left, right
    private static readonly RenderTexture?[] _eyeViews = new RenderTexture?[2];
    private static readonly RenderTexture?[] _lensMaps = new RenderTexture?[2];
    private static Camera? _lensMapCamera;
    private static readonly Vector4[] _lensNormals = new Vector4[4];
    private static int _eyesFrame = -100;

    private static bool EyesDrawn => Time.frameCount - _eyesFrame <= 1;

    // After the camera is placed for the frame (NewCameraSwitch, and anything patched onto it, runs first): the
    // picture over the whole screen from where the camera is, then each eye.
    internal static void OnCameraPreCull(Camera camera)
    {
        if (!BinocularOn || _rendering || _body == null || _lensNodes.Length == 0 || _eyeOptics == null || camera != Camera.main)
            return;

        Material binocular = _binocularMaterial!;
        Transform view = camera.transform;
        Vector3 position = view.position;
        if ((_lensNodes[0].position - position).sqrMagnitude > MaxEyeDistance * MaxEyeDistance)
        {
            binocular.SetVector(SplitId, Vector4.zero);   // the main camera's own frame
            return;
        }

        _pictureFrame = Time.frameCount;
        _pictureReady = RenderPicture(camera, true);
        if (!_pictureReady)
        {
            binocular.SetVector(SplitId, Vector4.zero);
            return;
        }

        for (int i = 0; i < _lensNodes.Length; i++)
        {
            Vector3 normal = _lensNodes[i].forward;
            _lensNormals[i] = new Vector4(normal.x, normal.y, normal.z, HalfField);
        }

        Vector3 right = view.right;
        float a = Vector3.Dot(_lensNodes[CentreLensA].position - position, right);
        float b = Vector3.Dot(_lensNodes[CentreLensB].position - position, right);
        _eyesFrame = Time.frameCount;
        for (int side = 0; side < 2; side++)
        {
            bool aIsThisSide = side == 0 ? a <= b : a > b;
            Vector3 eye = position + right * ((aIsThisSide ? a : b) * EyeShift);
            RenderEye(side, camera, eye, _lensNodes[aIsThisSide ? CentreLensA : CentreLensB]);
        }
        binocular.SetVector(SplitId, new Vector4(1f, 0f, 0f, 0f));
    }

    private static void RenderEye(int side, Camera main, Vector3 position, Transform centreLens)
    {
        // Half of the frame the game's SSAA stage puts it in.
        int width = Mathf.Max(Screen.width / 2, 1), height = Mathf.Max(Screen.height, 1);
        RenderTexture view = Target(_eyeViews, side, width, height, 24, RenderTextureFormat.ARGBHalf, "Justachae NVG eye");
        RenderTexture lensMap = Target(_lensMaps, side, Mathf.Max((int)(width * LensMapScale), 8), Mathf.Max((int)(height * LensMapScale), 8), 16,
            RenderTextureFormat.ARGB32, "Justachae NVG lens map");

        Camera? camera = _eyeCameras[side];
        if (camera == null)
        {
            GameObject go = new GameObject(side == 0 ? "Justachae NVG left eye" : "Justachae NVG right eye");
            Object.DontDestroyOnLoad(go);
            camera = go.AddComponent<Camera>();
            camera.enabled = false;
            go.AddComponent<EyeOptics>();
            _eyeCameras[side] = camera;
        }

        // The main camera's view cut to this eye's half (normalised device x of that half rescaled to fill it),
        // with a near plane close enough for the eyecups.
        Matrix4x4 crop = Matrix4x4.identity;
        crop.m00 = 2f;
        crop.m03 = side == 0 ? 1f : -1f;
        Matrix4x4 projection = crop * Matrix4x4.Perspective(main.fieldOfView, main.aspect, EyeNear, main.farClipPlane);

        camera.CopyFrom(main);
        camera.transform.position = position;
        camera.rect = new Rect(0f, 0f, 1f, 1f);
        camera.nearClipPlane = EyeNear;
        camera.projectionMatrix = projection;
        camera.depthTextureMode |= DepthTextureMode.Depth;
        Matrix4x4 inverse = (projection * camera.worldToCameraMatrix).inverse;
        float pixelsPerRadian = height / (2f * Mathf.Tan(main.fieldOfView * 0.5f * Mathf.Deg2Rad));

        // Which lens each pixel of this eye looks through, past the housing.
        Camera map = LensMapCamera();
        map.CopyFrom(camera);
        map.targetTexture = lensMap;
        map.clearFlags = CameraClearFlags.SolidColor;
        map.backgroundColor = Color.clear;
        map.cullingMask = 1 << _body!.gameObject.layer;
        map.farClipPlane = LensMapRange;
        map.projectionMatrix = crop * Matrix4x4.Perspective(main.fieldOfView, main.aspect, EyeNear, LensMapRange);
        map.depthTextureMode = DepthTextureMode.None;
        map.RenderWithShader(_lensMaskShader, "RenderType");
        map.targetTexture = null;

        // The lenses' defocus: the pupil over their distance.
        float lensDistance = Mathf.Max((centreLens.position - position).magnitude, EyeNear);
        float lensRadius = Pupil / lensDistance * pixelsPerRadian * 0.5f * LensMapScale;
        RenderTexture blurred = RenderTexture.GetTemporary(lensMap.width, lensMap.height, 0, RenderTextureFormat.ARGB32);
        _eyeOptics!.SetVector(MaskBlurId, new Vector4(lensRadius, 0f, 0f, 0f));
        _eyeOptics.SetVector(JitterId, new Vector4(Time.frameCount % 64, 0f, 0f, 0f));
        Graphics.Blit(lensMap, blurred, _eyeOptics, 2);
        Graphics.Blit(blurred, lensMap);
        RenderTexture.ReleaseTemporary(blurred);

        // Values the eye's optics read when its camera finishes (EyeOptics.OnRenderImage).
        _eyeOptics.SetVector(NearId, new Vector4(ForegroundLimit, Pupil * pixelsPerRadian * 0.25f, LargestDefocus * height * 0.5f, LargestDefocus * height * 0.5f));
        _eyeOptics.SetTexture(LensMapId, lensMap);
        _eyeOptics.SetMatrix(EyeInvVpId, inverse);
        _eyeOptics.SetVector(EyePosId, position);
        _eyeOptics.SetVectorArray(LensNormalId, _lensNormals);
        _eyeOptics.SetVector(FieldSoftId, new Vector4(FieldStopSoftness, 0f, 0f, 0f));
        _eyeOptics.SetColor(GlowId, Glow);
        _eyeOptics.SetTexture(PictureId, _picture);
        _eyeOptics.SetMatrix(PictureVpId, _viewProjection);

        camera.targetTexture = view;
        try
        {
            camera.Render();
        }
        finally
        {
            camera.ResetProjectionMatrix();
            camera.targetTexture = null;
        }

        _binocularMaterial!.SetTexture(EyeIds[side], view);
    }

    // The eye camera's image through the eye's optics.
    internal static void Optics(RenderTexture source, RenderTexture destination)
    {
        if (_eyeOptics == null)
        {
            Graphics.Blit(source, destination);
            return;
        }

        int width = Mathf.Max(source.width / 2, 1), height = Mathf.Max(source.height / 2, 1);
        RenderTexture gathered = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGBHalf);
        RenderTexture smoothed = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGBHalf);
        _eyeOptics.SetVector(OutSizeId, new Vector4(width, height, 0f, 0f));
        Graphics.Blit(source, gathered, _eyeOptics, 0);
        Graphics.Blit(gathered, smoothed, _eyeOptics, 1);
        _eyeOptics.SetTexture(ForegroundId, smoothed);
        Graphics.Blit(source, destination, _eyeOptics, 3);
        RenderTexture.ReleaseTemporary(gathered);
        RenderTexture.ReleaseTemporary(smoothed);
    }

    private static Camera LensMapCamera()
    {
        if (_lensMapCamera == null)
        {
            GameObject go = new GameObject("Justachae NVG lens map");
            Object.DontDestroyOnLoad(go);
            _lensMapCamera = go.AddComponent<Camera>();
            _lensMapCamera.enabled = false;
        }
        return _lensMapCamera;
    }

    private static RenderTexture Target(RenderTexture?[] targets, int side, int width, int height, int depth, RenderTextureFormat format, string name)
    {
        RenderTexture? target = targets[side];
        if (target != null && target.width == width && target.height == height)
            return target;

        if (target != null)
            target.Release();
        target = new RenderTexture(width, height, depth, format) { name = name + (side == 0 ? " left" : " right") };
        targets[side] = target;
        return target;
    }

    private static void ReleaseEyes()
    {
        for (int i = 0; i < 2; i++)
        {
            _eyeViews[i]?.Release();
            _eyeViews[i] = null;
            _lensMaps[i]?.Release();
            _lensMaps[i] = null;
        }
    }
}

internal sealed class EyeOptics : MonoBehaviour
{
    private void OnRenderImage(RenderTexture source, RenderTexture destination) => NightVisionEyepieces.Optics(source, destination);
}
