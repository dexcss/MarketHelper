using System;
using System.Collections.Generic;
using System.Linq;
using ECommons.Automation;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MarketHelper;

/// <summary>
/// Wrappers around the retainer sell addons.
///
/// Every value here is verified against real API-15 plugins, not guessed:
///  - Price is set via the typed AddonRetainerSell->AskingPrice->SetValue(int), the same
///    call AllaganMarket uses (RetainerSellOverlayWindow.cs). This is NOT a callback.
///  - Compare Prices = event 4 (confirmed from Marketbuddy's annotated ReceiveEvent captures).
///    Confirm = event 0, matching the working MarketBotty script
///    (SafeCallback("RetainerSell", true, 2, price) then SafeCallback("RetainerSell", true, 0)).
///    An older header note claimed 21 for Confirm; the code has always used 0 and that is correct.
///  - Button clicks/closes use ECommons Callback.Fire(addon, updateState, values...),
///    the same helper AutoRetainer uses throughout its retainer handlers.
/// </summary>
public static unsafe class Addons
{
    public static AtkUnitBase* GetAddon(string name)
    {
        nint addr = Plugin.GameGui.GetAddonByName(name, 1);
        return (AtkUnitBase*)addr;
    }

    public static bool IsReady(string name)
    {
        var a = GetAddon(name);
        return a != null && a->IsVisible && a->IsFullyLoaded();
    }

    /// <summary>Lighter check: addon exists and is visible (doesn't require IsFullyLoaded).</summary>
    public static bool IsVisible(string name)
    {
        var a = GetAddon(name);
        return a != null && a->IsVisible;
    }

    public static bool Exists(string name) => GetAddon(name) != null;

    // ---- RetainerSell: the single-item sell window ----

    public static AddonRetainerSell* GetRetainerSell() => (AddonRetainerSell*)GetAddon("RetainerSell");

    public static string GetOpenItemName()
    {
        var a = GetRetainerSell();
        if (a == null || a->ItemName == null) return string.Empty;
        return CleanText(a->ItemName->NodeText.ToString());
    }

    /// <summary>
    /// Strip the private-use glyphs (HQ symbol, item-type icons at U+E000–U+F8FF) and control
    /// characters the game embeds in node text, leaving clean readable text. Without this, item
    /// names come through as "=H===%==I===&=" style garbage.
    /// </summary>
    public static string CleanText(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c >= '\uE000' && c <= '\uF8FF') continue; // private use area (game glyphs)
            if (char.IsControl(c)) continue;
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    /// <summary>The open item's name exactly as the node holds it, glyphs included (for HQ detection).</summary>
    public static string GetOpenItemNameRaw()
    {
        var a = GetRetainerSell();
        if (a == null || a->ItemName == null) return string.Empty;
        return a->ItemName->NodeText.ToString();
    }

    public static bool GetOpenItemIsHq()
    {
        // The HQ glyph (U+E03C — confirmed against FFXIVClientStructs' own comment and
        // InventoryTools) sits in the private-use range that CleanText strips, so this MUST read
        // the RAW node text. The previous version read the cleaned name, i.e. searched for a
        // character it had just removed: it could never be true, so every HQ item was priced as
        // NQ (and an HQ-only market had the NQ multiplier applied on top).
        return GetOpenItemNameRaw().Contains('\uE03C');
    }

    public static int GetCurrentAskingPrice()
    {
        var a = GetRetainerSell();
        if (a == null || a->AskingPrice == null) return -1;
        return a->AskingPrice->Value;
    }

    /// <summary>Fire the "Compare Prices" button, which populates InfoProxyItemSearch. Event 4.</summary>
    public static void FireComparePrices()
    {
        var a = GetAddon("RetainerSell");
        if (a == null) return;
        Callback.Fire(a, true, 4);
    }

    /// <summary>
    /// Set the asking price on the typed numeric-input field (AllaganMarket's method),
    /// then confirm. Confirm is RetainerSell event 0, matching the working MarketBotty script
    /// (SafeCallback("RetainerSell", true, 2, price) then SafeCallback("RetainerSell", true, 0)).
    /// </summary>
    public static void SetPriceAndConfirm(int price)
    {
        var a = GetRetainerSell();
        if (a == null || a->AskingPrice == null) return;

        // Set price: the script uses callback (2, price); AllaganMarket sets the field directly.
        // We do both for robustness — set the field, then fire the set-price event.
        a->AskingPrice->SetValue(price);
        Callback.Fire(&a->AtkUnitBase, true, 2, price);

        // Read the field back and only confirm when it actually holds the price we asked for.
        // Confirming blind is the one unrecoverable mistake here: if the value did not take, the
        // listing goes out at whatever the field was showing (often the game's suggested price).
        // The quantity path already refuses to confirm on a mismatch; the price path must too.
        var got = a->AskingPrice->Value;
        if (got != price)
        {
            Svc.Chat.Print($"[Market Helper] Price didn't take ({got:N0}g, wanted {price:N0}g) — not confirming. Close the window and retry.");
            CloseAddon("RetainerSell");
            return;
        }
        Callback.Fire(&a->AtkUnitBase, true, 0);
    }

    /// <summary>
    /// Set how many units of the stack to list. Returns the value the field actually holds
    /// afterwards, which the caller MUST check before confirming — a quantity that didn't take
    /// means listing the whole stack at a single-unit price, which is a real loss, not a retry.
    /// </summary>
    public static int SetQuantity(int quantity, int opcode)
    {
        var a = GetRetainerSell();
        if (a == null || a->Quantity == null) return -1;

        a->Quantity->SetValue(quantity);
        try { Callback.Fire(&a->AtkUnitBase, true, opcode, quantity); }
        catch { /* the direct SetValue above is the load-bearing part */ }

        return a->Quantity->Value;
    }

    /// <summary>Quantity currently in the sell window, or -1 if it can't be read.</summary>
    public static int ReadQuantity()
    {
        var a = GetRetainerSell();
        if (a == null || a->Quantity == null) return -1;
        return a->Quantity->Value;
    }

    /// <summary>Units in the stack being listed, per the sell window's own maximum.</summary>
    public static int ReadMaxQuantity()
    {
        var a = GetRetainerSell();
        if (a == null || a->Quantity == null) return -1;
        return a->Quantity->Data.Max;
    }

    /// <summary>Set the asking price without confirming; the user confirms manually.</summary>
    public static void SetPriceOnly(int price)
    {
        var a = GetRetainerSell();
        if (a == null || a->AskingPrice == null) return;
        a->AskingPrice->SetValue(price);
        Callback.Fire(&a->AtkUnitBase, true, 2, price);
    }

    // ---- Navigation callbacks (values verified from the MarketBotty script) ----

    /// <summary>RetainerList: select retainer at sorted index r. Callback (2, r).</summary>
    public static void SelectRetainer(int r)
    {
        var a = GetAddon("RetainerList");
        if (a == null) return;
        Callback.Fire(a, true, 2, r);
    }

    /// <summary>SelectString: choose entry index (3 = "Sell items on the market", 2 = single-retainer path).</summary>
    public static void SelectStringEntry(int index)
    {
        var a = GetAddon("SelectString");
        if (a == null) return;
        Callback.Fire(a, true, index);
    }

    /// <summary>
    /// Select the SelectString entry whose text starts with any of the given strings (case-insensitive).
    /// Returns the matched index, or -1 if none. This is how AutoRetainer exits a retainer — it picks
    /// the "Quit" entry by name rather than firing a cancel, which doesn't reliably return to the list.
    /// </summary>
    public static int SelectStringEntryByText(params string[] wanted)
    {
        var addon = (AddonSelectString*)GetAddon("SelectString");
        if (addon == null) return -1;
        ref var menu = ref addon->PopupMenu.PopupMenu;
        var count = menu.EntryCount;
        for (var i = 0; i < count; i++)
        {
            var ptr = menu.EntryNames[i];
            if (!ptr.HasValue) continue;
            var text = ptr.ToString();
            if (string.IsNullOrEmpty(text)) continue;
            foreach (var w in wanted)
            {
                if (text.StartsWith(w, StringComparison.OrdinalIgnoreCase))
                {
                    Callback.Fire(&addon->AtkUnitBase, true, i);
                    return i;
                }
            }
        }
        return -1;
    }

    /// <summary>The localised "Quit" entry text from the Addon sheet (row 2383), as AutoRetainer uses.</summary>
    public static string QuitText()
    {
        var row = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>()?.GetRowOrDefault(2383);
        return row?.Text.ExtractText() ?? "Quit";
    }

    /// <summary>Select "Quit" on the retainer SelectString menu to return to the retainer list.</summary>
    public static bool QuitRetainer()
    {
        if (!IsVisible("SelectString")) return false;
        return SelectStringEntryByText(QuitText(), "Quit") >= 0;
    }

    /// <summary>Addon sheet row 2378 = "Entrust or withdraw items." — opens the retainer inventory.</summary>
    public static string EntrustItemsText()
    {
        var row = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>()?.GetRowOrDefault(2378);
        return row?.Text.ExtractText() ?? "Entrust or withdraw items.";
    }

    /// <summary>Open the retainer inventory via the SelectString menu (needed before listing items).</summary>
    public static bool OpenRetainerInventory()
    {
        if (!IsVisible("SelectString")) return false;
        return SelectStringEntryByText(EntrustItemsText(), "Entrust or withdraw items") >= 0;
    }

    /// <summary>Addon sheet row 2381 = "Sell items on the market." — opens RetainerSellList.</summary>
    public static string SellOnMarketText()
    {
        var row = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>()?.GetRowOrDefault(2381);
        return row?.Text.ExtractText() ?? "Sell items on the market.";
    }

    /// <summary>Open the "Sell items on the market" screen (RetainerSellList) from the SelectString menu.</summary>
    public static bool OpenSellOnMarket()
    {
        if (!IsVisible("SelectString")) return false;
        // Prefer name match (locale-safe); fall back to the known index 3.
        var idx = SelectStringEntryByText(SellOnMarketText(), "Sell items on the market");
        if (idx >= 0) return true;
        SelectStringEntry(3);
        return true;
    }

    /// <summary>
    /// True if the market search results window is showing the game's rate-limit message
    /// ("Please wait and try your search again."), which means the search FAILED — the empty
    /// result is NOT a genuinely empty market. Read from the ItemSearchResult addon's message node.
    ///
    /// Localised: Addon sheet rows 1997 and 1998 ARE that message (verified by dumping the
    /// installed client's Addon sheet with work\sheetdump — row 1946 is "No matching items.",
    /// 1997/1998 are the throttle). Row ids are locale-independent and the sheet's Text field is
    /// the client's language, so matching the visible node text against those rows detects the
    /// throttle on any client. The historical English substring match is kept as a backstop.
    /// </summary>
    private static HashSet<string>? _throttleTexts;

    private static HashSet<string> ThrottleTexts()
    {
        if (_throttleTexts != null) return _throttleTexts;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Please wait and try your search again.",
        };
        var sheet = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>();
        if (sheet != null)
        {
            foreach (var rowId in new[] { 1997u, 1998u })
            {
                var t = sheet.GetRowOrDefault(rowId)?.Text.ExtractText();
                if (!string.IsNullOrWhiteSpace(t)) set.Add(t.Trim());
            }
        }
        _throttleTexts = set;
        return set;
    }

    public static unsafe bool MarketSearchThrottled()
    {
        var addon = GetAddon("ItemSearchResult");
        if (addon == null) return false;
        var texts = ThrottleTexts();
        // The "please wait" message is shown in a text node when the list is empty due to throttle.
        // Scan visible text nodes for the known phrase (locale-tolerant: match a stable substring).
        for (uint id = 1; id <= 60; id++)
        {
            var node = addon->UldManager.SearchNodeById(id);
            if (node == null || node->Type != NodeType.Text) continue;
            var tn = (AtkTextNode*)node;
            if (!tn->AtkResNode.IsVisible()) continue;
            var text = tn->NodeText.ToString();
            if (string.IsNullOrEmpty(text)) continue;
            if (texts.Contains(text.Trim())) return true;
            // Backstop for an unexpected phrasing on some locale: the English fragment.
            if (text.Contains("try your search again", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Please wait and try", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
    /// AddonMaster.ContextMenu (same as AutoRetainer) so it reads real entry text and selects by
    /// name — it never blind-clicks a positional index, so it can't hit a destructive entry.
    /// </summary>
    public static int SelectContextMenuByText(params string[] wanted)
    {
        if (!ECommons.GenericHelpers.TryGetAddonMaster<ECommons.UIHelpers.AddonMasterImplementations.AddonMaster.ContextMenu>(out var m) || !m.IsAddonReady)
            return -1;
        var entries = m.Entries;
        // Pass 1: exact (trimmed, case-insensitive) match.
        for (var i = 0; i < entries.Length; i++)
        {
            var e = entries[i];
            if (!e.Enabled) continue;
            var text = (e.Text ?? string.Empty).Trim();
            foreach (var w in wanted)
                if (!string.IsNullOrEmpty(w) && text.Equals(w.Trim(), StringComparison.OrdinalIgnoreCase))
                { e.Select(); return i; }
        }
        // Pass 2: contains match (handles trailing punctuation / decorations).
        for (var i = 0; i < entries.Length; i++)
        {
            var e = entries[i];
            if (!e.Enabled) continue;
            var text = (e.Text ?? string.Empty).Trim();
            foreach (var w in wanted)
                if (!string.IsNullOrEmpty(w) && text.Contains(w.Trim(), StringComparison.OrdinalIgnoreCase))
                { e.Select(); return i; }
        }
        return -1;
    }

    /// <summary>Debug: list the visible context menu entries (for diagnosing name matching).</summary>
    public static string DumpContextMenu()
    {
        if (!ECommons.GenericHelpers.TryGetAddonMaster<ECommons.UIHelpers.AddonMasterImplementations.AddonMaster.ContextMenu>(out var m) || !m.IsAddonReady)
            return "(no context menu)";
        var entries = m.Entries;
        var parts = new System.Collections.Generic.List<string>();
        foreach (var e in entries)
            parts.Add($"'{(e.Text ?? "").Trim()}'{(e.Enabled ? "" : "(disabled)")}");
        return string.Join(", ", parts);
    }

    /// <summary>RetainerSellList: open the item at slot (0-based). Callback (0, slot, 1).</summary>
    public static void OpenSellListItem(int slot)
    {
        var a = GetAddon("RetainerSellList");
        if (a == null) return;
        Callback.Fire(a, true, 0, slot, 1);
    }

    /// <summary>Addon sheet row 959 = "Adjust Price" — the RetainerSellList context-menu entry.</summary>
    public static string AdjustPriceText()
    {
        var row = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>()?.GetRowOrDefault(959);
        return row?.Text.ExtractText() ?? "Adjust Price";
    }

    /// <summary>
    /// RetainerSellList context menu: open the price editor. Selects the "Adjust Price" entry BY
    /// NAME, and only falls back to the historical positional (0,0) if that name isn't present —
    /// so a drifted or unexpected menu can never blind-click a destructive entry at index 0.
    /// </summary>
    public static void ContextMenuFirst()
    {
        var a = GetAddon("ContextMenu");
        if (a == null) return;
        if (SelectContextMenuByText(AdjustPriceText(), "Adjust Price") >= 0) return;
        Callback.Fire(a, true, 0, 0);
    }

    /// <summary>Advance the retainer greeting Talk dialogue if present. Script: SafeCallback("Talk", true).</summary>
    public static bool AdvanceTalk()
    {
        var a = GetAddon("Talk");
        if (a == null || !a->IsVisible) return false;
        Callback.Fire(a, true);
        return true;
    }

    /// <summary>
    /// Resolve a nested node by walking component children by node id, mirroring the SND
    /// GetNode(addon, path...) convention. The first id is a node in the addon's root UldManager;
    /// each subsequent id is looked up inside the previous node's component UldManager.
    /// Returns null if any hop is missing.
    /// </summary>
    private static AtkResNode* ResolveNode(AtkUnitBase* addon, params uint[] path)
    {
        if (addon == null || path.Length == 0) return null;
        var node = addon->UldManager.SearchNodeById(path[0]);
        for (var i = 1; i < path.Length && node != null; i++)
        {
            var comp = node->GetComponent();
            if (comp == null) return null;
            node = comp->UldManager.SearchNodeById(path[i]);
        }
        return node;
    }

    /// <summary>
    /// Whether the market-board mannequin icon is visible on the given RetainerSellList row.
    /// Row node id is 5 for the first item, then 51001, 51002, ... for subsequent rows.
    /// Path (per SND convention): RetainerSellList -> 11 (list) -> rowId -> 9 -> 11 (icon).
    /// This is the game's own display flag for "this listing is on a mannequin".
    ///
    /// Returns NULL when the answer cannot be determined: the row has no node (the list only
    /// renders the rows that exist — a retainer holding more items than the list draws has rows
    /// that simply are not there), or the structure has drifted. Callers MUST treat null as
    /// "unknown — do not open". Returning false here was a fail-open: a mannequin row beyond the
    /// rendered set reported "not a mannequin" and got opened, which can delist it.
    /// </summary>
    public static bool? IsSellListRowMannequin(int rowIndex)
    {
        var addon = GetAddon("RetainerSellList");
        if (addon == null) return null;
        var list = addon->UldManager.SearchNodeById(11);
        if (list == null) return null;
        var comp = list->GetComponent();
        if (comp == null) return null;
        var listComp = (AtkComponentList*)comp;
        if (rowIndex < 0 || rowIndex >= listComp->GetItemCount()) return null;
        var renderer = listComp->GetItemRenderer(rowIndex);
        if (renderer == null) return null;
        // Node 11 (type 2) inside the row renderer is the mannequin icon — Visible on mannequin
        // items, hidden otherwise (confirmed by diffing mannequin vs non-mannequin row dumps).
        var icon = renderer->AtkComponentButton.AtkComponentBase.UldManager.SearchNodeById(11);
        if (icon == null) return null;   // structure drift: we cannot verify, so we cannot open
        return icon->IsVisible();
    }

    /// <summary>
    /// Diagnostic: dump the node tree of a RetainerSellList row so we can find the real mannequin
    /// icon node id. Prints each descendant node's id, type, and visibility.
    /// </summary>
    public static string DumpSellListRow(int rowIndex)
    {
        var addon = GetAddon("RetainerSellList");
        if (addon == null) return "RetainerSellList not open";
        var list = addon->UldManager.SearchNodeById(11);
        if (list == null) return "node 11 (list) not found";
        var listComp = list->GetComponent();
        if (listComp == null) return "node 11 has no component";

        // For rowIndex -1, dump the LIST's direct children (to find real row node ids).
        if (rowIndex < 0)
        {
            var sbl = new System.Text.StringBuilder("list children: ");
            ref var lu = ref listComp->UldManager;
            for (var i = 0; i < lu.NodeListCount; i++)
            {
                var n = lu.NodeList[i];
                if (n == null) continue;
                var hasComp = n->GetComponent() != null ? "C" : " ";
                var vis = n->IsVisible() ? "V" : "-";
                sbl.Append($"[id={n->NodeId} t={(int)n->Type} {hasComp}{vis}] ");
            }
            return sbl.ToString();
        }

        var rowId = rowIndex == 0 ? 5u : (uint)(51000 + rowIndex);
        var row = listComp->UldManager.SearchNodeById(rowId);
        if (row == null) return $"row node {rowId} not found (list has {listComp->UldManager.NodeListCount} nodes)";
        var rowComp = row->GetComponent();
        var sb = new System.Text.StringBuilder();
        sb.Append($"row {rowId}: ");
        if (rowComp == null) { sb.Append("no component; "); return sb.ToString(); }
        var uld = rowComp->UldManager;
        for (var i = 0; i < uld.NodeListCount; i++)
        {
            var n = uld.NodeList[i];
            if (n == null) continue;
            var vis = n->IsVisible() ? "V" : "-";
            sb.Append($"[id={n->NodeId} t={(int)n->Type} {vis}] ");
        }
        return sb.ToString();
    }

    private static void WalkUld(AtkUldManager* uld, List<(uint id, int type, bool vis)> into)
    {
        for (var i = 0; i < uld->NodeListCount; i++)
        {
            var n = uld->NodeList[i];
            if (n == null) continue;
            into.Add((n->NodeId, (int)n->Type, n->IsVisible()));
            var comp = n->GetComponent();
            if (comp != null) WalkUld(&comp->UldManager, into);
        }
    }

    /// <summary>
    /// Diagnostic: for every rendered RetainerSellList row, walk the row's full node subtree and
    /// report each node's id/type, laid out against row 0's structure, with per-row visibility.
    /// A mannequin row differs from the others on exactly one node id — that id is the icon.
    /// </summary>
    public static List<string> DumpSellListRows()
    {
        var lines = new List<string>();
        var addon = GetAddon("RetainerSellList");
        if (addon == null) { lines.Add("RetainerSellList not open"); return lines; }
        var list = addon->UldManager.SearchNodeById(11);
        if (list == null) { lines.Add("node 11 (list) not found"); return lines; }
        var listComp = (AtkComponentList*)list->GetComponent();
        if (listComp == null) { lines.Add("node 11 has no component"); return lines; }

        var count = listComp->GetItemCount();
        lines.Add($"rendered rows: {count}");

        List<(uint id, int type, bool vis)>? layout = null;
        for (var r = 0; r < count && r < 20; r++)
        {
            var renderer = listComp->GetItemRenderer(r);
            if (renderer == null) { lines.Add($"  row {r}: <no renderer>"); continue; }

            var nodes = new List<(uint id, int type, bool vis)>();
            WalkUld(&renderer->AtkComponentButton.AtkComponentBase.UldManager, nodes);

            if (layout == null)
            {
                layout = nodes;
                lines.Add("  layout (row 0): " + string.Join(" ", nodes.Select(n => $"[{n.id}:{n.type}]")));
            }

            var parts = layout.Select(l =>
            {
                var m = nodes.FirstOrDefault(n => n.id == l.id);
                return $"{l.id}:{(m.id == l.id ? (m.vis ? "V" : "-") : "?")}";
            });
            lines.Add($"  row {r}: {string.Join(" ", parts)}");
        }
        return lines;
    }

    public static void CloseAddon(string name)
    {
        var a = GetAddon(name);
        if (a == null) return;
        // -1 = cancel/close, the pattern AutoRetainer uses to dismiss addons.
        Callback.Fire(a, true, -1);
    }

    public static void CloseSearchWindows()
    {
        CloseAddon("ItemHistory");
        CloseAddon("ItemSearchResult");
    }
}
