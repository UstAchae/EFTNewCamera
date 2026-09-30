using BepInEx;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;

namespace EFTNewCamera;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class NewCameraPlugin : BaseUnityPlugin
{
    public const string PluginGuid = "custom.eft.newcamera";
    public const string PluginName = "新镜头";
    public const string PluginVersion = "0.1.0";

    private void Awake()
    {
        ConfigEntry<bool> enabled = Config.Bind("新镜头", "总开关", false,
            "打开后把游戏自己的镜头切到第三人称骨架。");
        NewCameraSwitch.Enabled = enabled.Value;
        enabled.SettingChanged += (_, _) => NewCameraSwitch.Enabled = enabled.Value;
    }

    private void LateUpdate()
    {
        NewCameraSwitch.LateUpdate();
    }
}

internal static class NewCameraSwitch
{
    internal static bool Enabled;

    private static Player? _player;
    private static EPointOfView _original;
    private static bool _applied;

    internal static void LateUpdate()
    {
        if (!Enabled)
        {
            Restore();
            return;
        }

        Player? player = Singleton<GameWorld>.Instance?.MainPlayer;
        if (player == null || !player.IsYourPlayer)
            return;

        if (!ReferenceEquals(_player, player))
        {
            Restore();
            _player = player;
            _original = player.PointOfView;
            _applied = false;
        }

        if (player.PointOfView != EPointOfView.ThirdPerson)
            player.PointOfView = EPointOfView.ThirdPerson;
        _applied = true;
    }

    private static void Restore()
    {
        Player? player = _player;
        bool applied = _applied;
        _player = null;
        _applied = false;
        if (!applied || player == null)
            return;

        if (player.PointOfView != _original)
            player.PointOfView = _original;
    }
}
