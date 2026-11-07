using EFT;
using EFT.InventoryLogic;

namespace EFTNewCamera;

// The gear a worn item's own settings are kept for (a mount's height, tilt and slide; a helmet's tilt): the gear the
// player model is dressed from. In a raid that is the player's own; in the hideout the gear worn, also in the shooting
// range, where the player's own is a copy made anew with ids of its own (HideoutGear), so a setting made there is the
// worn gear's and holds after the range and in a raid.
internal static class EquipmentIds
{
    internal static InventoryEquipment? Worn(Player? player)
    {
        return player?.PlayerBody?.Equipment ?? player?.Inventory?.Equipment;
    }
}
