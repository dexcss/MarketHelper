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

    /// <summary>
    /// Like <see cref="FindItemSlot"/> but searches ONLY the open retainer's pages. The Lister must
    /// use this: a market listing is made from retainer stock, and searching the player's four bags
    /// first meant that carrying the same item yourself made the Lister target your bag slot (where
    /// "Put Up for Sale" is not offered) and kept reporting "more stacks remain" forever.
    /// </summary>
    public static (InventoryType Type, ushort Slot)? FindRetainerItemSlot(uint itemId)
    {
        var im = InventoryManager.Instance();
        if (im == null) return null;

        var containers = new List<InventoryType>
        {
            InventoryType.RetainerPage1, InventoryType.RetainerPage2,
            InventoryType.RetainerPage3, InventoryType.RetainerPage4,
            InventoryType.RetainerPage5, InventoryType.RetainerPage6,
            InventoryType.RetainerPage7,
        };

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

    /// <summary>
    /// Total units of an item held across the player's four bags (and optionally the open
    /// retainer's pages). Counts QUANTITY, not slots — a stack of 18 counts as 18, which is what
    /// the Lister needs to show "listing 20 /20".
    ///
    /// Only containers the game has actually loaded are counted, so with no retainer open this
    /// reports bags alone rather than guessing.
    /// </summary>
    public static int CountItem(uint itemId, bool includeRetainer)
    {
        var im = InventoryManager.Instance();
        if (im == null) return 0;

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

        var total = 0;
        foreach (var type in containers)
        {
            var inv = im->GetInventoryContainer(type);
            if (inv == null || !inv->IsLoaded) continue;
            for (var i = 0; i < inv->Size; i++)
            {
                var slot = inv->GetInventorySlot(i);
                if (slot == null) continue;
                if (slot->ItemId == itemId) total += (int)slot->Quantity;
            }
        }
        return total;
    }

    /// <summary>Debug: summarize loaded RetainerPage containers and their item ids.</summary>
    public static string DebugCountRetainerInventory()
    {
        var im = InventoryManager.Instance();
        if (im == null) return "no InventoryManager";
        var parts = new List<string>();
        var pages = new[] { InventoryType.RetainerPage1, InventoryType.RetainerPage2,
                            InventoryType.RetainerPage3, InventoryType.RetainerPage4,
                            InventoryType.RetainerPage5, InventoryType.RetainerPage6,
                            InventoryType.RetainerPage7 };
        var total = 0;
        var loaded = 0;
        foreach (var type in pages)
        {
            var inv = im->GetInventoryContainer(type);
            if (inv == null) continue;
            if (!inv->IsLoaded) continue;
            loaded++;
            for (var i = 0; i < inv->Size; i++)
            {
                var slot = inv->GetInventorySlot(i);
                if (slot != null && slot->Quantity > 0) total++;
            }
        }
        return $"{loaded} page(s) loaded, {total} filled slot(s)";
    }

    /// <summary>
    /// Find the first slot in the PLAYER's inventory (Inventory1-4) holding one of the given item
    /// ids. Returns (container, slot, itemId) or null. Used by manual deposit/entrust.
    /// </summary>
    public static (InventoryType Type, ushort Slot, uint ItemId)? FindPlayerInventoryItem(HashSet<uint> wanted)
    {
        var im = InventoryManager.Instance();
        if (im == null) return null;
        foreach (var type in new[] { InventoryType.Inventory1, InventoryType.Inventory2,
                                     InventoryType.Inventory3, InventoryType.Inventory4 })
        {
            var inv = im->GetInventoryContainer(type);
            if (inv == null || !inv->IsLoaded) continue;
            for (var i = 0; i < inv->Size; i++)
            {
                var slot = inv->GetInventorySlot(i);
                if (slot == null || slot->Quantity == 0) continue;
                if (wanted.Contains(slot->ItemId))
                    return (type, (ushort)i, slot->ItemId);
            }
        }
        return null;
    }

    // ---- Item Gatherer helpers ----

    /// <summary>True if the given container slot currently holds the given item id (quantity &gt; 0).</summary>
    public static bool SlotHasItem(InventoryType type, ushort slot, uint itemId)
    {
        var im = InventoryManager.Instance();
        if (im == null) return false;
        var inv = im->GetInventoryContainer(type);
        if (inv == null || !inv->IsLoaded || slot >= inv->Size) return false;
        var s = inv->GetInventorySlot(slot);
        return s != null && s->ItemId == itemId && s->Quantity > 0;
    }

    /// <summary>Number of free slots across the player's four main inventory bags.</summary>
    public static int FreePlayerBagSlots()
    {
        var im = InventoryManager.Instance();
        if (im == null) return -1;   // unknown, not "full"
        var free = 0;
        var anyLoaded = false;
        foreach (var type in new[] { InventoryType.Inventory1, InventoryType.Inventory2,
                                     InventoryType.Inventory3, InventoryType.Inventory4 })
        {
            var inv = im->GetInventoryContainer(type);
            if (inv == null || !inv->IsLoaded) continue;
            anyLoaded = true;
            for (var i = 0; i < inv->Size; i++)
            {
                var slot = inv->GetInventorySlot(i);
                if (slot == null || slot->ItemId == 0 || slot->Quantity == 0) free++;
            }
        }
        // If NONE of the bags were readable, we can't tell — return -1 (unknown), never 0 (full),
        // so a transient not-loaded state during retainer transitions doesn't look like "bags full".
        return anyLoaded ? free : -1;
    }

    /// <summary>True only when bags are CONFIRMED full (readable and zero free). Unknown =&gt; false.</summary>
    public static bool PlayerBagsFull()
    {
        var free = FreePlayerBagSlots();
        return free == 0;   // -1 (unknown) is NOT full
    }

    /// <summary>
    /// Find the first slot in the retainer's INVENTORY (RetainerPage1-7) holding one of the given
    /// item ids. Returns (container, slot, itemId) or null if none present.
    /// </summary>
    public static (InventoryType Type, ushort Slot, uint ItemId)? FindRetainerInventoryItem(HashSet<uint> wanted)
    {
        var im = InventoryManager.Instance();
        if (im == null) return null;
        foreach (var type in new[] { InventoryType.RetainerPage1, InventoryType.RetainerPage2,
                                     InventoryType.RetainerPage3, InventoryType.RetainerPage4,
                                     InventoryType.RetainerPage5, InventoryType.RetainerPage6,
                                     InventoryType.RetainerPage7 })
        {
            var inv = im->GetInventoryContainer(type);
            if (inv == null || !inv->IsLoaded) continue;
            for (var i = 0; i < inv->Size; i++)
            {
                var slot = inv->GetInventorySlot(i);
                if (slot == null || slot->Quantity == 0) continue;
                if (wanted.Contains(slot->ItemId))
                    return (type, (ushort)i, slot->ItemId);
            }
        }
        return null;
    }

    /// <summary>
    /// Find the first slot in the retainer's MARKET listings (RetainerMarket) holding one of the
    /// given item ids. Returns (container, slot, itemId) or null.
    /// </summary>
    public static (InventoryType Type, ushort Slot, uint ItemId)? FindRetainerMarketItem(HashSet<uint> wanted)
    {
        var im = InventoryManager.Instance();
        if (im == null) return null;
        var inv = im->GetInventoryContainer(InventoryType.RetainerMarket);
        if (inv == null || !inv->IsLoaded) return null;
        for (var i = 0; i < inv->Size; i++)
        {
            var slot = inv->GetInventorySlot(i);
            if (slot == null || slot->Quantity == 0) continue;
            if (wanted.Contains(slot->ItemId))
                return (InventoryType.RetainerMarket, (ushort)i, slot->ItemId);
        }
        return null;
    }
}
