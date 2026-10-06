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
    public const string PluginVersion = "0.1.60";

    private void Awake()
    {
        ConfigEntry<bool> enabled = Config.Bind("新镜头", "总开关", false,
            "打开后使用第三人称骨架，并把游戏镜头挂在人物头部。");
        AimArms.Install();
        BodyVisibility.Install();
        FirstPersonAudio.Install();
        NewCameraSwitch.Enabled = enabled.Value;
        enabled.SettingChanged += (_, _) => NewCameraSwitch.Enabled = enabled.Value;
        BindDirection();
        BindPosition();
        BindAimDistance();
        BindRecoil();
        BindSprint();
        BindBody();
    }

    private void BindRecoil()
    {
        ConfigEntry<float> strength = Config.Bind("后坐力", "倍率", 0.3f,
            new ConfigDescription("原生后坐力的倍率。0 为没有后坐力，1 为原生满强度。镜头和枪共用这一条。", new AcceptableValueRange<float>(0f, 1f)));
        strength.SettingChanged += (_, _) => Recoil.Strength = strength.Value;
        Recoil.Strength = strength.Value;
    }

    private void BindSprint()
    {
        ConfigEntry<float> sway = Config.Bind("奔跑", "水平摆动", 0.5f,
            new ConfigDescription("奔跑时头部相对环境的水平摆动强度。0 为去掉左右和前后的水平晃动，上下起伏保留。1 为动画原样。奔跑时仍可用鼠标转动视角。", new AcceptableValueRange<float>(0f, 1f)));
        sway.SettingChanged += (_, _) => HeadMotion.SprintHorizontalSway = sway.Value;
        HeadMotion.SprintHorizontalSway = sway.Value;
    }

    private void BindBody()
    {
        ConfigEntry<bool> showHead = Config.Bind("人物", "显示头部", false, "让本地玩家原本仅投射阴影的头部模型参与正常渲染。");
        ConfigEntry<bool> showBody = Config.Bind("人物", "显示身体", false, "让本地玩家原本仅投射阴影的身体模型参与正常渲染。");
        ConfigEntry<bool> showFeet = Config.Bind("人物", "显示腿脚", false, "显示本地玩家腿部和脚部模型。");
        ConfigEntry<bool> hideHeadwear = Config.Bind("人物", "隐藏头部装备", false, "隐藏头部槽装备，例如头盔和帽子。身上其他装备仍然显示。");
        ConfigEntry<bool> hideFaceCover = Config.Bind("人物", "隐藏面部装备", false, "隐藏面部槽装备，例如面罩和头套。身上其他装备仍然显示。");
        ConfigEntry<bool> hideEyewear = Config.Bind("人物", "隐藏眼部装备", false, "隐藏眼部槽装备，例如眼镜和护目镜。身上其他装备仍然显示。");
        void Refresh()
        {
            BodyVisibility.ShowHead = showHead.Value;
            BodyVisibility.ShowBody = showBody.Value;
            BodyVisibility.ShowFeet = showFeet.Value;
            BodyVisibility.HideHeadwear = hideHeadwear.Value;
            BodyVisibility.HideFaceCover = hideFaceCover.Value;
            BodyVisibility.HideEyewear = hideEyewear.Value;
            BodyVisibility.Restore();
        }

        showHead.SettingChanged += (_, _) => Refresh();
        showBody.SettingChanged += (_, _) => Refresh();
        showFeet.SettingChanged += (_, _) => Refresh();
        hideHeadwear.SettingChanged += (_, _) => Refresh();
        hideFaceCover.SettingChanged += (_, _) => Refresh();
        hideEyewear.SettingChanged += (_, _) => Refresh();
        Refresh();
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
                HeadMotion.Restore();
                BodyVisibility.Restore();
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
        HeadMotion.Apply(player);
        AimArms.Apply(player);
        BodyVisibility.Apply(player);
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
                HeadMotion.Apply(player);
                _poseRotation = CameraDirection.Resolve(head.rotation);
                _posePosition = CameraPosition.Resolve(head.position, _poseRotation);
                _poseRotation *= Recoil.CameraKick(player);
                AimArms.AlignToCamera(player, _posePosition, _poseRotation);
                Recoil.KickWeapon(player);
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
