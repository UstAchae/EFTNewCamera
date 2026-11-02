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
[BepInDependency("com.borkel.nvgmasks", BepInDependency.DependencyFlags.SoftDependency)]   // BRNVG makes the eyepiece picture
public sealed class NewCameraPlugin : BaseUnityPlugin
{
    public const string PluginGuid = "justachae.thirdpersoncamera";
    public const string PluginName = "Justachae's Third-Person Skeleton and Camera";
    public const string PluginVersion = "1.3.0";

    internal static BepInEx.Logging.ManualLogSource? Log;
    private const string SkeletonThird = "第三人称骨架", SkeletonFirst = "第一人称骨架";

    private void Awake()
    {
        Log = Logger;
        // No section: the F12 window shows the master switch right under the plugin's name, without a heading.
        ConfigEntry<bool> enabled = Config.Bind("", "总开关", true,
            new ConfigDescription("关掉后整个 mod 不起作用，一切按游戏原样。", null, new ConfigurationManagerAttributes { Order = 4 }));
        MoveFrom(enabled, "新镜头", "总开关");
        ConfigEntry<string> skeleton = Config.Bind("", "骨架", SkeletonThird,
            new ConfigDescription("第三人称骨架：用第三人称的身体动作（呼吸摆动、跑步转向时头部翻滚、换枪和翻背包的动作）。第一人称骨架：身体动作用游戏原版的第一人称（没有呼吸摆动和跑步转向时的头部翻滚，换枪、翻背包没有身体动作），晃得少，适合觉得第三人称骨架晕的玩家。除了身体动作，两种骨架的其他功能完全一样（镜头挂在头部、人物显示、瞄准等），只是第一人称骨架下瞄准时枪本身按原版第一人称的摆动晃（呼吸、手抖，屏息时由原版自己减弱），“瞄准摆动”那几组里的“摆动”只管手臂跟着身体动作的晃动，“呼吸”不起作用。局内可以随时切换。",
                new AcceptableValueList<string>(SkeletonThird, SkeletonFirst), new ConfigurationManagerAttributes { Order = 1 }));
        AimPatches.Install();
        Fisheye.Install();
        BodyVisibility.Install();
        VisorFilter.Install();
        FirstPersonAudio.Install();
        AimFieldOfView.Install();
        NightVisionEyepieces.Install();
        NewCameraSwitch.Enabled = enabled.Value;
        NewCameraSwitch.FirstPersonSkeleton = skeleton.Value == SkeletonFirst;
        enabled.SettingChanged += (_, _) => NewCameraSwitch.Enabled = enabled.Value;
        skeleton.SettingChanged += (_, _) => NewCameraSwitch.FirstPersonSkeleton = skeleton.Value == SkeletonFirst;
        BindFisheye();
        BindDirection();
        BindPosition();
        BindAimDistance();
        BindSway();
        BindBody();
        BindVestibular();
        BindNightVision();
        OpticElevation.Initialize();
    }

    private void BindFisheye()
    {
        ConfigEntry<bool> fisheye = Config.Bind("", "鱼眼", false,
            new ConfigDescription("画面像透过鱼眼镜头看：视野更宽，越往边缘压得越扁，直线往外弯。瞄准时、头戴夜视仪或热成像的目镜在眼前时自动渐变回普通画面。画面中心是放大过的，视野开得越大中心越糊；游戏图像设置里的超采样能减轻。",
                null, new ConfigurationManagerAttributes { Order = 3 }));
        ConfigEntry<float> field = Config.Bind("", "鱼眼视野", 120f,
            new ConfigDescription("鱼眼开着时，屏幕左边缘到右边缘之间的角度（度）。游戏设置里的视野是上下方向的，75 在 16:9 屏幕上大约相当于这里的 107。",
                new AcceptableValueRange<float>(100f, 140f), new ConfigurationManagerAttributes { Order = 2 }));
        void Refresh()
        {
            Fisheye.Enabled = fisheye.Value;
            Fisheye.Field = field.Value;
        }

        fisheye.SettingChanged += (_, _) => Refresh();
        field.SettingChanged += (_, _) => Refresh();
        Refresh();
    }

    private void BindNightVision()
    {
        ConfigEntry<bool> binocular = Config.Bind("头戴式成像装备", "双眼镜头", true,
            new ConfigDescription("头戴夜视仪和热成像开着时，镜头在原地分成左右两个相机，像两只眼睛：各自横移到对准自己那一侧的目镜，随夜视仪翻下的动画逐渐分开。屏幕从中间分成左右两半，左半边是左眼看到的，右半边是右眼看到的，中间平滑过渡。两个相机只画离眼半米以内的东西（夜视仪、头盔边缘），远处的景物仍是主镜头的。镜头离夜视仪较远（比如自由镜头）时不分开。",
                null, new ConfigurationManagerAttributes { Order = 2 }));
        ConfigEntry<float> eyeShift = Config.Bind("头戴式成像装备", "双眼平移比例", 0.8f,
            new ConfigDescription("双眼镜头时，两个相机各自往外平移多少：1 是正好对准自己那一侧的中间镜片，0 是不平移。画面里的景物始终完整，平移只改变目镜的圆在画面里的位置和被镜片边缘挡掉多少。",
                new AcceptableValueRange<float>(0f, 1.2f), new ConfigurationManagerAttributes { Order = 1 }));
        ConfigEntry<KeyboardShortcut> raiseMount = Config.Bind("头戴式成像装备", "挂架升高", new KeyboardShortcut(KeyCode.Equals, KeyCode.LeftAlt),
            new ConfigDescription("按住时把支架往上调（TATM 的滑块、G24 的托架）。只在夜视仪开着、并且处于腰射（没有瞄准）时有效。每个支架（每一件物品）各自记住自己调好的高度、仰俯和前后，存在配置文件夹里的 justachae.thirdpersoncamera.mounts.txt。", null, new ConfigurationManagerAttributes { Order = -1 }));
        ConfigEntry<KeyboardShortcut> lowerMount = Config.Bind("头戴式成像装备", "挂架降低", new KeyboardShortcut(KeyCode.Minus, KeyCode.LeftAlt),
            new ConfigDescription("按住时把支架往下调。只在夜视仪开着、并且处于腰射（没有瞄准）时有效。", null, new ConfigurationManagerAttributes { Order = -2 }));
        ConfigEntry<KeyboardShortcut> tiltUp = Config.Bind("头戴式成像装备", "挂架仰起", new KeyboardShortcut(KeyCode.Equals, KeyCode.LeftShift),
            new ConfigDescription("按住时把支架往上仰（目镜抬高）。只在夜视仪开着、并且处于腰射（没有瞄准）时有效。", null, new ConfigurationManagerAttributes { Order = -4 }));
        ConfigEntry<KeyboardShortcut> tiltDown = Config.Bind("头戴式成像装备", "挂架俯下", new KeyboardShortcut(KeyCode.Minus, KeyCode.LeftShift),
            new ConfigDescription("按住时把支架往下俯（目镜降低）。只在夜视仪开着、并且处于腰射（没有瞄准）时有效。", null, new ConfigurationManagerAttributes { Order = -5 }));
        ConfigEntry<KeyboardShortcut> slideForward = Config.Bind("头戴式成像装备", "挂架往前", new KeyboardShortcut(KeyCode.Equals, KeyCode.LeftControl),
            new ConfigDescription("按住时把夜视仪沿支架往前推（离眼睛远；G24 一格一格地走）。只在夜视仪开着、并且处于腰射（没有瞄准）时有效。", null, new ConfigurationManagerAttributes { Order = -7 }));
        ConfigEntry<KeyboardShortcut> slideBack = Config.Bind("头戴式成像装备", "挂架往后", new KeyboardShortcut(KeyCode.Minus, KeyCode.LeftControl),
            new ConfigDescription("按住时把夜视仪沿支架往后拉（离眼睛近）。只在夜视仪开着、并且处于腰射（没有瞄准）时有效。", null, new ConfigurationManagerAttributes { Order = -8 }));
        ConfigEntry<float> headwearPitch = Config.Bind("头戴式成像装备", "头部装备仰俯", 0f,
            new ConfigDescription("只转头盔（和挂在上面的夜视仪），头和镜头不动。把头盔前沿往上抬多少度，只能往上，不能往下压。也可以用下面两个快捷键按住调整。",
                new AcceptableValueRange<float>(0f, HeadwearTilt.Range), new ConfigurationManagerAttributes { Order = -9 }));
        ConfigEntry<KeyboardShortcut> headwearUp = Config.Bind("头戴式成像装备", "头部装备仰起", HeadwearTilt.Up,
            new ConfigDescription("按住时把头盔前沿往上抬。只在腰射（没有瞄准）时有效，瞄准时同样的键调瞄准距离。", null, new ConfigurationManagerAttributes { Order = -10 }));
        ConfigEntry<KeyboardShortcut> headwearDown = Config.Bind("头戴式成像装备", "头部装备俯下", HeadwearTilt.Down,
            new ConfigDescription("按住时把头盔前沿往回放（最低回到原位）。", null, new ConfigurationManagerAttributes { Order = -11 }));
        HeadwearTilt.Pitch = headwearPitch;
        ConfigEntry<string> picture = Config.Bind("头戴式成像装备", "夜视画面来源", NightVisionEyepieces.PictureAuto,
            new ConfigDescription("头戴夜视仪和热成像只在自己的目镜里成像，目镜以外是不带夜视的原样画面，设备模型照常画出。支持 GPNVG-18、PNV-10T、PNV-57E、Armasight N-15、AN/PVS-14（单筒）、模组物品 PVS-31A 和 T-7 热成像；其他型号仍是游戏原样的全屏滤镜。这一项选目镜里的夜视画面用谁做。自动：装了 BRNVG 就用 BRNVG，没装就用游戏原版的夜视滤镜。BRNVG：用 BRNVG 的画面，它的 F12 设置（增益、颜色、噪点、泛光、近处虚焦等）都会作用到目镜里；没装 BRNVG 时按原版。原版：用游戏自己的夜视滤镜（绿色、噪点），装了 BRNVG 也不用它的画面。热成像设备不受这个选项影响。",
                new AcceptableValueList<string>(NightVisionEyepieces.PictureAuto, NightVisionEyepieces.PictureBrnvg, NightVisionEyepieces.PictureGame),
                new ConfigurationManagerAttributes { Order = 3 }));
        ConfigEntry<bool> glow = Config.Bind("头戴式成像装备", "目镜光晕", true,
            new ConfigDescription("夜视开着时，镜片的光往四周晕开，照在镜片玻璃边缘和眼罩内侧的那一圈光。关掉后只剩镜片里的画面。热成像也一样。",
                null, new ConfigurationManagerAttributes { Order = 0 }));
        ConfigEntry<bool> reflections = Config.Bind("头戴式成像装备", "镜片反光", true,
            new ConfigDescription("夜视仪翻下时，目镜镜片表面映出的周围环境和高光，叠在镜片里的夜视画面上。关掉后镜片里只剩夜视画面，设备上的玻璃部件也不画。",
                null, new ConfigurationManagerAttributes { Order = 0 }));
        ConfigEntry<float> monocularSeeThrough = Config.Bind("头戴式成像装备", "PVS-14 左眼透明度", 0.5f,
            new ConfigDescription("AN/PVS-14 单筒夜视仪只挡在右眼前，左眼看过去时机身往画面边缘逐渐变透明。0：不变透明（两只眼都看到完整的机身）；0.5：默认；1：变透明得最快。",
                new AcceptableValueRange<float>(0f, 1f), new ConfigurationManagerAttributes { Order = 0 }));
        void Refresh()
        {
            NightVisionEyepieces.Picture = picture.Value;
            HeadwearTilt.Degrees = headwearPitch.Value;
            HeadwearTilt.Up = headwearUp.Value;
            HeadwearTilt.Down = headwearDown.Value;
            NightVisionEyepieces.EyepieceGlow = glow.Value;
            NightVisionEyepieces.LensReflections = reflections.Value;
            NightVisionEyepieces.MonocularSeeThrough = monocularSeeThrough.Value;
            NightVisionEyepieces.RaiseMount = raiseMount.Value;
            NightVisionEyepieces.LowerMount = lowerMount.Value;
            NightVisionEyepieces.TiltUp = tiltUp.Value;
            NightVisionEyepieces.TiltDown = tiltDown.Value;
            NightVisionEyepieces.SlideForward = slideForward.Value;
            NightVisionEyepieces.SlideBack = slideBack.Value;
            NightVisionEyepieces.Binocular = binocular.Value;
            NightVisionEyepieces.EyeShift = eyeShift.Value;
        }

        picture.SettingChanged += (_, _) => Refresh();
        headwearPitch.SettingChanged += (_, _) => Refresh();
        headwearUp.SettingChanged += (_, _) => Refresh();
        headwearDown.SettingChanged += (_, _) => Refresh();
        glow.SettingChanged += (_, _) => Refresh();
        reflections.SettingChanged += (_, _) => Refresh();
        monocularSeeThrough.SettingChanged += (_, _) => Refresh();
        binocular.SettingChanged += (_, _) => Refresh();
        eyeShift.SettingChanged += (_, _) => Refresh();
        raiseMount.SettingChanged += (_, _) => Refresh();
        lowerMount.SettingChanged += (_, _) => Refresh();
        tiltUp.SettingChanged += (_, _) => Refresh();
        tiltDown.SettingChanged += (_, _) => Refresh();
        slideForward.SettingChanged += (_, _) => Refresh();
        slideBack.SettingChanged += (_, _) => Refresh();
        Refresh();
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
            "画出头盔和帽子。关掉后头部装备不画，但头盔上挂的夜视仪、热成像和它们的支架照常画出（用来对付全封闭头盔挡住视线）。");
        ConfigEntry<bool> showFaceCover = Config.Bind("人物", "显示面部装备", false,
            "画出面罩和头套。关掉后整个面部装备都不画。");
        ConfigEntry<bool> showEyewear = Config.Bind("人物", "显示眼部装备", false,
            "画出眼镜和护目镜。关掉后整个眼部装备都不画。");
        ConfigEntry<bool> showVisor = Config.Bind("人物", "显示面罩滤镜", false,
            "画出面罩玻璃上的滤镜。关掉后玻璃还在，滤镜画面没有。");
        ConfigEntry<float> headPitch = Config.Bind("人物", "头部仰俯", 17f,
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
        ConfigEntry<float> strength = Config.Bind("前庭平衡", "强度", 0.7f,
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
        ConfigEntry<float> vertical = BindMeters("镜头位置", "垂直", 0.05f, "沿镜头上方向移动，单位为米。");
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
            "瞄准时按住，瞄准的前后距离慢慢变大，枪离镜头更远。不瞄准时这个键不起作用（腰射时同样的键调夜视仪挂架）。");
        _aimDistanceCloser = Config.Bind("瞄准", "前后距离调近快捷键", new KeyboardShortcut(KeyCode.Minus),
            "瞄准时按住，瞄准的前后距离慢慢变小，枪离镜头更近。不瞄准时这个键不起作用（腰射时同样的键调夜视仪挂架）。");
        _aimDistance.SettingChanged += (_, _) => AimArms.ForwardDistance = _aimDistance.Value;
        AimArms.ForwardDistance = _aimDistance.Value;
    }

    private const float AimDistanceMin = -0.10f;
    private const float AimDistanceMax = 0f;
    private const float AimDistanceSpeed = 0.05f;
    private ConfigEntry<float>? _aimDistance;
    private ConfigEntry<KeyboardShortcut>? _aimDistanceFarther;
    private ConfigEntry<KeyboardShortcut>? _aimDistanceCloser;

    private void AdjustAimDistance()
    {
        if (_aimDistance == null || _aimDistanceFarther == null || _aimDistanceCloser == null || !NewCameraSwitch.Enabled)
            return;
        // Only while aiming: the same keys set the helmet mount at the hip.
        bool aiming = Comfort.Common.Singleton<GameWorld>.Instance?.MainPlayer?.ProceduralWeaponAnimation?.IsAiming == true;
        float step = 0f;
        if (aiming && _aimDistanceFarther.Value.IsPressed())
            step += AimDistanceSpeed * Time.deltaTime;
        if (aiming && _aimDistanceCloser.Value.IsPressed())
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
        BindSwaySet("瞄准摆动-平时", "平时（不屏息、手臂有体力）", AimArms.Normal, 0.05f, 0.1f, 1f);
        BindSwaySet("瞄准摆动-屏息", "按住左 Alt 屏息时", AimArms.Holding, 0f, 0f, 0f);
        BindSwaySet("瞄准摆动-没体力", "手臂没体力时（优先于屏息）", AimArms.Exhausted, 1f, 1f, 1f);
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

    private void BindSwaySet(string section, string when, AimArms.SwayAmounts target, float sway, float headFollowDefault, float breathDefault)
    {
        ConfigEntry<float> swayAmount = BindAmount(section, "摆动", sway,
            when + "：枪和手臂快的晃动（转视角时的甩动、走路、手上动作等）带来的平移和转动。0 是瞄具钉在眼前不动，1 是游戏原样。");
        ConfigEntry<float> headFollow = BindAmount(section, "跟随头部", headFollowDefault,
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

    // The camera callbacks are each one delegate among the game's: one that throws stops the rest for that camera (the
    // game lights lights there, per camera), so each of ours is kept from throwing; a failure is logged once.
    private static readonly Camera.CameraCallback PreCull = camera =>
    {
        Guarded(NewCameraSwitch.OnCameraPreCull, camera, "pre-cull (camera)");
        Guarded(NightVisionEyepieces.OnCameraPreCull, camera, "pre-cull (eyepieces)");
    };
    private static readonly Camera.CameraCallback PreRender = camera =>
    {
        Guarded(NightVisionEyepieces.OnCameraPreRender, camera, "pre-render (eyepieces)");
        Guarded(NightVisionEyepieces.OnCameraPreRenderIr, camera, "pre-render (IR)");
    };
    private static readonly Camera.CameraCallback PostRender = camera =>
    {
        Guarded(NightVisionEyepieces.OnCameraPostRender, camera, "post-render (eyepieces)");
        Guarded(NightVisionEyepieces.OnCameraPostRenderIr, camera, "post-render (IR)");
        Guarded(OpticElevation.Restore, camera, "post-render (optic)");
    };
    private static readonly System.Collections.Generic.HashSet<string> Reported = new System.Collections.Generic.HashSet<string>();

    private static void Guarded(Camera.CameraCallback callback, Camera camera, string name)
    {
        try
        {
            callback(camera);
        }
        catch (System.Exception e)
        {
            if (Reported.Add(name + e.GetType().Name + e.TargetSite))
                Log?.LogError($"Camera {name} failed (reported once): {e}");
        }
    }

    private void OnEnable()
    {
        StartCoroutine(EndOfFrame());
        Camera.onPreCull += PreCull;
        Camera.onPreRender += PreRender;
        Camera.onPostRender += PostRender;
    }

    // The fisheye's field handed back to the game once the frame is drawn, before its next frame's updates read it.
    private static System.Collections.IEnumerator EndOfFrame()
    {
        WaitForEndOfFrame end = new WaitForEndOfFrame();
        while (true)
        {
            yield return end;
            Fisheye.GiveBack();
        }
    }

    private void OnDisable()
    {
        Fisheye.GiveBack();
        Camera.onPreCull -= PreCull;
        Camera.onPreRender -= PreRender;
        Camera.onPostRender -= PostRender;
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
        Fisheye.LateUpdate();
    }
}

internal static class NewCameraSwitch
{
    internal static bool Enabled;
    internal static bool FirstPersonSkeleton;
    private static bool _applied;
    private static int _poseFrame = -1;
    private static Vector3 _posePosition;

    // The camera is where this mod put it this frame, at the player's eyes, and has not been moved off since (a free
    // camera places itself after this mod does).
    internal static bool AtEyes(Camera camera) => _poseFrame == Time.frameCount && (camera.transform.position - _posePosition).sqrMagnitude < 1e-6f;
    private static Quaternion _poseRotation;

    // Where the camera was put from the head this frame (along the world's axes), as it was worked out, not as the
    // difference of the two places in the world: far out in a map those are rounded to tens of micrometres.
    internal static Transform? PoseHead;
    internal static Vector3 PoseFromHead;

    internal static void LateUpdate()
    {
        Player? player = Singleton<GameWorld>.Instance?.MainPlayer;
        bool mine = player != null && player.IsYourPlayer;
        // The hideout overview (before walking in) and the gym use the game's free camera.
        bool wanted = Enabled && mine && player!.PointOfView != EPointOfView.FreeCamera;
        // Let go of whenever this mod stops applying: switched off, the free camera, or the player gone or another
        // one (a raid over), whether or not there is a player to hand things back to.
        if (_applied && (!wanted || !ReferenceEquals(player, _appliedTo)))
        {
            ReleaseAll(ReferenceEquals(player, _appliedTo) ? player : null);
            _applied = false;
            _appliedTo = null;
        }
        if (!wanted)
            return;

        if (player!.PointOfView != EPointOfView.ThirdPerson)
            player.PointOfView = EPointOfView.ThirdPerson;
        if (!_applied)
        {
            AimFieldOfView.Reset();
            VestibularView.Reset();
        }
        ChooseSkeleton(player);
        AimArms.Apply(player);
        NightVisionEyepieces.Update(player);
        BodyVisibility.Apply(player);
        _applied = true;
        _appliedTo = player;
    }

    // The body's animation, all else as the third-person view has it: the first-person skeleton is the game's
    // first-person animator (no breathing sway, no roll of the head turning at a run, no body moves for changing the
    // weapon or opening the backpack). The game sets it as it sets the point of view: the first-person one is set
    // every frame, the third-person one again once chosen back.
    private static void ChooseSkeleton(Player player)
    {
        bool first = FirstPersonSkeleton;
        if (!first && !_firstSkeleton && ReferenceEquals(_skeletonOf, player))
            return;
        player.MovementContext?.PlayerAnimatorPointOfView(first ? EPointOfView.FirstPerson : EPointOfView.ThirdPerson);
        _firstSkeleton = first;
        _skeletonOf = player;
    }

    private static bool _firstSkeleton;
    private static Player? _skeletonOf;
    private static Player? _appliedTo;

    // Everything this mod changed handed back, as the game had it; player: the one it was applied to, when still there.
    private static void ReleaseAll(Player? player)
    {
        NightVisionEyepieces.Release();
        CollimatorHandoff.Release();
        OpticPicture.Release(player);
        AimArms.Release(player);
        BodyVisibility.Restore();
        HeadwearTilt.Release();
        _skeletonOf = null;
        if (player == null || Enabled)
            return;
        // Switched off: the game's own view back at once, not at the next aim or pose change.
        if (player.PointOfView != EPointOfView.FirstPerson)
            player.PointOfView = EPointOfView.FirstPerson;
        AimArms.GiveBackAim(player);
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
            LaserReplay.Replay(camera);
            return;
        }

        if (main)
            OpticPicture.Apply(player);

        if (_poseFrame != Time.frameCount)
        {
            Transform? head = player.PlayerBones?.Head?.Original;
            if (head != null)
            {
                AimArms.UnliftHead(head);
                AimArms.FollowMountedGun(player);
                _poseRotation = CameraDirection.Resolve(head.rotation);
                _posePosition = CameraPosition.Resolve(head.position, _poseRotation);
                PoseFromHead = CameraPosition.Offset(_poseRotation);
                AimArms.ObserveHead(player, _posePosition, _poseRotation);
                AimArms.AlignToCamera(player, _posePosition, _poseRotation);
                if (AimArms.TrackMountedSight(player))
                {
                    _poseRotation = CameraDirection.Resolve(head.rotation);
                    _posePosition = CameraPosition.Resolve(head.position, _poseRotation);
                    PoseFromHead = CameraPosition.Offset(_poseRotation);
                }
                else
                    _poseRotation = AimArms.KickView(_poseRotation);
                _poseRotation = VestibularView.Stabilize(player, _posePosition, _poseRotation);
                Vector3 beforeRecoil = _posePosition;
                AimArms.BodyRecoil(player, ref _posePosition, ref _poseRotation);
                PoseFromHead += _posePosition - beforeRecoil;
                PoseHead = head;
                AimArms.LiftHead(head);
                _poseFrame = Time.frameCount;
            }
        }

        if (main && _poseFrame == Time.frameCount)
            camera.transform.SetPositionAndRotation(_posePosition, _poseRotation);
        if (optic && _poseFrame == Time.frameCount)
            AimArms.CarryOptic(camera);

        LaserReplay.Replay(camera);
        if (optic)
            OpticElevation.Apply(camera);
        if (main)
        {
            CollimatorHandoff.Update(camera);
        }
    }
}
