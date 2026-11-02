using Comfort.Common;
using EFT;
using UnityEngine;

namespace EFTNewCamera;

// The aim stamina drain: the game's runs whenever the weapon is aimed; with this mod, only while aimed with the
// breath held.
internal static class AimStamina
{
    private static Physical? _drainOwner;
    private static bool _aimDrain;

    internal static bool AimPrefix(Physical __instance, float mass)
    {
        Player? player = Singleton<GameWorld>.Instance?.MainPlayer;
        if (player == null || !ReferenceEquals(player.Physical, __instance))
            return true;
        if (!NewCameraSwitch.Enabled)
        {
            SetAimDrain(__instance, false);
            return true;
        }

        __instance._aimMass = mass;
        __instance.Consumptions[Physical.EConsumptionType.Aim].Delta.SetDirty();
        ApplyAimDrain(__instance);
        return false;
    }

    internal static void HoldBreathPostfix(Physical __instance)
    {
        Player? player = Singleton<GameWorld>.Instance?.MainPlayer;
        if (player == null || !ReferenceEquals(player.Physical, __instance) || !NewCameraSwitch.Enabled)
            return;
        ApplyAimDrain(__instance);
    }

    internal static void ApplyAimDrain(Physical physical)
    {
        SetAimDrain(physical, physical._aimMass > 0f && physical.HoldingBreath);
    }

    private static void SetAimDrain(Physical physical, bool active)
    {
        if (!ReferenceEquals(_drainOwner, physical))
        {
            _drainOwner = physical;
            _aimDrain = false;
        }
        if (_aimDrain == active)
            return;

        _aimDrain = active;
        Physical.Consumption consumption = physical.Consumptions[Physical.EConsumptionType.Aim];
        consumption.SetActive(physical, active);
        if (!active)
            physical.Stamina.RemoveConsumption(consumption);
    }
}
