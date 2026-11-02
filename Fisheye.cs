using Comfort.Common;
using EFT;
using EFT.CameraControl;
using HarmonyLib;
using UnityEngine;

namespace EFTNewCamera;

// A fisheye lens on the view. The FPS camera renders straight (rectilinear) with a field wide enough for the fisheye's
// corners, and an image effect of ours lays that frame out as the lens would, after the SSAA stage has upscaled it
// (Justachae/Fisheye). The horizontal field across the screen is the one set. Aimed, or with the eyepieces showing, it
// eases back to the game's own field and straight picture: the optic and the eyes are worked out for a straight view.
// The game's field is taken as it stands after its own frame (its zoom eases in coroutines, from the camera's field)
// and handed back at the end of the frame, so the game never sees ours.
internal static class Fisheye
{
    internal static bool Enabled;
    internal static float Field = 120f;   // degrees, across the screen
    internal static float Curve = 0.4f;    // 0 the straight picture at that field, 1 the stereographic fisheye
    internal static float Vignette = 0.6f; // 0 none, 1 the corners well into the lens barrel's dark
    private const float FadeSeconds = 0.25f;
    private const float MaxCornerDegrees = 85f;

    private static float _weight;          // 0 the straight picture, 1 the whole fisheye
    private static Camera? _widened;       // the camera given our field this frame
    private static float _gameField;       // its field as the game had it
    private static Vector4 _shape;         // k, f, the straight frame's half height, aspect (Justachae/Fisheye)
    private static Vector4 _shade;         // the image circle's radius and soft edge, the light's falloff
    private static Camera? _imageCamera;
    private static FisheyeImage? _image;
    private static Material? _material;
    private static bool _installed;
    private static readonly int ShapeId = Shader.PropertyToID("_Fisheye");
    private static readonly int ShadeId = Shader.PropertyToID("_FisheyeShade");

    internal static void Install()
    {
        if (_installed)
            return;
        _installed = true;
        Harmony harmony = new Harmony(NewCameraPlugin.PluginGuid + ".fisheye");
        System.Reflection.MethodInfo? awake = AccessTools.Method(typeof(BSG.CameraEffects.NightVision), "Awake");
        if (awake == null)
        {
            NewCameraPlugin.Log?.LogWarning("Not patched (fisheye): the FPS camera's night vision has no Awake.");
            return;
        }
        // After the eyepieces' own image effect, so the lens takes the frame with them in it.
        harmony.Patch(awake, postfix: new HarmonyMethod(AccessTools.Method(typeof(Fisheye), nameof(NightVisionAwakePostfix))) { priority = Priority.Low });
    }

    // The FPS camera (the one with SSAA) waking: the lens on it, off until wanted. Put on any later, it would come after
    // the SSAA stage hands the frame to the screen (see NightVisionComposite.cs).
    private static void NightVisionAwakePostfix(BSG.CameraEffects.NightVision __instance)
    {
        if (__instance.GetComponent<SSAA>() == null || __instance.GetComponent<FisheyeImage>() != null)
            return;
        __instance.gameObject.AddComponent<FisheyeImage>().enabled = false;
    }

    internal static void LateUpdate()
    {
        GiveBack();
        Player? player = Singleton<GameWorld>.Instance?.MainPlayer;
        Camera? camera = CameraManager.Instance?.Camera;
        bool ours = NewCameraSwitch.Enabled && player != null && player.IsYourPlayer && player.PointOfView == EPointOfView.ThirdPerson;
        bool wanted = ours && Enabled && !(player!.HandsController != null && player.HandsController.IsAiming)
            && !NightVisionEyepieces.EyesShown;
        _weight = ours ? Mathf.MoveTowards(_weight, wanted ? 1f : 0f, Time.unscaledDeltaTime / FadeSeconds) : 0f;
        FisheyeImage? image = Image(camera);
        float t = _weight * _weight * (3f - 2f * _weight);
        if (camera == null || image == null || t <= 0.0001f || Material() == null)
        {
            if (image != null && image.enabled)
                image.enabled = false;
            return;
        }

        // r = f tan(k theta) / k, in half screen heights: k = 1 is the game's straight view at its own field, k = 1/2 the
        // stereographic fisheye; the curve set picks between, at the field set. The less curved, the less the centre is
        // magnified and the more what is near the edges is stretched, as a body camera's wide lens has it.
        float aspect = camera.aspect;
        _gameField = camera.fieldOfView;
        float gameAcross = 2f * Mathf.Atan(Mathf.Tan(_gameField * 0.5f * Mathf.Deg2Rad) * aspect);
        float across = Mathf.Lerp(gameAcross, Field * Mathf.Deg2Rad, t);
        float k = 1f - 0.5f * Curve * t;
        float f = aspect * k / Mathf.Tan(k * across * 0.5f);
        // The straight frame reaches just as far as the fisheye's corners; its edges then reach past the fisheye's.
        float corner = Mathf.Sqrt(aspect * aspect + 1f);
        float cornerAngle = Mathf.Min(Mathf.Atan(k * corner / f) / k, MaxCornerDegrees * Mathf.Deg2Rad);
        float halfHeight = Mathf.Tan(cornerAngle) / corner;
        _shape = new Vector4(k, f, halfHeight, aspect);
        // The image circle shrinks from past the corners (none of it seen) to within them.
        float shade = Vignette * t;
        _shade = new Vector4(1.2f - 0.3f * shade, 0.15f, 0.35f * shade, 0f);
        camera.fieldOfView = 2f * Mathf.Atan(halfHeight) * Mathf.Rad2Deg;
        _widened = camera;
        if (!image.enabled)
            image.enabled = true;
    }

    // The game's field back on the camera, at the end of the frame (and before the next one is worked out).
    internal static void GiveBack()
    {
        if (_widened != null)
            _widened.fieldOfView = _gameField;
        _widened = null;
    }

    private static FisheyeImage? Image(Camera? camera)
    {
        if (!ReferenceEquals(camera, _imageCamera) || _image == null)
        {
            _imageCamera = camera;
            _image = camera != null ? camera.GetComponent<FisheyeImage>() : null;
        }
        return _image;
    }

    private static Material? Material()
    {
        if (_material == null && NightVisionEyepieces.FisheyeShader is Shader shader)
            _material = new Material(shader) { name = "Justachae fisheye", hideFlags = HideFlags.DontSave };
        return _material;
    }

    internal static void Render(Component camera, RenderTexture source, RenderTexture destination)
    {
        SSAAPropagator? propagator = camera.GetComponent<SSAAPropagator>();
        RenderTexture from = source, to = destination;
        bool staged = propagator != null && propagator.GetSourceDestination(out from, out to);
        if (!staged)
        {
            from = source;
            to = destination;
        }
        if (_material != null && ReferenceEquals(_widened, camera.GetComponent<Camera>()))
        {
            _material.SetVector(ShapeId, _shape);
            _material.SetVector(ShadeId, _shade);
            Graphics.Blit(from, to, _material);
        }
        else
            Graphics.Blit(from, to);
        if (staged)
            propagator!.ReleaseSourceDestination(from, to);
    }
}

// Ours, on the FPS camera: the fisheye lens over the frame.
internal sealed class FisheyeImage : MonoBehaviour
{
    private void OnRenderImage(RenderTexture source, RenderTexture destination) => Fisheye.Render(this, source, destination);
}
