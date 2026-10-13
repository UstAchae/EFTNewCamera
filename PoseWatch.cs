using Comfort.Common;
using EFT;
using UnityEngine;

namespace EFTNewCamera;

internal static class PoseWatch
{
    private const float Distance = 5f;
    private const float FieldOfView = 40f;

    private static Camera? _camera;
    private static RenderTexture? _texture;

    internal static bool IsCamera(Camera camera)
    {
        return _camera != null && camera == _camera;
    }

    internal static void LateUpdate()
    {
        if (!NewCameraSwitch.Enabled)
        {
            Release();
            return;
        }

        Player? player = Singleton<GameWorld>.Instance?.MainPlayer;
        if (player == null || !player.IsYourPlayer || player.MovementContext == null)
        {
            if (_camera != null)
                _camera.enabled = false;
            return;
        }

        Camera watch = Ensure();
        Vector3 forward = Quaternion.Euler(0f, player.MovementContext.Rotation.x, 0f) * Vector3.forward;
        Vector3 feet = player.Transform.position;
        Transform? head = player.PlayerBones?.Head?.Original;
        Vector3 focus = head != null ? (feet + head.position) * 0.5f : feet + Vector3.up;
        Vector3 position = focus + forward * Distance;
        watch.transform.SetPositionAndRotation(position, Quaternion.LookRotation(focus - position, Vector3.up));
        watch.enabled = true;
    }

    internal static void Draw()
    {
        if (!NewCameraSwitch.Enabled || _texture == null || _camera == null || !_camera.enabled)
            return;

        float width = 360f;
        float height = width * _texture.height / _texture.width;
        float margin = 12f;
        Rect picture = new Rect(margin, Screen.height - height - margin, width, height);
        Color previous = GUI.color;
        GUI.color = Color.black;
        GUI.DrawTexture(new Rect(picture.x - 2f, picture.y - 2f, picture.width + 4f, picture.height + 4f), Texture2D.whiteTexture);
        GUI.color = Color.white;
        GUI.DrawTexture(picture, _texture, ScaleMode.StretchToFill, false);
        GUI.color = previous;
    }

    internal static void Release()
    {
        if (_camera != null)
        {
            _camera.targetTexture = null;
            Object.Destroy(_camera.gameObject);
            _camera = null;
        }

        if (_texture != null)
        {
            _texture.Release();
            Object.Destroy(_texture);
            _texture = null;
        }
    }

    private static Camera Ensure()
    {
        if (_camera != null)
            return _camera;

        _texture = new RenderTexture(480, 270, 24, RenderTextureFormat.ARGB32)
        {
            name = "EFTNewCamera.PoseWatch",
            antiAliasing = 1
        };
        _texture.Create();

        GameObject host = new GameObject("EFTNewCamera.PoseWatch");
        host.hideFlags = HideFlags.HideAndDontSave;
        Object.DontDestroyOnLoad(host);
        Camera watch = host.AddComponent<Camera>();
        watch.enabled = false;
        watch.targetTexture = _texture;
        watch.clearFlags = CameraClearFlags.Skybox;
        watch.fieldOfView = FieldOfView;
        watch.nearClipPlane = 0.05f;
        watch.farClipPlane = 300f;
        watch.depth = 100f;
        watch.stereoTargetEye = StereoTargetEyeMask.None;
        watch.allowHDR = false;
        watch.allowMSAA = false;
        int ui = LayerMask.NameToLayer("UI");
        Camera? main = Camera.main;
        int mask = main != null ? main.cullingMask : ~0;
        if (ui >= 0)
            mask &= ~(1 << ui);
        watch.cullingMask = mask;
        AudioListener? listener = host.GetComponent<AudioListener>();
        if (listener != null)
            Object.Destroy(listener);
        _camera = watch;
        return watch;
    }
}
