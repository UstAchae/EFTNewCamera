using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;

namespace EFTNewCamera;

// The id a worn item's own settings are kept under (a mount's height, tilt and slide; a helmet's tilt). In the hideout's
// shooting range the player wears copies of their gear, made anew with ids of their own (HideoutPlayer's range inventory,
// CloneItem): a setting made there is kept under the item it was copied from, found at the same place in the same slot,
// so the gear keeps it after the range and in a raid. Only when settings are read or written: nothing per frame.
internal static class EquipmentIds
{
    internal static string Kept(Item item, EquipmentSlot slot)
    {
        Player? player = Singleton<GameWorld>.Instance?.MainPlayer;
        if (player is HideoutPlayer hideout && hideout.OriginalInventory?.Inventory?.Equipment is InventoryEquipment original
            && player.Inventory?.Equipment is InventoryEquipment worn)
        {
            Item? wornTop = worn.GetSlot(slot).ContainedItem, originalTop = original.GetSlot(slot).ContainedItem;
            if (wornTop != null && originalTop != null && wornTop.Id != originalTop.Id && wornTop.TemplateId == originalTop.TemplateId)
            {
                List<Item> wornItems = wornTop.GetAllItems().ToList(), originalItems = originalTop.GetAllItems().ToList();
                int at = wornItems.IndexOf(item);
                if (at >= 0 && at < originalItems.Count && originalItems[at].TemplateId == item.TemplateId)
                {
                    NewCameraPlugin.Log?.LogInfo($"Shooting range: {item.ShortName?.Localized() ?? item.TemplateId.ToString()} kept under the gear it was copied from ({originalItems[at].Id})");
                    return originalItems[at].Id.ToString();
                }
            }
        }
        return item.Id.ToString();
    }
}
