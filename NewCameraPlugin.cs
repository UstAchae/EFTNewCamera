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
    public const string PluginVersion = "0.1.108";

    private void Awake()
    {
        ConfigEntry<bool> enabled = Config.Bind("新镜头", "总开关", false,
            "打开后使用第三人称骨架，并把游戏镜头挂在人物头部。");
        AimArms.Log = Logger;
        AimArms.Install();
        FirstPersonAudio.Install();
        NewCameraSwitch.Enabled = enabled.Value;
        enabled.SettingChanged += (_, _) => NewCameraSwitch.Enabled = enabled.Value;
        BindDirection();
        BindPosition();
        BindAimDistance();
        BindSway();
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

    private void BindSway()
    {
        ConfigEntry<float> gunMove = BindAmount("枪的平移", 0f,
            "枪自己的晃（转视角时的甩动、后坐力等）带来的平移。0 是瞄具钉在眼前不动，1 是游戏原样。");
        ConfigEntry<float> gunTurn = BindAmount("枪的旋转", 1f,
            "枪自己的晃（转视角时的甩动、后坐力等）带来的转动。0 是不转，1 是游戏原样。");
        ConfigEntry<float> armMove = BindAmount("手臂的平移", 0f,
            "人物动画带着枪走的平移（呼吸、走路、手上动作等）。0 是瞄具钉在眼前不动，1 是游戏原样。");
        ConfigEntry<float> armTurn = BindAmount("手臂的旋转", 1f,
            "人物动画带着枪走的转动（呼吸、走路、手上动作等）。0 是不转，1 是游戏原样。");
        ConfigEntry<float> headFollow = BindAmount("跟随头部", 0f,
            "枪跟着头的细小晃动（走路颠簸、呼吸等）走多少。0 是枪只认头晃动的中心：方向是鼠标朝向加头的固定偏角，位置是头相对脖子的中心；1 是瞄具每帧摆到头上镜头的正前方。");
        ConfigEntry<bool> chase = Config.Bind("瞄准摆动", "照原版慢半拍跟上", true,
            "打开时瞄具不再每帧钉死在眼前，而是像原版第一人称那样慢半拍追过去，拉栓这类快动作会先把枪带走再回来。关掉就是每帧钉死。");
        void Refresh()
        {
            AimArms.Chase = chase.Value;
            AimArms.HeadFollow = headFollow.Value;
            AimArms.GunMove = gunMove.Value;
            AimArms.GunTurn = gunTurn.Value;
            AimArms.ArmMove = armMove.Value;
            AimArms.ArmTurn = armTurn.Value;
        }

        chase.SettingChanged += (_, _) => Refresh();
        headFollow.SettingChanged += (_, _) => Refresh();
        gunMove.SettingChanged += (_, _) => Refresh();
        gunTurn.SettingChanged += (_, _) => Refresh();
        armMove.SettingChanged += (_, _) => Refresh();
        armTurn.SettingChanged += (_, _) => Refresh();
        Refresh();
    }

    private ConfigEntry<float> BindAmount(string key, float defaultValue, string description)
    {
        return Config.Bind("瞄准摆动", key, defaultValue,
            new ConfigDescription(description, new AcceptableValueRange<float>(0f, 1f)));
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
                Quaternion lastRotation = _poseRotation;
                _poseRotation = CameraDirection.Resolve(head.rotation);
                _posePosition = CameraPosition.Resolve(head.position, _poseRotation);
                LogFreeLook(player, lastRotation);
                AimArms.ObserveHead(player, _posePosition, _poseRotation);
                AimArms.AlignToCamera(player, _posePosition, _poseRotation);
                _poseFrame = Time.frameCount;
            }
        }

        if (main && _poseFrame == Time.frameCount)
            camera.transform.SetPositionAndRotation(_posePosition, _poseRotation);
        if (optic && _poseFrame == Time.frameCount)
            AimArms.CarryOptic(camera);

        AimArms.ReplayLasers(camera);
    }

    private static float _freeLookLogUntil;
    private static bool _freeLooking;

    private static void LogFreeLook(Player player, Quaternion lastRotation)
    {
        if (AimArms.Log == null)
            return;

        Vector3 head = player.HeadRotation;
        bool freeLooking = head.sqrMagnitude > 0.01f;
        if (freeLooking && !_freeLooking)
            _freeLookLogUntil = Time.time + 3f;
        _freeLooking = freeLooking;
        if (!freeLooking || Time.time > _freeLookLogUntil)
            return;

        Vector2 mouse = player.MovementContext != null ? player.MovementContext.Rotation : Vector2.zero;
        Quaternion delta = Quaternion.Inverse(lastRotation) * _poseRotation;
        float turned = 2f * Mathf.Atan2(new Vector3(delta.x, delta.y, delta.z).magnitude, Mathf.Abs(delta.w)) * Mathf.Rad2Deg;
        AimArms.Log.LogInfo(string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "扭头 帧={0} dt={1:F4} 扭头角=({2:F3},{3:F3}) 画面转={4:F3} 鼠标=({5:F3},{6:F3}) 瞄准={7}",
            Time.frameCount, Time.deltaTime, head.x, head.y,
            turned, mouse.x, mouse.y,
            player.HandsController != null && player.HandsController.IsAiming));
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
