using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using ECommons.GameHelpers;

namespace MarketHelper;

public enum BuyState
{
    Idle, NextStop, Travel, WaitTravel, FindBoard, WalkBoard, InteractBoard, WaitBoard,
    NextItem, Search, WaitTyped, WaitResults, WaitManualSearch, SelectRow, WaitListings, PickListing,
    WaitConfirm, WaitPurchase, CloseBoard, ReturnHome, WaitReturn, Done, Error,
}

/// <summary>
/// Executes a <see cref="BuyPlanResult"/>: hops to each world via Lifestream, opens a market
/// board, and buys the listings that are still at or under your cap.
///
/// THREE HARD RULES, in descending order of importance:
///
/// 1. The plan is a hint, never an authority. Universalis data can be minutes stale, so every
///    purchase is priced off the LIVE board proxy. A plan line whose price has moved above the
///    cap is dropped, not bought.
/// 2. Every purchase is confirmed against the game's own dialog. We read the SelectYesno text,
///    check it names the item we asked for and that the total is within cap (plus tax headroom),
///    and answer NO + abort if anything fails to match. A mis-clicked row therefore costs nothing.
/// 3. Gil and bag space are ALWAYS re-read from the game, never tracked in a counter. A failed or
///    partial purchase self-corrects on the next pass instead of desyncing the run. (This is the
///    rule Consolidator was rebuilt around; it applies verbatim here.)
/// </summary>
public sealed class BuyRunner
{
    private readonly Plugin _plugin;
    private Configuration Cfg => _plugin.Config;

    public BuyState State { get; private set; } = BuyState.Idle;
    public string Status { get; private set; } = "Idle.";
    public List<string> Report { get; } = new();
    public bool Running => State is not (BuyState.Idle or BuyState.Done or BuyState.Error);
    public bool DryRun { get; private set; }

    private BuyPlanResult? _plan;
    private int _stopIdx = -1;
    private WorldStop? _stop;

    private List<uint> _itemQueue = new();
    private int _itemIdx = -1;
    private uint _item;
    private string _itemName = string.Empty;
    private long _cap;
    private int _wantUnits;
    private bool _hqOnly;

    private double _deadline;
    private int _ticks;
    private int _buysThisItem;
    private int _searchAttempt;
    private long _gilAtStart;
    private long _expectedTotal;
    private int _expectedQty;
    private string _homeWorld = string.Empty;

    // Dry run only: listings we've already "bought" on paper, so the loop advances.
    private readonly HashSet<string> _simulated = new();

    // Live totals, for the report. Spend is derived from re-read gil, never accumulated blindly.
    private int _unitsBought;
    private long _spent;

    private static double Now => Environment.TickCount64;

    public BuyRunner(Plugin plugin) => _plugin = plugin;

    public void Start(BuyPlanResult plan, bool dryRun)
    {
        if (plan == null || !plan.HasWork) { SetError("Nothing in the plan to buy."); return; }
        if (!Player.Available) { SetError("Not logged in."); return; }

        _plan = plan;
        DryRun = dryRun;
        _stopIdx = -1;
        _stop = null;
        _itemQueue = new List<uint>();
        _itemIdx = -1;
        _unitsBought = 0;
        _spent = 0;
        _simulated.Clear();
        Report.Clear();
        _gilAtStart = MarketBoard.Gil();
        _homeWorld = string.IsNullOrWhiteSpace(Cfg.BuyerHomeWorld) ? WorldInfo.CurrentWorld() : Cfg.BuyerHomeWorld.Trim();

        Log(DryRun
            ? $"DRY RUN — no gil will be spent. {plan.GrandUnits} unit(s) across {plan.Stops.Count} world(s)."
            : $"Buying {plan.GrandUnits} unit(s) across {plan.Stops.Count} world(s) for about {plan.GrandTotal:N0}g.");

        State = BuyState.NextStop;
    }

    public void Stop()
    {
        if (Running) NavmeshBridge.Stop();
        State = BuyState.Idle;
        Status = "Stopped.";
    }

    public void Tick()
    {
        if (!Running) return;
        try { Step(); }
        catch (Exception ex) { SetError($"Exception: {ex.Message}"); }
    }

    private void Step()
    {
        if (Now < _deadline) return;

        switch (State)
        {
            case BuyState.NextStop:
            {
                _stopIdx++;
                if (_plan == null || _stopIdx >= _plan.Stops.Count)
                {
                    Finish();
                    return;
                }
                _stop = _plan.Stops[_stopIdx];
                var here = WorldInfo.CurrentWorld();
                var hereDc = WorldInfo.CurrentDataCenter();
                Log($"Stop {_stopIdx + 1}/{_plan.Stops.Count}: {_stop.World}{(string.IsNullOrWhiteSpace(_stop.DataCenter) ? "" : $" [{_stop.DataCenter}]")} — {_stop.TotalUnits} unit(s), about {_stop.TotalCost:N0}g.");

                if (string.Equals(here, _stop.World, StringComparison.OrdinalIgnoreCase))
                {
                    State = BuyState.FindBoard;
                    return;
                }
                if (!string.IsNullOrWhiteSpace(_stop.DataCenter)
                    && !string.IsNullOrWhiteSpace(hereDc)
                    && !string.Equals(hereDc, _stop.DataCenter, StringComparison.OrdinalIgnoreCase))
                    Log($"Data-center transfer: {hereDc} -> {_stop.DataCenter}. This one can queue for a while.");
                State = BuyState.Travel;
                return;
            }

            case BuyState.Travel:
            {
                if (_stop == null) { State = BuyState.NextStop; return; }
                if (!LifestreamBridge.Available)
                {
                    Log($"Skipping {_stop.World} — Lifestream isn't loaded, can't travel.");
                    State = BuyState.NextStop;
                    return;
                }
                Status = $"Travelling to {_stop.World}...";
                if (!LifestreamBridge.ExecuteCommand(_stop.World))
                {
                    Log($"Skipping {_stop.World} — Lifestream refused the travel command.");
                    State = BuyState.NextStop;
                    return;
                }
                _ticks = 0;
                Wait(2000);
                State = BuyState.WaitTravel;
                return;
            }

            case BuyState.WaitTravel:
            {
                if (_stop == null) { State = BuyState.NextStop; return; }
                if (string.Equals(WorldInfo.CurrentWorld(), _stop.World, StringComparison.OrdinalIgnoreCase)
                    && !LifestreamBridge.IsBusy())
                {
                    Log($"Arrived on {_stop.World}.");
                    Wait(1500);
                    State = BuyState.FindBoard;
                    return;
                }
                // Travel keeps a hard deadline — a stuck queue must never hang the run silently.
                if (++_ticks > 1200)
                {
                    LifestreamBridge.Abort();
                    Log($"Travel to {_stop.World} timed out — skipping that world.");
                    State = BuyState.NextStop;
                    return;
                }
                Wait(250);
                return;
            }

            case BuyState.FindBoard:
            {
                var board = MarketBoard.GetNearest(Cfg.BuyerBoardNameOverride, out var dist);
                if (board == null)
                {
                    SetError("No market board nearby. Stand near one (or near the city aetheryte with vnavmesh installed) and run again.");
                    return;
                }
                if (dist > 4.5f)
                {
                    if (Cfg.BuyerUseNavmesh && NavmeshBridge.Ready)
                    {
                        Status = $"Walking to the market board ({dist:F0}y)...";
                        NavmeshBridge.MoveTo(board.Position);
                        _ticks = 0;
                        Wait(800);
                        State = BuyState.WalkBoard;
                        return;
                    }
                    SetError($"Nearest market board is {dist:F0}y away. Move closer, or install vnavmesh to have it walked automatically.");
                    return;
                }
                State = BuyState.InteractBoard;
                return;
            }

            case BuyState.WalkBoard:
            {
                var board = MarketBoard.GetNearest(Cfg.BuyerBoardNameOverride, out var dist);
                if (board == null) { SetError("Lost sight of the market board while walking."); return; }
                if (dist <= 4.0f)
                {
                    NavmeshBridge.Stop();
                    Wait(400);
                    State = BuyState.InteractBoard;
                    return;
                }
                if (!NavmeshBridge.Moving && ++_ticks > 8)
                {
                    NavmeshBridge.MoveTo(board.Position);
                    _ticks = 0;
                }
                if (_ticks > 600) { SetError("Couldn't reach the market board."); return; }
                Wait(400);
                return;
            }

            case BuyState.InteractBoard:
            {
                var board = MarketBoard.GetNearest(Cfg.BuyerBoardNameOverride, out _);
                if (board == null) { State = BuyState.FindBoard; return; }
                Status = "Opening the market board...";
                MarketBoard.Interact(board);
                _ticks = 0;
                Wait(800);
                State = BuyState.WaitBoard;
                return;
            }

            case BuyState.WaitBoard:
            {
                if (MarketBoard.BoardOpen)
                {
                    BuildItemQueue();
                    State = BuyState.NextItem;
                    return;
                }
                if (++_ticks > 40) { SetError("The market board didn't open."); return; }
                Wait(300);
                return;
            }

            case BuyState.NextItem:
            {
                _itemIdx++;
                if (_stop == null || _itemIdx >= _itemQueue.Count)
                {
                    State = BuyState.CloseBoard;
                    return;
                }
                _item = _itemQueue[_itemIdx];

                var lines = _stop.Lines.Where(l => l.ItemId == _item).ToList();
                _itemName = lines.Count > 0 ? lines[0].ItemName : ItemSearch.FindById(_item);
                _wantUnits = lines.Sum(l => l.Quantity);
                _hqOnly = lines.All(l => l.Hq) && lines.Count > 0;

                // The cap always comes from the CONFIG row, not the plan — if you lowered your max
                // price after scanning, the lower number wins.
                var cfgRow = Cfg.BuyerItems.FirstOrDefault(i => i.ItemId == _item);
                _cap = cfgRow?.MaxPrice ?? lines.Max(l => l.UnitPrice);
                if (cfgRow != null) _hqOnly = cfgRow.HqOnly;

                _buysThisItem = 0;
                Log($"{_itemName}: want {_wantUnits} unit(s) at or under {_cap:N0}g each.");
                State = BuyState.Search;
                return;
            }

            case BuyState.Search:
            {
                if (!MarketBoard.BoardOpen) { State = BuyState.FindBoard; return; }
                Status = $"Typing {_itemName}...";
                var detail = MarketBoard.TypeSearch(_itemName, Cfg.BuyerPartialMatch);
                if (Cfg.Debug) _plugin.Chat($"[Market Helper] Buyer: typed -> {detail}");
                if (detail.Contains("FAILED", StringComparison.Ordinal))
                    Log($"{_itemName}: a search field wouldn't set — {detail}");
                _ticks = 0;
                _searchAttempt = 0;
                // Deliberate gap between typing and searching: firing the search in the same frame
                // as the text write can search on a half-set field and come back "No matching items".
                Wait(Math.Max(200, Cfg.BuyerSearchTypeDelayMs));
                State = BuyState.WaitTyped;
                return;
            }

            case BuyState.WaitTyped:
            {
                Status = $"Searching for {_itemName}...";
                var ran = MarketBoard.RunSearchOnly(true);
                if (Cfg.Debug) _plugin.Chat($"[Market Helper] Buyer: {ran}");
                _ticks = 0;
                Wait(Math.Max(600, Cfg.SearchPacingMs));
                State = BuyState.WaitResults;
                return;
            }

            case BuyState.WaitResults:
            {
                // The user may have driven the board themselves — if listings for our item are
                // already up, skip straight past the result list.
                if (MarketBoard.ListingsReadyFor(_item))
                {
                    _ticks = 0;
                    State = BuyState.PickListing;
                    return;
                }

                var row = MarketBoard.FindResultRow(_item);
                if (row >= 0)
                {
                    if (Cfg.Debug) _plugin.Chat($"[Market Helper] Buyer: {_itemName} at result row {row} of {MarketBoard.ResultPageCount()}.");
                    // Two ways of loading the listings, on purpose. The row click drives the real
                    // UI (which we need open to buy); the proxy request is struct-verified and
                    // loads the listing data even if the click's callback case is wrong.
                    var click = MarketBoard.SelectResultRow(row, Cfg.BuyerResultRowEventType, Cfg.BuyerResultRowEventParam);
                    var req = Cfg.BuyerForceListingRequest ? "; " + MarketBoard.RequestListings(_item) : string.Empty;
                    if (Cfg.Debug) _plugin.Chat($"[Market Helper] Buyer: {click}{req}");
                    _ticks = 0;
                    Wait(700);
                    State = BuyState.WaitListings;
                    return;
                }

                // Retry ladder. Typing the name is reliable; making the game ACT on it is the part
                // that can silently no-op, so escalate through the ways of firing the search
                // before giving up on the item.
                _ticks++;
                if (_ticks == 6 && _searchAttempt == 0)
                {
                    _searchAttempt = 1;
                    var d = MarketBoard.RunSearchOnly(false);
                    if (Cfg.Debug) _plugin.Chat($"[Market Helper] Buyer: retry 1 -> {d}");
                    Wait(400);
                    return;
                }
                if (_ticks == 14 && _searchAttempt == 1)
                {
                    _searchAttempt = 2;
                    var d = MarketBoard.TypeSearch(_itemName, Cfg.BuyerPartialMatch);
                    if (Cfg.Debug) _plugin.Chat($"[Market Helper] Buyer: retry 2 (retype) -> {d}");
                    _ticks = 0;
                    Wait(Math.Max(200, Cfg.BuyerSearchTypeDelayMs));
                    State = BuyState.WaitTyped;
                    return;
                }
                if (_ticks == 24 && _searchAttempt == 2)
                {
                    _searchAttempt = 3;
                    var d = MarketBoard.PressSearchButton(Cfg.BuyerSearchButtonOpcode);
                    if (Cfg.Debug) _plugin.Chat($"[Market Helper] Buyer: retry 3 -> {d}");
                    Wait(400);
                    return;
                }

                if (_ticks > 34)
                {
                    if (Cfg.BuyerManualSearchFallback)
                    {
                        Log($"{_itemName}: couldn't fire the board search. Search it yourself and open the listings — I'll carry on from there. (Stop to cancel.)");
                        _ticks = 0;
                        State = BuyState.WaitManualSearch;
                        Wait(500);
                        return;
                    }
                    Log($"{_itemName}: no search result on this board — skipping.");
                    State = BuyState.NextItem;
                    return;
                }
                Wait(300);
                return;
            }

            case BuyState.WaitManualSearch:
            {
                if (!MarketBoard.BoardOpen && !MarketBoard.ResultsOpen)
                {
                    Log($"{_itemName}: board closed while waiting — skipping.");
                    State = BuyState.NextItem;
                    return;
                }
                if (MarketBoard.ListingsReadyFor(_item))
                {
                    Log($"{_itemName}: listings are up — carrying on.");
                    _ticks = 0;
                    State = BuyState.PickListing;
                    return;
                }
                var manualRow = MarketBoard.FindResultRow(_item);
                if (manualRow >= 0)
                {
                    MarketBoard.SelectResultRow(manualRow, Cfg.BuyerResultRowEventType, Cfg.BuyerResultRowEventParam);
                    _ticks = 0;
                    Wait(700);
                    State = BuyState.WaitListings;
                    return;
                }

                var limitTicks = Math.Max(10, Cfg.BuyerManualSearchTimeoutSec * 2);
                Status = $"Waiting for you to search {_itemName} ({(limitTicks - _ticks) / 2}s)...";
                if (++_ticks > limitTicks)
                {
                    Log($"{_itemName}: gave up waiting for a manual search — skipping.");
                    State = BuyState.NextItem;
                    return;
                }
                Wait(500);
                return;
            }

            case BuyState.WaitListings:
            {
                if (MarketBoard.ListingsReadyFor(_item))
                {
                    _ticks = 0;
                    State = BuyState.PickListing;
                    return;
                }
                if (Cfg.BuyerForceListingRequest && (_ticks == 10 || _ticks == 25))
                {
                    var again = MarketBoard.RequestListings(_item);
                    if (Cfg.Debug) _plugin.Chat($"[Market Helper] Buyer: re-request -> {again}");
                }
                if (++_ticks > 40)
                {
                    // The diagnostic goes in the message itself — a bare "never loaded" costs a
                    // whole extra test run to explain.
                    Log($"{_itemName}: listings never loaded — skipping. [{MarketBoard.ListingsDiagnostic(_item)}]");
                    State = BuyState.NextItem;
                    return;
                }
                Wait(300);
                return;
            }

            case BuyState.PickListing:
            {
                if (_buysThisItem >= 40) { Log($"{_itemName}: purchase cap for one item reached — moving on."); State = BuyState.NextItem; return; }

                var boughtSoFar = UnitsBoughtFor(_item);
                var remaining = _wantUnits - boughtSoFar;
                if (remaining <= 0) { Log($"{_itemName}: done ({boughtSoFar} unit(s))."); State = BuyState.NextItem; return; }

                // Bag space is re-read, never assumed.
                var free = RetainerReader.FreePlayerBagSlots();
                if (free >= 0 && free < Math.Max(1, Cfg.BuyerMinFreeSlots))
                {
                    Log($"Only {free} free bag slot(s) left — stopping before the bags fill.");
                    State = BuyState.CloseBoard;
                    _finishAfterClose = true;
                    return;
                }

                var listings = MarketBoard.Listings(_item)
                    .Where(l => l.UnitPrice <= _cap)
                    .Where(l => !_hqOnly || l.Hq)
                    .Where(l => Cfg.BuyerAllowOvershoot || l.Quantity <= remaining)
                    .Where(l => !DryRun || !_simulated.Contains(SimKey(l)))
                    .ToList();

                if (listings.Count == 0)
                {
                    Log($"{_itemName}: nothing left at or under {_cap:N0}g here ({boughtSoFar}/{_wantUnits} bought).");
                    State = BuyState.NextItem;
                    return;
                }

                var pick = listings[0];
                _expectedTotal = pick.Total;
                _expectedQty = pick.Quantity;

                // A real purchase needs the listings WINDOW, not just the listing data. If the
                // proxy loaded but the UI didn't open, buying blind is exactly what we refuse to
                // do — hand it to you instead.
                if (!DryRun && !MarketBoard.ResultsOpen)
                {
                    if (Cfg.BuyerManualSearchFallback)
                    {
                        Log($"{_itemName}: prices loaded but the listings window isn't open, so I won't click blind. Open it yourself and I'll carry on.");
                        _ticks = 0;
                        State = BuyState.WaitManualSearch;
                        Wait(500);
                        return;
                    }
                    Log($"{_itemName}: listings window isn't open — skipping rather than clicking blind.");
                    State = BuyState.NextItem;
                    return;
                }

                // Gil safety, re-read live: reserve floor and optional per-run spend ceiling.
                var gil = MarketBoard.Gil();
                if (gil - _expectedTotal < Cfg.BuyerGilReserve)
                {
                    Log($"Stopping: buying that would drop you below your {Cfg.BuyerGilReserve:N0}g reserve.");
                    State = BuyState.CloseBoard;
                    _finishAfterClose = true;
                    return;
                }
                var spentSoFar = Math.Max(0, _gilAtStart - gil);
                if (Cfg.BuyerMaxSpendPerRun > 0 && spentSoFar + _expectedTotal > Cfg.BuyerMaxSpendPerRun)
                {
                    Log($"Stopping: per-run spend limit of {Cfg.BuyerMaxSpendPerRun:N0}g would be exceeded.");
                    State = BuyState.CloseBoard;
                    _finishAfterClose = true;
                    return;
                }

                if (DryRun)
                {
                    Log($"[dry run] would buy {pick.Quantity}x {_itemName}{(pick.Hq ? " (HQ)" : "")} at {pick.UnitPrice:N0}g = {pick.Total:N0}g from {pick.Seller}.");
                    _simulated.Add(SimKey(pick));
                    _unitsBought += pick.Quantity;
                    _spent += pick.Total;
                    RecordBought(_item, pick.Quantity);
                    _buysThisItem++;
                    Wait(120);
                    return;   // stay in PickListing for the next one
                }

                Status = $"Buying {pick.Quantity}x {_itemName} at {pick.UnitPrice:N0}g...";
                var lc = MarketBoard.ClickListing(pick.Index, Cfg.BuyerListingRowEventType, Cfg.BuyerListingRowEventParam);
                if (Cfg.Debug) _plugin.Chat($"[Market Helper] Buyer: buying rawIdx={pick.Index} {pick.UnitPrice:N0}g x{pick.Quantity} — {lc}");
                _gilBeforeBuy = gil;
                _ticks = 0;
                Wait(500);
                State = BuyState.WaitConfirm;
                return;
            }

            case BuyState.WaitConfirm:
            {
                if (!MarketBoard.ConfirmVisible)
                {
                    // Some purchases complete without a confirmation dialog. Gil leaving the
                    // wallet is the real proof, so check that before calling it a failure.
                    if (MarketBoard.Gil() < _gilBeforeBuy)
                    {
                        _ticks = 0;
                        State = BuyState.WaitPurchase;
                        return;
                    }
                    if (++_ticks > 24)
                    {
                        Log($"{_itemName}: no confirmation dialog appeared — skipping this item.");
                        State = BuyState.NextItem;
                        return;
                    }
                    Wait(250);
                    return;
                }

                var text = MarketBoard.ReadConfirmText();
                if (!ConfirmMatches(text, out var reason))
                {
                    MarketBoard.AnswerConfirm(false);
                    SetError($"Purchase confirmation didn't match ({reason}). Answered No and stopped. Dialog said: {Trim(text)}");
                    return;
                }

                MarketBoard.AnswerConfirm(true);
                _ticks = 0;
                Wait(900);
                State = BuyState.WaitPurchase;
                return;
            }

            case BuyState.WaitPurchase:
            {
                // Confirmed by gil actually leaving the wallet — not by assuming the click worked.
                var gil = MarketBoard.Gil();
                if (gil < _gilBeforeBuy)
                {
                    var cost = _gilBeforeBuy - gil;
                    _unitsBought += _expectedQty;
                    _spent += cost;
                    RecordBought(_item, _expectedQty);
                    _buysThisItem++;
                    Log($"Bought {_expectedQty}x {_itemName} for {cost:N0}g.");
                    _ticks = 0;
                    Wait(700);
                    State = BuyState.PickListing;
                    return;
                }
                if (MarketBoard.ConfirmVisible) { Wait(300); return; }
                if (++_ticks > 30)
                {
                    Log($"{_itemName}: purchase didn't register (gil unchanged) — re-checking listings.");
                    _buysThisItem++;
                    _ticks = 0;
                    State = BuyState.PickListing;
                    return;
                }
                Wait(300);
                return;
            }

            case BuyState.CloseBoard:
            {
                if (MarketBoard.ResultsOpen) { Addons.CloseAddon("ItemSearchResult"); Wait(400); return; }
                if (MarketBoard.BoardOpen) { Addons.CloseAddon("ItemSearch"); Wait(400); return; }
                if (_finishAfterClose) { Finish(); return; }
                State = BuyState.NextStop;
                return;
            }

            case BuyState.ReturnHome:
            {
                if (!LifestreamBridge.Available || string.IsNullOrWhiteSpace(_homeWorld)
                    || string.Equals(WorldInfo.CurrentWorld(), _homeWorld, StringComparison.OrdinalIgnoreCase))
                {
                    State = BuyState.Done;
                    Status = Summary();
                    return;
                }
                Status = $"Returning to {_homeWorld}...";
                LifestreamBridge.ExecuteCommand(_homeWorld);
                _ticks = 0;
                Wait(2000);
                State = BuyState.WaitReturn;
                return;
            }

            case BuyState.WaitReturn:
            {
                if (string.Equals(WorldInfo.CurrentWorld(), _homeWorld, StringComparison.OrdinalIgnoreCase)
                    && !LifestreamBridge.IsBusy())
                {
                    Log($"Back on {_homeWorld}.");
                    State = BuyState.Done;
                    Status = Summary();
                    return;
                }
                if (++_ticks > 1200)
                {
                    LifestreamBridge.Abort();
                    Log("Return trip timed out — you're still away from your home world.");
                    State = BuyState.Done;
                    Status = Summary();
                    return;
                }
                Wait(250);
                return;
            }
        }
    }

    // ---- helpers -----------------------------------------------------------------------------

    private bool _finishAfterClose;
    private long _gilBeforeBuy;
    private readonly Dictionary<uint, int> _bought = new();

    private void RecordBought(uint itemId, int qty)
        => _bought[itemId] = (_bought.TryGetValue(itemId, out var n) ? n : 0) + qty;

    private int UnitsBoughtFor(uint itemId)
        => _bought.TryGetValue(itemId, out var n) ? n : 0;

    private static string SimKey(MarketBoard.BoardListing l)
        => $"{l.Index}|{l.UnitPrice}|{l.Quantity}|{l.Seller}";

    private void BuildItemQueue()
    {
        _itemQueue = _stop == null
            ? new List<uint>()
            : _stop.Lines.Select(l => l.ItemId).Distinct().ToList();
        _itemIdx = -1;
    }

    /// <summary>
    /// Verify the game's own confirmation dialog before answering Yes. Both checks must pass:
    /// the dialog names the item we asked for, and the biggest number in it (the total gil) is
    /// within our expected total plus tax headroom. Anything else is refused.
    /// </summary>
    private bool ConfirmMatches(string text, out string reason)
    {
        reason = string.Empty;
        if (string.IsNullOrWhiteSpace(text)) { reason = "dialog text was empty"; return false; }

        var haystack = Simplify(text);
        var needle = Simplify(_itemName);
        if (needle.Length >= 3 && !haystack.Contains(needle))
        {
            reason = $"it doesn't mention \"{_itemName}\"";
            return false;
        }

        var total = MarketBoard.LargestNumberIn(text);
        if (total < 0) { reason = "no price could be read from it"; return false; }

        // Buyer tax adds up to ~5%; allow a little headroom plus rounding.
        var ceiling = (long)(_expectedTotal * 1.10) + 100;
        if (total > ceiling)
        {
            reason = $"it asks for {total:N0}g but we expected at most {ceiling:N0}g";
            return false;
        }
        return true;
    }

    private static string Simplify(string s)
        => new((s ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string Trim(string s)
        => s.Length <= 160 ? s : s[..160] + "...";

    private void Finish()
    {
        if (Cfg.BuyerReturnHome && !DryRun) { State = BuyState.ReturnHome; return; }
        State = BuyState.Done;
        Status = Summary();
        _plugin.Chat($"[Market Helper] {Summary()}");
    }

    private string Summary()
    {
        var actual = DryRun ? _spent : Math.Max(0, _gilAtStart - MarketBoard.Gil());
        return DryRun
            ? $"Dry run finished — would have bought {_unitsBought} unit(s) for about {actual:N0}g. Nothing was spent."
            : $"Finished — bought {_unitsBought} unit(s) for {actual:N0}g.";
    }

    private void Wait(int ms)
    {
        var scale = Math.Clamp(Cfg.SearchPacingMs / 600f, 0.35f, 2.5f);
        _deadline = Now + (int)(ms * scale);
    }

    private void SetError(string msg)
    {
        NavmeshBridge.Stop();
        State = BuyState.Error;
        Status = msg;
        Report.Add(msg);
        _plugin.Chat($"[Market Helper] {msg}");
    }

    // ---- learn mode ---------------------------------------------------------------------------

    private bool _learnHooked;

    /// <summary>
    /// Attach or detach the board event listeners used by "learn mode". With it on, every event
    /// the ItemSearch / ItemSearchResult addons receive is printed with its type and parameter —
    /// so clicking a result row by hand tells us the exact numbers to send back, instead of
    /// guessing them. Safe to call repeatedly; it reconciles against the config flag.
    /// </summary>
    public void ApplyLearnMode()
    {
        var want = Cfg.BuyerLearnEvents;
        if (want == _learnHooked) return;

        try
        {
            if (want)
            {
                _plugin.AddonLifecycle.RegisterListener(AddonEvent.PostReceiveEvent, "ItemSearch", OnBoardEvent);
                _plugin.AddonLifecycle.RegisterListener(AddonEvent.PostReceiveEvent, "ItemSearchResult", OnBoardEvent);
                _plugin.Chat("[Market Helper] Learn mode ON — open a market board, click a search result, then click a listing. Turn it off when done.");
            }
            else
            {
                _plugin.AddonLifecycle.UnregisterListener(AddonEvent.PostReceiveEvent, "ItemSearch", OnBoardEvent);
                _plugin.AddonLifecycle.UnregisterListener(AddonEvent.PostReceiveEvent, "ItemSearchResult", OnBoardEvent);
                _plugin.Chat("[Market Helper] Learn mode OFF.");
            }
            _learnHooked = want;
        }
        catch (Exception ex)
        {
            _plugin.Chat($"[Market Helper] Learn mode couldn't be changed: {ex.Message}");
        }
    }

    private void OnBoardEvent(AddonEvent type, AddonArgs args)
    {
        try
        {
            if (args is not AddonReceiveEventArgs e) return;
            // Rollover/rollout and mouse-move fire constantly and would bury the useful lines.
            var t = (int)e.AtkEventType;
            if (t is 33 or 34 or 8 or 9) return;
            _plugin.Chat($"[Market Helper] LEARN {args.AddonName}: eventType={t} param={e.EventParam}");
        }
        catch { /* diagnostics must never break a run */ }
    }

    public void DisposeLearnMode()
    {
        if (!_learnHooked) return;
        try
        {
            _plugin.AddonLifecycle.UnregisterListener(AddonEvent.PostReceiveEvent, "ItemSearch", OnBoardEvent);
            _plugin.AddonLifecycle.UnregisterListener(AddonEvent.PostReceiveEvent, "ItemSearchResult", OnBoardEvent);
        }
        catch { }
        _learnHooked = false;
    }

    private void Log(string msg)
    {
        Report.Add(msg);
        Status = msg;
        if (Cfg.Verbose) _plugin.Chat($"[Market Helper] {msg}");
    }
}
