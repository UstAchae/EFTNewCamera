using BepInEx;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.Settings;
using UnityEngine;

namespace EFTNewCamera;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class NewCameraPlugin : BaseUnityPlugin
{
    public const string PluginGuid = "custom.eft.newcamera";
    public const string PluginName = "新镜头";
    public const string PluginVersion = "0.1.97";

    private void Awake()
    {
        ConfigEntry<bool> enabled = Config.Bind("新镜头", "总开关", false,
            "打开后使用第三人称骨架，并把游戏镜头挂在人物头部。");
        AimArms.Log = Logger;
        AimSway.Log = Logger;
        AimArms.Install();
        FirstPersonAudio.Install();
        NewCameraSwitch.Enabled = enabled.Value;
        enabled.SettingChanged += (_, _) => NewCameraSwitch.Enabled = enabled.Value;
        BindDirection();
        BindPosition();
        BindAimDistance();
        BindSways();
    }

    private void BindSways()
    {
        string[] parts = { "头", "脖子", "手臂", "枪" };
        string[] partNotes =
        {
            "头带着镜头动、枪不跟着动时，枪在画面里看起来的晃。画面本身不受影响。",
            "脖子带着头和镜头动、枪不跟着动时，枪在画面里看起来的晃。画面本身不受影响。",
            "手臂动画带着枪的晃，站着和走路时手臂的摆都在这里。",
            "枪在手上自己的晃，游戏程序给的那部分，不含后坐力。"
        };
        string[] axes = { "左右转", "上下转", "侧倾", "左右移", "上下移", "前后移" };
        string[] axisNotes = { "枪口左右转", "枪口上下转", "枪身绕枪管歪斜", "左右平移", "上下平移", "前后平移" };
        for (int part = 0; part < parts.Length; part++)
        {
            for (int axis = 0; axis < axes.Length; axis++)
            {
                int p = part;
                int a = axis;
                BindPortion(parts[part] + "-" + axes[axis], v => AimSway.Amounts[p, a] = v,
                    partNotes[part] + "这一项只管" + axisNotes[axis] + "。1 是游戏原来的，0 是这一项不晃。");
            }
        }

        string[] recoilAxes = { "左右移", "上下移", "前后移" };
        for (int axis = 0; axis < recoilAxes.Length; axis++)
        {
            int a = axis;
            BindPortion("后坐力-" + recoilAxes[axis], v => AimSway.RecoilAmounts[a] = v,
                "开枪时枪被顶动的" + recoilAxes[axis] + "。1 是游戏原来的，0 是不顶。");
        }
    }

    private void BindPortion(string key, System.Action<float> apply, string description)
    {
        ConfigEntry<float> entry = Config.Bind("瞄准摆动", key, 1f,
            new ConfigDescription(description + " 拉栓、切开火模式、换弹这些手上动作不受影响，始终照游戏原样。",
                new AcceptableValueRange<float>(0f, 1f)));
        entry.SettingChanged += (_, _) => apply(entry.Value);
        apply(entry.Value);
    }

    private void BindDirection()
    {
        ConfigEntry<float> pitch = BindDegrees("镜头朝向", "俯仰", -90f, "头骨坐标到镜头坐标的俯仰对齐，单位为度。");
        ConfigEntry<float> yaw = BindDegrees("镜头朝向", "偏航", -20f, "头骨坐标到镜头坐标的偏航对齐，单位为度。");
        ConfigEntry<float> roll = BindDegrees("镜头朝向", "翻滚", 90f, "头骨坐标到镜头坐标的翻滚对齐，单位为度。");
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
        ConfigEntry<float> horizontal = BindMeters("镜头位置", "水平", 0f, "沿镜头右方向移动，单位为米。");
        ConfigEntry<float> vertical = BindMeters("镜头位置", "垂直", 0f, "沿镜头上方向移动，单位为米。");
        ConfigEntry<float> forward = BindMeters("镜头位置", "前后", 0f, "沿镜头前方向移动，单位为米。");
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

    private void BindAimDistance()
    {
        ConfigEntry<float> distance = BindMeters("瞄准", "前后距离", 0f, "瞄准后沿镜头前方向移动枪，单位为米。正值让枪离镜头更远。");
        distance.SettingChanged += (_, _) => AimArms.ForwardDistance = distance.Value;
        AimArms.ForwardDistance = distance.Value;
    }

    private ConfigEntry<float> BindDegrees(string section, string key, float defaultValue, string description)
    {
        return Config.Bind(section, key, defaultValue,
            new ConfigDescription(description, new AcceptableValueRange<float>(-180f, 360f)));
    }

    private ConfigEntry<float> BindMeters(string section, string key, float defaultValue, string description)
    {
        return Config.Bind(section, key, defaultValue,
            new ConfigDescription(description, new AcceptableValueRange<float>(-0.05f, 1f)));
    }

    private void OnEnable()
    {
        Camera.onPreCull += NewCameraSwitch.OnCameraPreCull;
        Camera.onPreRender += NewCameraSwitch.OnCameraPreRender;
    }

    private void OnDisable()
    {
        Camera.onPreCull -= NewCameraSwitch.OnCameraPreCull;
        Camera.onPreRender -= NewCameraSwitch.OnCameraPreRender;
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
    private static int _poseFrame = -1;
    private static Vector3 _posePosition;
    private static Quaternion _poseRotation;
    internal static void LateUpdate()
    {
        Player? player = Singleton<GameWorld>.Instance?.MainPlayer;
        if (!Enabled)
        {
            if (_applied && player != null && player.IsYourPlayer)
            {
                OpticPicture.Release();
                AimArms.Release(player);
                if (player.PointOfView != EPointOfView.FirstPerson)
                    player.PointOfView = EPointOfView.FirstPerson;
            }
            _applied = false;
            return;
        }

        if (player == null || !player.IsYourPlayer)
            return;

        if (player.PointOfView != EPointOfView.ThirdPerson)
            player.PointOfView = EPointOfView.ThirdPerson;
        AimArms.Apply(player);
        _applied = true;
    }

    internal static void OnCameraPreCull(Camera camera)
    {
        if (!Enabled || camera == null)
            return;

        bool main = camera.CompareTag("MainCamera");
        bool optic = OpticPicture.IsCamera(camera);
        if (!main && !optic)
            return;

        if (main)
            HoldFieldOfView(camera);

        Player? player = Singleton<GameWorld>.Instance?.MainPlayer;
        if (player == null || !player.IsYourPlayer)
        {
            AimArms.ReplayLasers(camera);
            return;
        }

        if (main)
            OpticPicture.Apply(player);

        if (_poseFrame != Time.frameCount)
        {
            Transform? head = player.PlayerBones?.Head?.Original;
            if (head != null)
            {
                _poseRotation = CameraDirection.Resolve(head.rotation);
                _posePosition = CameraPosition.Resolve(head.position, _poseRotation);
                _poseFrame = Time.frameCount;
            }
        }

        if (main && _poseFrame == Time.frameCount)
            camera.transform.SetPositionAndRotation(_posePosition, _poseRotation);

        AimArms.ReplayLasers(camera);
    }

    internal static void OnCameraPreRender(Camera camera)
    {
        if (!Enabled || camera == null || !camera.CompareTag("MainCamera"))
            return;

        HoldFieldOfView(camera);
    }

    private static void HoldFieldOfView(Camera camera)
    {
        if (!Singleton<SettingsManager>.Instantiated)
            return;

        camera.fieldOfView = Singleton<SettingsManager>.Instance.Game.Settings.FieldOfView.Value;
    }
}
