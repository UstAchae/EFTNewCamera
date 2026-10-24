using System.Collections.Generic;
using System.Reflection;
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
    public const string PluginGuid = "justachae.thirdpersoncamera";
    public const string PluginName = "Justachae's Third-Person Skeleton and Camera";
    public const string PluginVersion = "1.0.0";

    private void Awake()
    {
        // No section: the F12 window shows the master switch right under the plugin's name, without a heading.
        ConfigEntry<bool> enabled = Config.Bind("", "总开关", false,
            "打开后使用第三人称骨架，并把游戏镜头挂在人物头部。");
        MoveFrom(enabled, "新镜头", "总开关");
        AimArms.Install();
        BodyVisibility.Install();
        VisorFilter.Install();
        FirstPersonAudio.Install();
        ReticleAfterTaa.Install();
        AimFieldOfView.Install();
        NewCameraSwitch.Enabled = enabled.Value;
        enabled.SettingChanged += (_, _) => NewCameraSwitch.Enabled = enabled.Value;
        BindDirection();
        BindPosition();
        BindAimDistance();
        BindSway();
        BindBody();
        BindVestibular();
        OpticElevation.Initialize();
    }

    private void BindBody()
    {
        ConfigEntry<float> walkHead = Config.Bind("人物", "走路摇头", 0.2f,
            new ConfigDescription("走路和跑步时，把头的左右转动收到平均朝向上。镜头仍按原来的固定位置挂在头上，和头一起转。0 是不摇头，1 是游戏原样。站着不动和中键扭头不受影响。",
                new AcceptableValueRange<float>(0f, 1f), new ConfigurationManagerAttributes { Order = 1 }));
        MoveFrom(walkHead, "新镜头", "走路摇头");
        walkHead.SettingChanged += (_, _) => AimArms.WalkHeadSway = walkHead.Value;
        AimArms.WalkHeadSway = walkHead.Value;
        ConfigEntry<bool> showHead = Config.Bind("人物", "显示头部", false,
            "画出自己的头。关掉时头只投阴影，画面里看不见。");
        ConfigEntry<bool> showHeadwear = Config.Bind("人物", "显示头部装备", true,
            "画出头盔和帽子。关掉后整个头部装备都不画。打开时，夜视和热成像要等自己的滤镜真正亮起，才把那一台设备藏起来，滤镜关掉就再画出来。");
        ConfigEntry<bool> showFaceCover = Config.Bind("人物", "显示面部装备", false,
            "画出面罩和头套。关掉后整个面部装备都不画。上面挂的夜视或热成像，同样等自己的滤镜亮起才藏。");
        ConfigEntry<bool> showEyewear = Config.Bind("人物", "显示眼部装备", false,
            "画出眼镜和护目镜。关掉后整个眼部装备都不画。上面挂的夜视或热成像，同样等自己的滤镜亮起才藏。");
        ConfigEntry<bool> showVisor = Config.Bind("人物", "显示面罩滤镜", false,
            "画出面罩玻璃上的滤镜。关掉后玻璃还在，滤镜画面没有。");
        ConfigEntry<float> headPitch = Config.Bind("人物", "头部仰俯", 28f,
            new ConfigDescription("只转头部模型，镜头保持原位。正值把脸往上抬，单位为度。",
                new AcceptableValueRange<float>(-45f, 45f)));
        ConfigEntry<float> freeLookYaw = Config.Bind("人物", "中键扭头角度", 80f,
            new ConfigDescription("按住鼠标中键自由看时，头最多往左或往右转多少度。",
                new AcceptableValueRange<float>(30f, 150f)));
        ConfigEntry<float> freeLookDown = Config.Bind("人物", "中键低头额外角度", 20f,
            new ConfigDescription("按住鼠标中键自由看时，在原版能低头的角度上再多低多少度。0 是原版的范围。",
                new AcceptableValueRange<float>(0f, 60f)));
        ConfigEntry<float> axisForward = Config.Bind("人物", "中键扭头轴前后倾斜", 5f,
            new ConfigDescription("中键左右扭头时绕的那根轴，0 是竖直。正值让轴的上端往前倒，负值往后倒，单位为度。",
                new AcceptableValueRange<float>(-60f, 60f)));
        void Refresh()
        {
            AimArms.FreeLookYaw = freeLookYaw.Value;
            AimArms.FreeLookDownExtra = freeLookDown.Value;
            AimArms.LookAxisForward = axisForward.Value;
            BodyVisibility.ShowHead = showHead.Value;
            BodyVisibility.ShowHeadwear = showHeadwear.Value;
            BodyVisibility.ShowFaceCover = showFaceCover.Value;
            BodyVisibility.ShowEyewear = showEyewear.Value;
            VisorFilter.Show = showVisor.Value;
            AimArms.HeadModelPitch = headPitch.Value;
        }

        showHead.SettingChanged += (_, _) => Refresh();
        showHeadwear.SettingChanged += (_, _) => Refresh();
        showFaceCover.SettingChanged += (_, _) => Refresh();
        showEyewear.SettingChanged += (_, _) => Refresh();
        showVisor.SettingChanged += (_, _) => Refresh();
        headPitch.SettingChanged += (_, _) => Refresh();
        freeLookYaw.SettingChanged += (_, _) => Refresh();
        freeLookDown.SettingChanged += (_, _) => Refresh();
        axisForward.SettingChanged += (_, _) => Refresh();
        Refresh();
    }

    // A setting that moved to another section takes over the value saved under its old place, once.
    private void MoveFrom(ConfigEntryBase entry, string oldSection, string oldKey)
    {
        PropertyInfo? property = typeof(ConfigFile).GetProperty("OrphanedEntries", BindingFlags.Instance | BindingFlags.NonPublic);
        if (property?.GetValue(Config) is not Dictionary<ConfigDefinition, string> orphans)
            return;

        ConfigDefinition old = new ConfigDefinition(oldSection, oldKey);
        if (!orphans.TryGetValue(old, out string value))
            return;
        orphans.Remove(old);
        entry.SetSerializedValue(value);
        Config.Save();
    }

    private void BindVestibular()
    {
        ConfigEntry<bool> balance = Config.Bind("前庭平衡", "总开关", true,
            new ConfigDescription("像内耳前庭带着眼球那样，镜头位置不动，只把镜头转回玩家想看的方向，抵消走路、跑步时头的晃动。注视点取视线上实际看到的东西的距离；切枪、换弹、打药、翻背包、搜刮、开门等动作时，动画让头转去看手或包的部分会保留。开镜、架枪、固定武器、翻越、攀爬时不起作用；中键自由看和后坐力保留。关闭后镜头完全按头部原样晃动。",
                null, new ConfigurationManagerAttributes { Order = 3 }));
        ConfigEntry<float> strength = Config.Bind("前庭平衡", "强度", 0.8f,
            new ConfigDescription("左右和上下平衡多少。0 是不平衡（原样），1 是完全平衡。",
                new AcceptableValueRange<float>(0f, 1f), new ConfigurationManagerAttributes { Order = 2 }));
        ConfigEntry<float> torsion = Config.Bind("前庭平衡", "扭转补偿", 0.25f,
            new ConfigDescription("头绕视线歪斜（翻滚）时，镜头反向扭回多少。人眼只能反向扭回头部歪斜的一小部分（约一到三成），所以默认 0.25。0 是完全不管翻滚，1 是歪斜全部扭回。探头的歪斜也按这个比例减小。最终效果再乘上强度。",
                new AcceptableValueRange<float>(0f, 1f), new ConfigurationManagerAttributes { Order = 1 }));
        void Refresh()
        {
            VestibularView.Enabled = balance.Value;
            VestibularView.Strength = strength.Value;
            VestibularView.Torsion = torsion.Value;
        }

        balance.SettingChanged += (_, _) => Refresh();
        strength.SettingChanged += (_, _) => Refresh();
        torsion.SettingChanged += (_, _) => Refresh();
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
        ConfigEntry<float> vertical = BindMeters("镜头位置", "垂直", 0.08f, "沿镜头上方向移动，单位为米。");
        ConfigEntry<float> forward = BindMeters("镜头位置", "前后", 0.08f, "沿镜头前方向移动，单位为米。");
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
        _aimDistance = Config.Bind("瞄准", "前后距离", -0.05f,
            new ConfigDescription("瞄准后沿镜头前方向移动枪，单位为米。负值让枪离镜头更近。",
                new AcceptableValueRange<float>(AimDistanceMin, AimDistanceMax)));
        _aimDistanceFarther = Config.Bind("瞄准", "前后距离调远快捷键", new KeyboardShortcut(KeyCode.Equals),
            "按住时瞄准的前后距离慢慢变大，枪离镜头更远。");
        _aimDistanceCloser = Config.Bind("瞄准", "前后距离调近快捷键", new KeyboardShortcut(KeyCode.Minus),
            "按住时瞄准的前后距离慢慢变小，枪离镜头更近。");
        _aimDistance.SettingChanged += (_, _) => AimArms.ForwardDistance = _aimDistance.Value;
        AimArms.ForwardDistance = _aimDistance.Value;
        ReticleAfterTaa.Enabled = true;
    }

    private const float AimDistanceMin = -0.10f;
    private const float AimDistanceMax = 0f;
    private const float AimDistanceSpeed = 0.05f;
    private ConfigEntry<float>? _aimDistance;
    private ConfigEntry<KeyboardShortcut>? _aimDistanceFarther;
    private ConfigEntry<KeyboardShortcut>? _aimDistanceCloser;

    private void AdjustAimDistance()
    {
        if (_aimDistance == null || _aimDistanceFarther == null || _aimDistanceCloser == null)
            return;
        float step = 0f;
        if (_aimDistanceFarther.Value.IsPressed())
            step += AimDistanceSpeed * Time.deltaTime;
        if (_aimDistanceCloser.Value.IsPressed())
            step -= AimDistanceSpeed * Time.deltaTime;

        // Setting the config value saves the whole file, so it is written once when the key is let go.
        if (step != 0f)
        {
            AimArms.ForwardDistance = Mathf.Clamp(AimArms.ForwardDistance + step, AimDistanceMin, AimDistanceMax);
            _aimDistanceHeld = true;
        }
        else if (_aimDistanceHeld)
        {
            _aimDistanceHeld = false;
            _aimDistance.Value = AimArms.ForwardDistance;
        }
    }

    private bool _aimDistanceHeld;

    private void BindSway()
    {
        BindSwaySet("瞄准摆动-平时", "平时（不屏息、手臂有体力）", AimArms.Normal, 0.2f, 1f);
        BindSwaySet("瞄准摆动-屏息", "按住左 Alt 屏息时", AimArms.Holding, 0f, 0f);
        BindSwaySet("瞄准摆动-没体力", "手臂没体力时（优先于屏息）", AimArms.Exhausted, 1f, 1f);
        AimArms.Chase = true;
        ConfigEntry<float> followDelay = Config.Bind("瞄准摆动", "跟手延迟（秒）", 0.1f,
            new ConfigDescription("晃鼠标时枪落后画面的快慢，数越大越慢。开镜和退瞄后的腰射都用这个数。0 是不延迟。子弹跟着枪口。",
                new AcceptableValueRange<float>(0f, 0.3f)));
        ConfigEntry<float> recoilKick = Config.Bind("瞄准摆动", "后坐力上跳", 0.25f,
            new ConfigDescription("瞄准开枪时枪口上跳的力度，1 是原版第一人称的力度，0 是不跳。子弹跟着枪口走。不受上面摆动滑条影响。",
                new AcceptableValueRange<float>(0f, 2f)));
        ConfigEntry<float> recoilChest = Config.Bind("瞄准摆动", "后坐力传到胸腔", 2f,
            new ConfigDescription("开枪时原版第一人称给镜头的那一下后坐力，有多少推到胸腔上。胸腔带着肩膀和头一起转，镜头跟着头走；枪和手留在原地。0 是不推。和下面一项加起来是 1 时，镜头受到的力度跟原版第一人称一样。",
                new AcceptableValueRange<float>(0f, 2f)));
        ConfigEntry<float> recoilHead = Config.Bind("瞄准摆动", "后坐力传到头部", 1f,
            new ConfigDescription("同一份后坐力，有多少再推到头上（绕脖子转），镜头跟着头走。0 是不推。",
                new AcceptableValueRange<float>(0f, 2f)));
        void Refresh()
        {
            AimArms.RecoilKick = recoilKick.Value;
            AimArms.RecoilChest = recoilChest.Value;
            AimArms.RecoilHead = recoilHead.Value;
            AimArms.FollowDelay = followDelay.Value;
        }

        recoilKick.SettingChanged += (_, _) => Refresh();
        recoilChest.SettingChanged += (_, _) => Refresh();
        recoilHead.SettingChanged += (_, _) => Refresh();
        followDelay.SettingChanged += (_, _) => Refresh();
        Refresh();
    }

    private void BindSwaySet(string section, string when, AimArms.SwayAmounts target, float sway, float breathDefault)
    {
        ConfigEntry<float> swayAmount = BindAmount(section, "摆动", sway,
            when + "：枪和手臂快的晃动（转视角时的甩动、走路、手上动作等）带来的平移和转动。0 是瞄具钉在眼前不动，1 是游戏原样。");
        ConfigEntry<float> headFollow = BindAmount(section, "跟随头部", sway,
            when + "：瞄准方向跟着头的晃动（呼吸、走路颠簸、姿势带来的偏角）走多少，镜头和枪一起跟。0 是只认鼠标方向（探头的歪斜照样保留），1 是完全跟着头。");
        ConfigEntry<float> breath = BindAmount(section, "呼吸", breathDefault,
            when + "：手臂和枪跟着慢晃（多半是呼吸）转多少。摆动滑条只管快的晃动，这个管更慢的那部分。0 是不跟着转，1 是游戏原样。");
        void Refresh()
        {
            target.GunMove = swayAmount.Value;
            target.GunTurn = swayAmount.Value;
            target.ArmMove = swayAmount.Value;
            target.ArmTurn = swayAmount.Value;
            target.HeadFollow = headFollow.Value;
            target.Breath = breath.Value;
        }

        swayAmount.SettingChanged += (_, _) => Refresh();
        headFollow.SettingChanged += (_, _) => Refresh();
        breath.SettingChanged += (_, _) => Refresh();
        Refresh();
    }

    private ConfigEntry<float> BindAmount(string section, string key, float defaultValue, string description)
    {
        return Config.Bind(section, key, defaultValue,
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
        Camera.onPostRender += ReticleAfterTaa.OnCameraPostRender;
        Camera.onPostRender += OpticElevation.Restore;
    }

    private void OnDisable()
    {
        Camera.onPreCull -= NewCameraSwitch.OnCameraPreCull;
        Camera.onPostRender -= ReticleAfterTaa.OnCameraPostRender;
        Camera.onPostRender -= OpticElevation.Restore;
        OpticElevation.Shutdown();
    }

    private void Update()
    {
        OpticElevation.UpdateHeld();
        AdjustAimDistance();
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
                BodyVisibility.Restore();
                if (player.PointOfView != EPointOfView.FirstPerson)
                    player.PointOfView = EPointOfView.FirstPerson;
            }
            _applied = false;
            return;
        }

        if (player == null || !player.IsYourPlayer)
            return;

        // The hideout overview (before walking in) and the gym use the game's free camera.
        if (player.PointOfView == EPointOfView.FreeCamera)
        {
            if (_applied)
            {
                OpticPicture.Release();
                AimArms.Release(player);
                BodyVisibility.Restore();
            }
            _applied = false;
            return;
        }

        if (player.PointOfView != EPointOfView.ThirdPerson)
            player.PointOfView = EPointOfView.ThirdPerson;
        if (!_applied)
        {
            AimFieldOfView.Reset();
            VestibularView.Reset();
        }
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

        Player? player = Singleton<GameWorld>.Instance?.MainPlayer;
        if (player != null && player.IsYourPlayer && player.PointOfView == EPointOfView.FreeCamera)
            return;

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
                AimArms.FollowMountedGun(player);
                _poseRotation = CameraDirection.Resolve(head.rotation);
                _posePosition = CameraPosition.Resolve(head.position, _poseRotation);
                AimArms.ObserveHead(player, _posePosition, _poseRotation);
                AimArms.AlignToCamera(player, _posePosition, _poseRotation);
                if (AimArms.TrackMountedSight(player))
                {
                    _poseRotation = CameraDirection.Resolve(head.rotation);
                    _posePosition = CameraPosition.Resolve(head.position, _poseRotation);
                }
                else
                    _poseRotation = AimArms.KickView(_poseRotation);
                _poseRotation = VestibularView.Stabilize(player, _posePosition, _poseRotation);
                AimArms.BodyRecoil(player, ref _posePosition, ref _poseRotation);
                AimArms.LiftHead(head);
                _poseFrame = Time.frameCount;
            }
        }

        if (main && _poseFrame == Time.frameCount)
            camera.transform.SetPositionAndRotation(_posePosition, _poseRotation);
        if (optic && _poseFrame == Time.frameCount)
            AimArms.CarryOptic(camera);

        AimArms.ReplayLasers(camera);
        if (optic)
            OpticElevation.Apply(camera);
    }
}
