using BepInEx;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using UnityEngine;

namespace EFTNewCamera;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class NewCameraPlugin : BaseUnityPlugin
{
    public const string PluginGuid = "custom.eft.newcamera";
    public const string PluginName = "新镜头";
    public const string PluginVersion = "0.1.2";

    private void Awake()
    {
        ConfigEntry<bool> enabled = Config.Bind("新镜头", "总开关", false,
            "打开后使用第三人称骨架，并把游戏镜头挂在人物头部。");
        NewCameraSwitch.Enabled = enabled.Value;
        enabled.SettingChanged += (_, _) => NewCameraSwitch.Enabled = enabled.Value;
        BindDirection();
        BindPosition();
    }

    private void BindDirection()
    {
        ConfigEntry<float> pitch = BindDegrees("镜头朝向", "俯仰", "头骨坐标到镜头坐标的俯仰对齐，单位为度。");
        ConfigEntry<float> yaw = BindDegrees("镜头朝向", "偏航", "头骨坐标到镜头坐标的偏航对齐，单位为度。");
        ConfigEntry<float> roll = BindDegrees("镜头朝向", "翻滚", "头骨坐标到镜头坐标的翻滚对齐，单位为度。");
        void Refresh()
        {
            CameraDirection.Pitch = pitch.Value;
            CameraDirection.Yaw = yaw.Value;
            CameraDirection.Roll = roll.Value;
        }

        pitch.SettingChanged += (_, _) => Refresh();
        yaw.SettingChanged += (_, _) => Refresh();
        roll.SettingChanged += (_, _) => Refresh();
        Refresh();
    }

    private void BindPosition()
    {
        ConfigEntry<float> horizontal = BindMeters("镜头位置", "水平", "沿镜头右方向移动，单位为米。");
        ConfigEntry<float> vertical = BindMeters("镜头位置", "垂直", "沿镜头上方向移动，单位为米。");
        ConfigEntry<float> forward = BindMeters("镜头位置", "前后", "沿镜头前方向移动，单位为米。");
        void Refresh()
        {
            CameraPosition.Horizontal = horizontal.Value;
            CameraPosition.Vertical = vertical.Value;
            CameraPosition.Forward = forward.Value;
        }

        horizontal.SettingChanged += (_, _) => Refresh();
        vertical.SettingChanged += (_, _) => Refresh();
        forward.SettingChanged += (_, _) => Refresh();
        Refresh();
    }

    private ConfigEntry<float> BindDegrees(string section, string key, string description)
    {
        return Config.Bind(section, key, 0f,
            new ConfigDescription(description, new AcceptableValueRange<float>(-180f, 360f)));
    }

    private ConfigEntry<float> BindMeters(string section, string key, string description)
    {
        return Config.Bind(section, key, 0f,
            new ConfigDescription(description, new AcceptableValueRange<float>(-0.05f, 0.05f)));
    }

    private void OnEnable()
    {
        Camera.onPreCull += NewCameraSwitch.OnCameraPreCull;
    }

    private void OnDisable()
    {
        Camera.onPreCull -= NewCameraSwitch.OnCameraPreCull;
    }

    private void LateUpdate()
    {
        NewCameraSwitch.LateUpdate();
    }
}

internal static class NewCameraSwitch
{
    internal static bool Enabled;

    private static bool _applied;

    internal static void LateUpdate()
    {
        Player? player = Singleton<GameWorld>.Instance?.MainPlayer;
        if (!Enabled)
        {
            if (_applied && player != null && player.IsYourPlayer && player.PointOfView != EPointOfView.FirstPerson)
                player.PointOfView = EPointOfView.FirstPerson;
            _applied = false;
            return;
        }

        if (player == null || !player.IsYourPlayer)
            return;

        if (player.PointOfView != EPointOfView.ThirdPerson)
            player.PointOfView = EPointOfView.ThirdPerson;
        HeadMotion.Apply(player);
        _applied = true;
    }

    internal static void OnCameraPreCull(Camera camera)
    {
        if (!Enabled || camera == null || !camera.CompareTag("MainCamera"))
            return;

        Player? player = Singleton<GameWorld>.Instance?.MainPlayer;
        Transform? head = player != null && player.IsYourPlayer ? player.PlayerBones?.Head?.Original : null;
        if (head == null)
            return;

        Quaternion rotation = CameraDirection.Resolve(head.rotation);
        Vector3 position = CameraPosition.Resolve(head.position, rotation);
        camera.transform.SetPositionAndRotation(position, rotation);
    }
}
