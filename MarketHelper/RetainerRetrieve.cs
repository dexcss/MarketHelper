using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MarketHelper;

/// <summary>
/// Item Gatherer actions: move a designated item from a retainer to the player, the SAFE way.
/// Uses AgentInventoryContext.OpenForItemSlot (a documented ClientStructs member fn) to open the
/// item's context menu, then selects the retrieve/return entry BY NAME — never a blind positional
/// click, so it can't accidentally trigger a destructive entry.
/// </summary>
public static unsafe class RetainerRetrieve
{
    // Addon sheet rows for the localized context-menu entries.
    //  "Retrieve" (move retainer inventory item -> player) is Addon row 97.
    //  "Return to Inventory" (pull a market listing back to the retainer) is Addon row 98.
    private static string? _retrieve;
    private static string? _returnToInv;

    public static string RetrieveText =>
        _retrieve ??= Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>()?.GetRowOrDefault(97)?.Text.ExtractText() ?? "Retrieve";

    public static string ReturnToInventoryText =>
        _returnToInv ??= Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>()?.GetRowOrDefault(98)?.Text.ExtractText() ?? "Return to Inventory";

    private static bool IsAgentRetainerActive
    {
        get
        {
            var agent = AgentModule.Instance()->GetAgentByInternalId(AgentId.Retainer);
            return agent != null && agent->IsAgentActive();
        }
    }

    /// <summary>Open the context menu for an item slot, owned by the given addon.</summary>
    private static bool OpenContext(InventoryType type, ushort slot, string ownerAddon)
    {
        if (!IsAgentRetainerActive) return false;
        var addon = Addons.GetAddon(ownerAddon);
        if (addon == null) return false;
        var ctx = AgentInventoryContext.Instance();
        if (ctx == null) return false;
        ctx->OpenForItemSlot(type, slot, 0, addon->Id);
        return true;
    }

    /// <summary>Retainer inventory window (needed to retrieve inventory items).</summary>
    public static bool RetainerInventoryReady =>
        Addons.IsReady("InventoryRetainer") || Addons.IsReady("InventoryRetainerLarge");

    private static string InventoryAddonName =>
        Addons.IsReady("InventoryRetainerLarge") ? "InventoryRetainerLarge" : "InventoryRetainer";

    /// <summary>Open the context menu for a retainer INVENTORY item (RetainerPage slot).</summary>
    public static bool OpenInventoryItemContext(InventoryType type, ushort slot)
        => RetainerInventoryReady && OpenContext(type, slot, InventoryAddonName);

    /// <summary>Select "Retrieve" by name on the open context menu (moves item to player bags).</summary>
    public static bool SelectRetrieve()
        => Addons.SelectContextMenuByText(RetrieveText, "Retrieve", "Retrieve All", "Retrieve Item") >= 0;

    /// <summary>The market sell-list window (needed to pull listings back).</summary>
    public static bool RetainerSellListReady => Addons.IsReady("RetainerSellList");

    /// <summary>Open the context menu for a retainer MARKET listing (RetainerMarket slot).</summary>
    public static bool OpenMarketItemContext(InventoryType type, ushort slot)
        => RetainerSellListReady && OpenContext(type, slot, "RetainerSellList");

    /// <summary>Select "Return to Inventory" by name (pulls a listing back to retainer inventory).</summary>
    public static bool SelectReturnToInventory()
        => Addons.SelectContextMenuByText(ReturnToInventoryText, "Return to Inventory", "Return") >= 0;
}
