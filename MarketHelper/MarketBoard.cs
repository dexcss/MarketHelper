using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons;
using ECommons.Automation;
using ECommons.DalamudServices;
using ECommons.GameFunctions;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MarketHelper;

/// <summary>
/// Market-board discovery, searching, listing reads and purchase clicks for the Buyer tab.
///
/// SAFETY MODEL — read this before changing anything here.
///
/// Unlike the retainer side of this plugin, we cannot select a purchase by NAME from a context
/// menu; the board's results are a positional list. So the safety net is moved one step later:
/// every purchase produces a SelectYesno confirmation that states the item and the total gil, and
/// <see cref="ReadConfirmText"/> + BuyRunner verify BOTH against what we intended before answering
/// Yes. If the dialog doesn't match — wrong item, price above the cap, unparseable — we answer NO
/// and abort the run. That means a wrong row click costs nothing: it either does nothing, or it
/// raises a dialog we refuse.
///
/// The two callback opcodes below are the only values in this file that are not verified against
/// a struct definition. They are exposed in config (BuyerSelectResultOpcode / BuyerBuyOpcode) so
/// they can be corrected live from the "/undercut buydump" output without a rebuild.
/// </summary>
public static unsafe class MarketBoard
{
    // ---- Board object discovery -------------------------------------------------------------

    // Market boards are EventObj entities. Names per client language, plus a user override.
    private static readonly string[] DefaultBoardNames =
    {
        "Market Board",          // EN
        "マーケットボード",        // JP
        "Marktbrett",            // DE
        "Panneau du marché",     // FR
    };

    public static bool IsBoard(IGameObject? o, string overrideName)
    {
        if (o == null) return false;
        if (o.ObjectKind != ObjectKind.EventObj && o.ObjectKind != ObjectKind.HousingEventObject)
            return false;
        var name = o.Name.ToString();
        if (!string.IsNullOrWhiteSpace(overrideName)
            && name.Equals(overrideName.Trim(), StringComparison.OrdinalIgnoreCase))
            return true;
        return DefaultBoardNames.Any(n => name.Equals(n, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Nearest targetable market board, or null. Distance is set even when null is returned.</summary>
    public static IGameObject? GetNearest(string overrideName, out float distance)
    {
        distance = float.MaxValue;
        IGameObject? nearest = null;
        if (!Player.Available) return null;
        var me = Player.Object.Position;

        foreach (var o in Svc.Objects)
        {
            if (!o.IsTargetable) continue;
            if (!IsBoard(o, overrideName)) continue;
            var d = Vector3.Distance(me, o.Position);
            if (d < distance) { distance = d; nearest = o; }
        }
        return nearest;
    }

    public static bool Interact(IGameObject board)
    {
        if (board == null) return false;
        TargetSystem.Instance()->InteractWithObject(board.Struct(), false);
        return true;
    }

    // ---- Addons ------------------------------------------------------------------------------

    public static AddonItemSearch* GetItemSearch() => (AddonItemSearch*)Addons.GetAddon("ItemSearch");
    public static AddonItemSearchResult* GetItemSearchResult() => (AddonItemSearchResult*)Addons.GetAddon("ItemSearchResult");

    public static bool BoardOpen => Addons.IsVisible("ItemSearch");
    public static bool ResultsOpen => Addons.IsVisible("ItemSearchResult");

    /// <summary>
    /// Type an item name into the board's search field and run the search.
    ///
    /// Writes the name to BOTH backing strings and the text-input component, forces the addon out
    /// of wishlist/category mode with a plain field write (rather than calling SetModeFilter,
    /// whose filter argument we can't verify), then runs the search. Returns a description of
    /// every step and whether it succeeded, which is what the run log prints with Debug on —
    /// so a failure says WHICH part failed instead of just hanging.
    /// </summary>
    public static string TypeSearch(string itemName, bool partialMatch)
    {
        var addon = GetItemSearch();
        if (addon == null) return "no ItemSearch addon";
        if (string.IsNullOrEmpty(itemName)) return "empty item name";

        var steps = new List<string>();

        try { addon->Mode = AddonItemSearch.SearchMode.Normal; steps.Add("mode=Normal"); }
        catch (Exception ex) { steps.Add($"mode FAILED({ex.GetType().Name})"); }

        var bytes = Encoding.UTF8.GetBytes(itemName + "\0");
        fixed (byte* p = bytes)
        {
            try { addon->SearchText.SetString(p); steps.Add("text1"); }
            catch (Exception ex) { steps.Add($"text1 FAILED({ex.GetType().Name})"); }

            try { addon->SearchText2.SetString(p); steps.Add("text2"); }
            catch (Exception ex) { steps.Add($"text2 FAILED({ex.GetType().Name})"); }

            if (addon->SearchTextInput != null)
            {
                try { addon->SearchTextInput->SetText(p); steps.Add("input"); }
                catch (Exception ex) { steps.Add($"input FAILED({ex.GetType().Name})"); }
            }
            else steps.Add("input=null");

            // THE one that actually goes to the server. The addon's SearchText fields are the
            // UI's copy; AgentItemSearch's StringData->SearchParam is what the search request
            // reads. Setting only the addon side types the name and searches for nothing, which
            // is exactly the "No matching items" we saw.
            try
            {
                var agent = AgentItemSearch.Instance();
                if (agent == null) steps.Add("agent=null");
                else if (agent->StringData == null) steps.Add("agentString=null");
                else { agent->StringData->SearchParam.SetString(p); steps.Add("agentParam"); }
            }
            catch (Exception ex) { steps.Add($"agentParam FAILED({ex.GetType().Name})"); }
        }

        try { addon->PartialMatch = partialMatch; steps.Add($"partial={partialMatch}"); }
        catch (Exception ex) { steps.Add($"partial FAILED({ex.GetType().Name})"); }

        // Keep the visible checkbox in step with the flag, so what you see matches what runs.
        try
        {
            if (addon->PartialSearchCheckBox != null)
            {
                addon->PartialSearchCheckBox->AtkComponentButton.IsChecked = partialMatch;
                steps.Add("partialBox");
            }
        }
        catch (Exception ex) { steps.Add($"partialBox FAILED({ex.GetType().Name})"); }

        return string.Join(", ", steps);
    }

    /// <summary>Type the name and immediately run the search. Used by the retry ladder.</summary>
    public static string Search(string itemName, bool partialMatch)
        => TypeSearch(itemName, partialMatch) + ", " + RunSearchOnly(true);

    /// <summary>
    /// Fire the search again without retyping. Used by the retry ladder: the text is already in
    /// place, so if the first RunSearch didn't take, this is the cheap second attempt.
    /// </summary>
    public static string RunSearchOnly(bool ignoreFilters)
    {
        var addon = GetItemSearch();
        if (addon == null) return "runSearch: no addon";
        try { addon->RunSearch(ignoreFilters); return $"RunSearch({ignoreFilters})"; }
        catch (Exception ex) { return $"RunSearch({ignoreFilters}) FAILED({ex.GetType().Name}: {ex.Message})"; }
    }

    /// <summary>Click the board's Search button through the addon's own event handler.</summary>
    public static string PressSearchButton(int opcode)
    {
        var addon = Addons.GetAddon("ItemSearch");
        if (addon == null) return "searchButton: no addon";
        try { Callback.Fire(addon, true, opcode); return $"searchButton callback({opcode})"; }
        catch (Exception ex) { return $"searchButton callback({opcode}) FAILED({ex.GetType().Name})"; }
    }

    /// <summary>
    /// Row index in the board's result list whose item id matches, or -1 while the page is still
    /// loading / the item isn't present. Read from AgentItemSearch's parsed result page, so this
    /// is a real id comparison rather than a text guess.
    /// </summary>
    public static int FindResultRow(uint itemId)
    {
        var agent = AgentItemSearch.Instance();
        if (agent == null) return -1;
        var count = (int)Math.Min(agent->ListingPageItemCount, (uint)agent->ListingPageItemIds.Length);
        for (var i = 0; i < count; i++)
            if (agent->ListingPageItemIds[i] == itemId) return i;
        return -1;
    }

    public static int ResultPageCount()
    {
        var agent = AgentItemSearch.Instance();
        return agent == null ? 0 : (int)agent->ListingPageItemCount;
    }

    /// <summary>Click a row in the board's search results, opening that item's listings.</summary>
    public static void SelectResultRow(int row, int opcode)
    {
        var addon = Addons.GetAddon("ItemSearch");
        if (addon == null || row < 0) return;
        Callback.Fire(addon, true, opcode, row);
    }

    // ---- Listings ----------------------------------------------------------------------------

    /// <summary>One live board listing, keeping its RAW proxy index so it can be clicked.</summary>
    public readonly struct BoardListing
    {
        public readonly int Index;          // index into the proxy's listing array
        public readonly uint ItemId;
        public readonly long UnitPrice;
        public readonly long TotalTax;
        public readonly int Quantity;
        public readonly bool Hq;
        public readonly string Seller;

        public long Total => UnitPrice * Quantity;

        public BoardListing(int index, uint itemId, long unit, long tax, int qty, bool hq, string seller)
        { Index = index; ItemId = itemId; UnitPrice = unit; TotalTax = tax; Quantity = qty; Hq = hq; Seller = seller; }
    }

    /// <summary>
    /// Live listings for the currently-open item, cheapest first, each carrying its raw index.
    /// Rows belonging to a previous search are rejected by comparing each row's own ItemId to the
    /// proxy's SearchItemId — the same stale-row defence MarketData uses.
    /// </summary>
    public static List<BoardListing> Listings()
    {
        var result = new List<BoardListing>();
        var proxy = MarketData.GetProxy();
        if (proxy == null) return result;

        var searchId = proxy->SearchItemId;
        if (searchId == 0) return result;

        var count = (int)Math.Min(proxy->ListingCount, (uint)proxy->Listings.Length);
        for (var i = 0; i < count; i++)
        {
            ref var l = ref proxy->Listings[i];
            if (l.ItemId != searchId) continue;
            if (l.UnitPrice == 0) continue;
            result.Add(new BoardListing(
                i, l.ItemId, l.UnitPrice, l.TotalTax, (int)l.Quantity, l.IsHqItem, l.CharacterName.ToString()));
        }

        result.Sort((a, b) => a.UnitPrice.CompareTo(b.UnitPrice));
        return result;
    }

    /// <summary>True once listings for the expected item are actually readable.</summary>
    public static bool ListingsReadyFor(uint itemId)
    {
        var proxy = MarketData.GetProxy();
        if (proxy == null) return false;
        if (proxy->WaitingForListings) return false;
        if (proxy->SearchItemId != itemId) return false;
        return Listings().Count > 0;
    }

    /// <summary>Click a listing row to start a purchase. Raises the game's confirm dialog.</summary>
    public static void ClickListing(int index, int opcode)
    {
        var addon = Addons.GetAddon("ItemSearchResult");
        if (addon == null || index < 0) return;
        Callback.Fire(addon, true, opcode, index);
    }

    // ---- Confirmation dialog -----------------------------------------------------------------

    /// <summary>
    /// Concatenated visible text of the SelectYesno dialog, or empty if it isn't up. Read straight
    /// off the text nodes rather than through a helper property, so it can't silently change shape.
    /// </summary>
    public static string ReadConfirmText()
    {
        var addon = Addons.GetAddon("SelectYesno");
        if (addon == null || !addon->IsVisible) return string.Empty;

        var sb = new StringBuilder();
        for (uint id = 1; id <= 60; id++)
        {
            var node = addon->UldManager.SearchNodeById(id);
            if (node == null || node->Type != NodeType.Text) continue;
            var tn = (AtkTextNode*)node;
            if (!tn->AtkResNode.IsVisible()) continue;
            var text = tn->NodeText.ToString();
            if (string.IsNullOrWhiteSpace(text)) continue;
            sb.Append(text).Append(' ');
        }
        return sb.ToString().Trim();
    }

    public static bool ConfirmVisible => Addons.IsVisible("SelectYesno");

    /// <summary>SelectYesno: 0 = Yes, 1 = No. The long-standing convention across this codebase.</summary>
    public static void AnswerConfirm(bool yes)
    {
        var addon = Addons.GetAddon("SelectYesno");
        if (addon == null) return;
        Callback.Fire(addon, true, yes ? 0 : 1);
    }

    private static readonly Regex NumberRun = new(@"[0-9][0-9,\.\u00A0 ]*", RegexOptions.Compiled);

    /// <summary>
    /// Largest number appearing in a confirmation string, which for a purchase dialog is the
    /// total gil. Returns -1 when nothing parses — treated as "refuse and abort" by the caller.
    /// </summary>
    public static long LargestNumberIn(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return -1;
        long best = -1;
        foreach (Match m in NumberRun.Matches(text))
        {
            var digits = new string(m.Value.Where(char.IsDigit).ToArray());
            if (digits.Length == 0 || digits.Length > 12) continue;
            if (long.TryParse(digits, out var v) && v > best) best = v;
        }
        return best;
    }

    // ---- Player state ------------------------------------------------------------------------

    /// <summary>Current gil (inventory item id 1).</summary>
    public static long Gil()
    {
        var inv = InventoryManager.Instance();
        if (inv == null) return 0;
        return inv->GetInventoryItemCount(1);
    }

    // ---- Diagnostics -------------------------------------------------------------------------

    /// <summary>Human-readable dump of board state, for "/undercut buydump".</summary>
    public static List<string> Dump()
    {
        var lines = new List<string>();
        lines.Add($"ItemSearch visible={Addons.IsVisible("ItemSearch")} ItemSearchResult visible={Addons.IsVisible("ItemSearchResult")} SelectYesno visible={Addons.IsVisible("SelectYesno")}");

        var search = GetItemSearch();
        if (search == null)
        {
            lines.Add("AddonItemSearch: null");
        }
        else
        {
            lines.Add($"AddonItemSearch: mode={search->Mode} filter={search->SelectedFilter} partial={search->PartialMatch} text=\"{search->SearchText.ToString()}\" text2=\"{search->SearchText2.ToString()}\" input={(search->SearchTextInput == null ? "null" : "ok")}");
        }

        var agent = AgentItemSearch.Instance();
        if (agent == null)
        {
            lines.Add("AgentItemSearch: null");
        }
        else
        {
            var param = agent->StringData == null ? "(null)" : agent->StringData->SearchParam.ToString();
            lines.Add($"AgentItemSearch: searchParam=\"{param}\"");
            lines.Add($"AgentItemSearch: pageItems={agent->ListingPageItemCount} page={agent->ListingCurrentPage}/{agent->ListingPageCount} resultItemId={agent->ResultItemId} selectedIdx={agent->ResultSelectedIndex}");
            var count = (int)Math.Min(agent->ListingPageItemCount, (uint)agent->ListingPageItemIds.Length);
            for (var i = 0; i < count && i < 12; i++)
            {
                var id = agent->ListingPageItemIds[i];
                lines.Add($"  row {i}: itemId={id} ({ItemSearch.FindByIdAny(id)})");
            }
        }

        var proxy = MarketData.GetProxy();
        if (proxy == null)
        {
            lines.Add("InfoProxyItemSearch: null");
        }
        else
        {
            lines.Add($"Proxy: searchItemId={proxy->SearchItemId} listingCount={proxy->ListingCount} waiting={proxy->WaitingForListings}");
            var listings = Listings();
            for (var i = 0; i < listings.Count && i < 12; i++)
            {
                var l = listings[i];
                lines.Add($"  listing rawIdx={l.Index} {l.UnitPrice:N0}g x{l.Quantity} hq={l.Hq} seller={l.Seller}");
            }
        }

        var confirm = ReadConfirmText();
        if (confirm.Length > 0) lines.Add($"SelectYesno text: {confirm}");

        lines.Add($"Gil: {Gil():N0}");
        return lines;
    }
}
