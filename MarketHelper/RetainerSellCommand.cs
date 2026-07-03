using System;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MarketHelper;

/// <summary>
/// Opens the market-board "add listing" flow for an inventory item the SAFE way: it invokes the
/// inventory context menu for the slot (AgentInventoryContext.OpenForItemSlot — a documented
/// ClientStructs member function, NOT a raw signature scan), then selects the "Put Up for Sale"
/// entry BY NAME. Because the entry is chosen by its localized text, it can never accidentally hit
/// "Sell" (vendor) or any other action — if "Put Up for Sale" isn't present, nothing happens.
/// </summary>
public static unsafe class RetainerSellCommand
{
    // Addon sheet row 99 = "Put Up for Sale".
    private static string? _putUpForSale;
    public static string PutUpForSaleText =>
        _putUpForSale ??= Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>()?.GetRowOrDefault(99)?.Text.ExtractText() ?? "Put Up for Sale";

    /// <summary>The retainer sell-list window must be open to list new items.</summary>
    public static bool RetainerSellListReady => Addons.IsReady("RetainerSellList");

    private static bool IsAgentRetainerActive
    {
        get
        {
            var agent = AgentModule.Instance()->GetAgentByInternalId(AgentId.Retainer);
            return agent != null && agent->IsAgentActive();
        }
    }

    /// <summary>Always available — this path uses documented member functions, no signature scan.</summary>
    public static bool Available => true;

    /// <summary>
    /// Step 1: open the inventory context menu for the given slot. Returns false if preconditions
    /// aren't met. The caller then waits for ContextMenu and calls SelectPutUpForSale().
    /// </summary>
    public static bool OpenItemContext(InventoryType inventoryType, ushort slot)
    {
        if (!IsAgentRetainerActive) return false;
        if (!RetainerSellListReady) return false;
        var ctx = AgentInventoryContext.Instance();
        if (ctx == null) return false;
        var sellList = Addons.GetAddon("RetainerSellList");
        if (sellList == null) return false;
        var addonId = sellList->Id;
        ctx->OpenForItemSlot(inventoryType, slot, 0, addonId);
        return true;
    }

    /// <summary>
    /// Step 2: on the open ContextMenu, select the "Put Up for Sale" entry by name. Returns true
    /// if selected. Never clicks anything else — if the entry isn't found, returns false.
    /// </summary>
    public static bool SelectPutUpForSale()
    {
        return Addons.SelectContextMenuByText(PutUpForSaleText, "Put Up for Sale") >= 0;
    }
}
