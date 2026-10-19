using System;
using BepInEx.Configuration;

namespace EFTNewCamera;

internal static class OpticTest
{
    private static ConfigEntry<bool> _enabled = null!;
    internal static event Action? Changed;
    internal static bool Enabled => _enabled != null && _enabled.Value;

    internal static void Initialize(ConfigFile config)
    {
        _enabled = config.Bind("光学密位测试", "测试总开关", false,
            "统一开关A/B类所有测试提示框（含密位数值与近远交点读数）、黄线弹道、红色终点、紫色瞄准射线、绿色交点和双探针记录，默认关闭。关闭后隐藏提示并停止测试绘制和探针采集；密位调节继续生效。");
        _enabled.SettingChanged += (_, _) => Changed?.Invoke();
    }
}
