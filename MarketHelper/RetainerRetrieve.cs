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
    // Addon sheet rows for the localized context-menu entries. Row ids are locale-independent and
    // the TEXT is the client's language. Verified against the game's own Addon sheet:
    //   97  "Entrust to Retainer"        player item -> retainer          (deposit)
    //   98  "Retrieve from Retainer"     retainer inventory -> player     (withdraw)
    //   99  "Put Up for Sale"
    //   958 "Return to Retainer"         market listing -> retainer inventory
    //   976 "Return Items to Inventory"  market listing -> player inventory
    // An earlier build had 97/98 the wrong way round, which silently broke retrieval on any
    // non-English client (the English fallbacks cannot match a localized menu) and left the
    // listing-return matching on a loose "Return" substring.
    private static string? _retrieve;
    private static string? _returnToInv;

    public static string RetrieveText =>
        _retrieve ??= Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>()?.GetRowOrDefault(98)?.Text.ExtractText() ?? "Retrieve from Retainer";

    public static string ReturnToInventoryText =>
        _returnToInv ??= Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>()?.GetRowOrDefault(958)?.Text.ExtractText() ?? "Return to Retainer";

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

    /// <summary>Public accessor for the open retainer-inventory addon name (for deposit context).</summary>
    public static string InventoryAddonNamePublic => InventoryAddonName;

    /// <summary>Open the context menu for a retainer INVENTORY item (RetainerPage slot).</summary>
    public static bool OpenInventoryItemContext(InventoryType type, ushort slot)
        => RetainerInventoryReady && OpenContext(type, slot, InventoryAddonName);

    /// <summary>Select "Retrieve" by name on the open context menu (moves item to player bags).</summary>
    public static bool SelectRetrieve()
        => Addons.SelectContextMenuByText(RetrieveText, "Retrieve from Retainer", "Retrieve All", "Retrieve Item", "Retrieve") >= 0;

    /// <summary>The market sell-list window (needed to pull listings back).</summary>
    public static bool RetainerSellListReady => Addons.IsReady("RetainerSellList");

    /// <summary>Open the context menu for a retainer MARKET listing (RetainerMarket slot).</summary>
    public static bool OpenMarketItemContext(InventoryType type, ushort slot)
        => RetainerSellListReady && OpenContext(type, slot, "RetainerSellList");

    /// <summary>Select "Return to Retainer" by name (pulls a listing back to retainer inventory).</summary>
    ///
    /// Matches the precise rows only. The old "Return" substring matched whichever return entry the
    /// menu happened to list first, so it could send the listing to the player instead of the
    /// retainer and wedge the gatherer's next step.
    public static bool SelectReturnToInventory()
        => Addons.SelectContextMenuByText(ReturnToInventoryText, "Return to Retainer", "Return Items to Inventory") >= 0;

    // ---- Deposit / entrust (player inventory -> retainer) ----

    /// <summary>Open the context menu for a PLAYER inventory item slot (for entrust/deposit).</summary>
    public static bool OpenPlayerItemContext(InventoryType type, ushort slot, string ownerAddon)
        => OpenContext(type, slot, ownerAddon);

    /// <summary>Select "Entrust to Retainer" by name (moves a player item into the retainer).</summary>
    public static bool SelectEntrustToRetainer()
        => Addons.SelectContextMenuByText("Entrust to Retainer", "Entrust", "Entrust All", "Entrust Item") >= 0;
}
