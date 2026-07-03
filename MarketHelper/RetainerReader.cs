using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace MarketHelper;

/// <summary>
/// Reads retainer roster and per-retainer item counts from RetainerManager, instead of
/// scraping RetainerList nodes like the script did. Cleaner and less patch-fragile.
/// </summary>
public static unsafe class RetainerReader
{
    public static bool Ready
    {
        get
        {
            var rm = RetainerManager.Instance();
            return rm != null && rm->IsReady;
        }
    }

    public static int Count
    {
        get
        {
            var rm = RetainerManager.Instance();
            return rm == null ? 0 : rm->GetRetainerCount();
        }
    }

    /// <summary>Name of the retainer at sorted index i (matches RetainerList order).</summary>
    public static string NameAtSorted(int i)
    {
        var rm = RetainerManager.Instance();
        if (rm == null) return string.Empty;
        var r = rm->GetRetainerBySortedIndex((uint)i);
        return r == null ? string.Empty : r->NameString;
    }

    /// <summary>Market-listed item count for the retainer at sorted index i.</summary>
    public static int MarketItemsAtSorted(int i)
    {
        var rm = RetainerManager.Instance();
        if (rm == null) return 0;
        var r = rm->GetRetainerBySortedIndex((uint)i);
        return r == null ? 0 : r->MarketItemCount;
    }

    /// <summary>Item count on the currently active (open) retainer.</summary>
    public static int ActiveMarketItems()
    {
        var rm = RetainerManager.Instance();
        if (rm == null) return 0;
        var r = rm->GetActiveRetainer();
        return r == null ? 0 : r->MarketItemCount;
    }

    /// <summary>
    /// Current asking price of a retainer market slot, read directly from InventoryManager
    /// WITHOUT opening the item (used by the mannequin-safety guard). Returns 0 if unavailable.
    /// </summary>
    public static ulong MarketPriceAtSlot(int slot)
    {
        var im = InventoryManager.Instance();
        if (im == null) return 0;
        return im->GetRetainerMarketPrice((short)slot);
    }

    /// <summary>
    /// Locate an item by id across the player's inventory and (when a retainer is open) the
    /// retainer's inventory. Returns the container type and slot, or null if not found.
    /// </summary>
    public static (InventoryType Type, ushort Slot)? FindItemSlot(uint itemId, bool includeRetainer)
    {
        var im = InventoryManager.Instance();
        if (im == null) return null;

        var containers = new List<InventoryType>
        {
            InventoryType.Inventory1, InventoryType.Inventory2,
            InventoryType.Inventory3, InventoryType.Inventory4,
        };
        if (includeRetainer)
        {
            containers.Add(InventoryType.RetainerPage1);
            containers.Add(InventoryType.RetainerPage2);
            containers.Add(InventoryType.RetainerPage3);
            containers.Add(InventoryType.RetainerPage4);
            containers.Add(InventoryType.RetainerPage5);
            containers.Add(InventoryType.RetainerPage6);
            containers.Add(InventoryType.RetainerPage7);
        }

        foreach (var type in containers)
        {
            var inv = im->GetInventoryContainer(type);
            if (inv == null || !inv->IsLoaded) continue;
            for (var i = 0; i < inv->Size; i++)
            {
                var slot = inv->GetInventorySlot(i);
                if (slot == null) continue;
                if (slot->ItemId == itemId && slot->Quantity > 0)
                    return (type, (ushort)i);
            }
        }
        return null;
    }
}
