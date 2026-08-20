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
    WaitConfirm, WaitPurchase, CloseBoard, ReturnHome, WaitReturn, Paused, Done, Error,
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

    /// <summary>
    /// Structured trail of everything the run did — every purchase with expected vs actual cost,
    /// every skip and its reason, every pause. This is the record you check afterwards rather
    /// than scrolling chat, and it's what makes a mis-buy visible instead of invisible.
    /// </summary>
    public List<BuyAuditEntry> Audit { get; } = new();

    /// <summary>Where the last run's CSV landed, if one was written.</summary>
    public string? LastAuditPath { get; private set; }
    public bool Running => State is not (BuyState.Idle or BuyState.Done or BuyState.Error);

    /// <summary>Held mid-route with everything intact — the plan, the stop, the bought counts.</summary>
    public bool IsPaused => State == BuyState.Paused;
    public string PauseReason { get; private set; } = string.Empty;
    public bool DryRun { get; private set; }

    private BuyPlanResult? _plan;
    private int _stopIdx = -1;
    private WorldStop? _stop;

    private List<uint> _itemQueue = new();
    private int _itemIdx = -1;
    private uint _item;
    private string _itemName = string.Empty;
    private long _cap;
    private bool _capped;
    private string _capReason = string.Empty;
    private int _orderTotal;
    private int _stopTarget;
    private int _extensionPasses;
    private readonly Dictionary<uint, long> _plannedMax = new();
    private int _boughtBeforeStop;
    private int _wantUnits;
    private bool _hqOnly;

    private double _deadline;
    private int _ticks;
    private int _buysThisItem;
    private int _searchAttempt;
    private int _lastListingCount;
    private bool _resultsClosed;
    private bool _announcedManualPickup;
    private int _resumeItemIdx = -1;
    private int _blindGuardHits;
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
        ResetRunState();
        Report.Clear();
        _gilAtStart = MarketBoard.Gil();

        // Snapshot now: extension stops append lines, and the rail must stay anchored to what the
        // ORIGINAL plan was willing to pay, not to whatever a top-up pass has since added.
        _plannedMax.Clear();
        foreach (var stop in plan.Stops)
            foreach (var line in stop.Lines)
                if (!_plannedMax.TryGetValue(line.ItemId, out var cur) || line.UnitPrice > cur)
                    _plannedMax[line.ItemId] = line.UnitPrice;
        _homeWorld = string.IsNullOrWhiteSpace(Cfg.BuyerHomeWorld) ? WorldInfo.CurrentWorld() : Cfg.BuyerHomeWorld.Trim();

        var already = DryRun ? 0 : plan.Bought.Values.Sum();
        var stillNeeded = DryRun
            ? plan.GrandUnits
            : plan.Items.Sum(i => Math.Max(0, i.Requested - plan.BoughtFor(i.ItemId)));

        Log(DryRun
            ? $"DRY RUN — no gil will be spent. {plan.GrandUnits} unit(s) across {plan.Stops.Count} world(s)."
            : already > 0
                ? $"Carrying on: {stillNeeded} unit(s) still to buy of {plan.GrandUnits} planned ({already} already bought against this plan)."
                : $"Buying {plan.GrandUnits} unit(s) across {plan.Stops.Count} world(s) for about {plan.GrandTotal:N0}g.");
        Note(AuditKind.RunStart,
            $"{(DryRun ? "Dry run" : "Live run")} — {plan.Stops.Count} stop(s), {plan.GrandUnits} unit(s), planned {plan.GrandTotal:N0}g, starting gil {_gilAtStart:N0}",
            expected: plan.GrandTotal);

        State = BuyState.NextStop;
    }

    /// <summary>
    /// Clear EVERY per-run counter. This exists because forgetting one of them is silent and
    /// baffling: the dry-run tally survived between runs, so a dry run that "bought" 1 Ale Tap left
    /// the next live run believing the order was already filled — it reported "done (1 unit(s))"
    /// at each stop and never bought anything. Anything that accumulates during a run resets here.
    /// </summary>
    private void ResetRunState()
    {
        _stopIdx = -1;
        _stop = null;
        _itemQueue = new List<uint>();
        _itemIdx = -1;
        _item = 0;
        _itemName = string.Empty;
        _cap = 0;
        _capped = true;
        _capReason = string.Empty;
        _orderTotal = 0;
        _stopTarget = 0;
        _boughtBeforeStop = 0;
        _extensionPasses = 0;
        _wantUnits = 0;
        _hqOnly = false;

        _unitsBought = 0;
        _spent = 0;
        _dryBought.Clear();   // NOTE: the plan's real tally is deliberately NOT cleared here
        _simulated.Clear();

        _buysThisItem = 0;
        _searchAttempt = 0;
        _lastListingCount = 0;
        _resultsClosed = false;
        _blindGuardHits = 0;
        _announcedManualPickup = false;
        _resumeItemIdx = -1;
        PauseReason = string.Empty;
        Audit.Clear();
        LastAuditPath = null;
        _ticks = 0;
        _deadline = 0;

        _expectedTotal = 0;
        _expectedQty = 0;
        _gilBeforeBuy = 0;
        _finishAfterClose = false;
    }

    /// <summary>
    /// Hold the run where it is. Everything survives — the plan, which stop we're on, which item,
    /// and what's been bought — so Resume picks up rather than starting over.
    ///
    /// The board addons are closed on the way in, because whatever you need to do to clear the
    /// pause (empty your bags at a retainer, sell something, change a limit) means walking away
    /// from the board. Resume re-opens it rather than assuming it's still sitting there.
    /// </summary>
    private void PauseRun(string reason)
    {
        PauseReason = reason;
        _resumeItemIdx = Math.Max(0, _itemIdx);
        NavmeshBridge.Stop();

        if (MarketBoard.ResultsOpen) Addons.CloseAddon("ItemSearchResult");
        if (MarketBoard.BoardOpen) Addons.CloseAddon("ItemSearch");

        State = BuyState.Paused;
        Status = $"Paused — {reason}";
        Report.Add($"Paused: {reason}");
        Note(AuditKind.Pause, reason, _itemName);
        _plugin.Chat($"[Market Helper] Paused — {reason}");
    }

    /// <summary>
    /// Carry on from where the pause happened: same world, same stop, same item. Goes back via
    /// the board rather than resuming mid-click, so it re-finds and re-opens everything cleanly.
    /// </summary>
    public void Resume()
    {
        if (!IsPaused) return;

        PauseReason = string.Empty;
        _ticks = 0;
        _deadline = 0;
        _searchAttempt = 0;
        _lastListingCount = 0;
        _blindGuardHits = 0;
        _resultsClosed = false;
        _announcedManualPickup = false;

        Log("Resuming.");
        Note(AuditKind.Resume, "resumed by user", _itemName);
        State = BuyState.FindBoard;
    }

    /// <summary>
    /// Pause on request. Unlike Stop this keeps the route position and the bought counts, so
    /// Resume carries straight on — the difference matters, because Stop banks progress into the
    /// shopping list and ends the run, while Pause simply holds it.
    /// </summary>
    public void PauseByUser()
    {
        if (!Running || IsPaused) return;
        PauseRun("paused by you — press Resume when you're ready.");
    }

    /// <summary>Free bag slots right now — shown next to the Resume button while paused.</summary>
    public int FreeSlotsNow() => RetainerReader.FreePlayerBagSlots();

    public void Stop()
    {
        if (Running) NavmeshBridge.Stop();
        // Bank progress before dropping the run, or a mid-route stop silently loses the
        // deduction and the next SEND re-buys everything already in your bags.
        if (Audit.Count > 0)
        {
            var spentSoFar = DryRun ? _spent : Math.Max(0, _gilAtStart - MarketBoard.Gil());
            Note(AuditKind.Stop, $"stopped by user — {_unitsBought} unit(s), {spentSoFar:N0}g",
                qty: _unitsBought, actual: spentSoFar);
        }
        UpdateListAfterRun();
        SaveAudit();
        PauseReason = string.Empty;
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

                // Everything this stop was allocated has already been taken on an earlier SEND —
                // no reason to travel here at all.
                var stopLeft = _stop.TotalUnits;
                if (!DryRun)
                {
                    stopLeft = 0;
                    foreach (var group in _stop.Lines.GroupBy(l => l.ItemId))
                        stopLeft += Math.Max(0, group.Sum(l => l.Quantity) - _plan.BoughtAtStop(_stopIdx, group.Key));
                }
                if (stopLeft <= 0)
                {
                    Log($"Stop {_stopIdx + 1}/{_plan.Stops.Count}: {_stop.World} — already done, skipping.");
                    State = BuyState.NextStop;
                    return;
                }

                var here = WorldInfo.CurrentWorld();
                var hereDc = WorldInfo.CurrentDataCenter();
                var stopTag = string.IsNullOrWhiteSpace(_stop.DataCenter) ? "" : $" [{_stop.DataCenter}]";
                Log(stopLeft == _stop.TotalUnits
                    ? $"Stop {_stopIdx + 1}/{_plan.Stops.Count}: {_stop.World}{stopTag} — {_stop.TotalUnits} unit(s), about {_stop.TotalCost:N0}g."
                    : $"Stop {_stopIdx + 1}/{_plan.Stops.Count}: {_stop.World}{stopTag} — {stopLeft} of {_stop.TotalUnits} unit(s) still to take.");

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
                    Note(AuditKind.Arrive, $"arrived on {_stop.World}");
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
                    if (_resumeItemIdx >= 0)
                    {
                        // NextItem increments first, so step back one to land on the item we
                        // paused partway through instead of restarting the whole stop.
                        _itemIdx = _resumeItemIdx - 1;
                        _resumeItemIdx = -1;
                    }
                    State = BuyState.NextItem;
                    return;
                }
                if (++_ticks > 40) { SetError("The market board didn't open."); return; }
                Wait(300);
                return;
            }

            case BuyState.NextItem:
            {
                // Close the previous item's listings window first. The proxy caches its rows, so
                // leaving it open lets the next item read the last one's prices.
                if (MarketBoard.ResultsOpen && !_resultsClosed)
                {
                    Addons.CloseAddon("ItemSearchResult");
                    _resultsClosed = true;
                    Wait(400);
                    return;
                }
                _resultsClosed = false;
                _blindGuardHits = 0;
                _announcedManualPickup = false;

                _itemIdx++;
                if (_stop == null || _itemIdx >= _itemQueue.Count)
                {
                    State = BuyState.CloseBoard;
                    return;
                }
                _item = _itemQueue[_itemIdx];

                var lines = _stop.Lines.Where(l => l.ItemId == _item).ToList();
                _itemName = lines.Count > 0 ? lines[0].ItemName : ItemSearch.FindById(_item);
                _hqOnly = lines.Count > 0 && lines.All(l => l.Hq);

                var cfgRow = Cfg.BuyerItems.FirstOrDefault(i => i.ItemId == _item);
                if (cfgRow != null) _hqOnly = cfgRow.HqOnly;

                // Unticked on the shopping list means done or not wanted — a stale plan must not
                // resurrect it.
                if (cfgRow is { Enabled: false })
                {
                    Log($"{_itemName}: unticked on the shopping list — skipping.");
                    State = BuyState.NextItem;
                    return;
                }

                // EXACTLY this stop's allocation. Nothing more.
                //
                // The plan spreads an order across the globally cheapest listings, so a stop that
                // buys beyond its share is buying that world's expensive tail while cheaper
                // listings wait further along the route. Shortfalls are not chased here — they're
                // collected at the END of the run and bought from the next cheapest listings the
                // scan found, which is what TryExtendPlan does.
                var allocated = lines.Sum(l => l.Quantity);
                // The order size comes from the PLAN's snapshot, never from the live config row.
                // The row gets deducted after a run ("5 wanted" becomes "3 left"), so reading it
                // here would subtract the same purchases twice — the plan says 5 wanted and 2
                // bought, and that arithmetic has to stay internally consistent.
                _orderTotal = _plan?.Items.FirstOrDefault(i => i.ItemId == _item)?.Requested
                              ?? cfgRow?.Quantity ?? allocated;
                _boughtBeforeStop = UnitsBoughtFor(_item);

                // What this stop still owes: its allocation minus what it already delivered on an
                // earlier SEND, and never more than the order has left.
                var doneHere = _plan?.BoughtAtStop(_stopIdx, _item) ?? 0;
                _stopTarget = Math.Max(0, allocated - doneHere);
                _stopTarget = Math.Min(_stopTarget, Math.Max(0, _orderTotal - _boughtBeforeStop));
                _wantUnits = _stopTarget;

                if (_wantUnits <= 0)
                {
                    Log($"{_itemName}: nothing left to buy here ({_boughtBeforeStop}/{_orderTotal} bought, {doneHere}/{allocated} from this stop).");
                    State = BuyState.NextItem;
                    return;
                }

                // The cap always comes from the CONFIG row, not the plan — if you lowered your max
                // price after scanning, the lower number wins.
                _capped = cfgRow?.UseMaxPrice ?? true;
                _cap = cfgRow?.EffectiveCap ?? (lines.Count > 0 ? lines.Max(l => l.UnitPrice) : long.MaxValue);
                _capReason = _capped ? "your cap" : string.Empty;

                // Price rail for an UNCAPPED item. The plan's dearest allocated unit price is the
                // most we were ever willing to pay for this item; a small tolerance over it covers
                // listings that shifted since the scan. Anything beyond that belongs to a re-scan,
                // not to this run — without this rail an uncapped item will happily take a
                // 1.8M listing while a 975k one waits two stops away.
                if (!_capped && Cfg.BuyerTopUpMaxOverPlanPercent > 0)
                {
                    var plannedMax = _plannedMax.TryGetValue(_item, out var pmv) ? pmv : PlannedMaxUnitPrice(_item);
                    if (plannedMax > 0)
                    {
                        _cap = (long)(plannedMax * (1.0 + Cfg.BuyerTopUpMaxOverPlanPercent / 100.0));
                        _capReason = $"plan max {plannedMax:N0}g +{Cfg.BuyerTopUpMaxOverPlanPercent}%";
                    }
                }

                _buysThisItem = 0;
                _lastListingCount = 0;
                var resumed = doneHere > 0 ? $" ({doneHere} of {allocated} already taken here)" : "";
                Log(_cap == long.MaxValue
                    ? $"{_itemName}: buying this stop's {_wantUnits}{resumed} (no price cap)."
                    : $"{_itemName}: buying this stop's {_wantUnits}{resumed}, at or under {_cap:N0}g each ({_capReason}).");
                Note(AuditKind.Item,
                    (_cap == long.MaxValue ? "no price cap" : $"ceiling {_cap:N0}g ({_capReason})")
                    + $"; allocation {allocated}, order {_boughtBeforeStop}/{_orderTotal}",
                    _itemName, _wantUnits);
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
                    Note(AuditKind.Skip, "no search result on this board", _itemName);
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
                    if (!_announcedManualPickup)
                    {
                        Log($"{_itemName}: listings are up — carrying on.");
                        _announcedManualPickup = true;
                    }
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
                // Settle check: the array fills progressively, so only proceed once the count has
                // held steady across two polls. Cheap, and it replaces the unreliable
                // WaitingForListings flag that used to gate this.
                var now = MarketBoard.ListingCountFor(_item);
                if (now > 0 && now == _lastListingCount)
                {
                    _ticks = 0;
                    _lastListingCount = 0;
                    State = BuyState.PickListing;
                    return;
                }
                _lastListingCount = now;
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
                    Note(AuditKind.Problem, $"listings never loaded — {MarketBoard.ListingsDiagnostic(_item)}", _itemName);
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
                var boughtHere = boughtSoFar - _boughtBeforeStop;
                var remaining = _stopTarget - boughtHere;
                if (remaining <= 0)
                {
                    Log(DryRun
                        ? $"{_itemName}: dry run — took {boughtHere} here, {boughtSoFar} of {_orderTotal} overall."
                        : $"{_itemName}: took {boughtHere} here, {boughtSoFar} of {_orderTotal} overall.");
                    State = BuyState.NextItem;
                    return;
                }

                // Bag space is re-read, never assumed.
                var free = RetainerReader.FreePlayerBagSlots();
                if (free >= 0 && free < Math.Max(1, Cfg.BuyerMinFreeSlots))
                {
                    PauseRun($"Bags are full ({free} free slot(s)). Make room, then press Resume.");
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
                    Log(_cap == long.MaxValue
                        ? $"{_itemName}: no more listings here ({boughtSoFar}/{_orderTotal} bought overall)."
                        : $"{_itemName}: nothing left at or under {_cap:N0}g here ({boughtSoFar}/{_orderTotal} bought overall).");
                    Note(AuditKind.Skip, _cap == long.MaxValue
                        ? $"no more listings — {boughtSoFar}/{_orderTotal} overall"
                        : $"nothing at or under {_cap:N0}g ({_capReason}) — {boughtSoFar}/{_orderTotal} overall", _itemName);
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
                    // Bounce guard: handing back to the manual state only helps if something
                    // changes. If we land here twice for the same item, we're in a loop — say so
                    // once and move on instead of filling the log with the same two lines.
                    if (++_blindGuardHits > 2)
                    {
                        Log($"{_itemName}: the listings window won't stay open — skipping this item.");
                        State = BuyState.NextItem;
                        return;
                    }
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
                    PauseRun($"That purchase would drop you below your {Cfg.BuyerGilReserve:N0}g reserve. Top up or lower the reserve, then press Resume.");
                    return;
                }
                var spentSoFar = Math.Max(0, _gilAtStart - gil);
                if (Cfg.BuyerMaxSpendPerRun > 0 && spentSoFar + _expectedTotal > Cfg.BuyerMaxSpendPerRun)
                {
                    PauseRun($"Per-run spend limit of {Cfg.BuyerMaxSpendPerRun:N0}g reached. Raise it, then press Resume.");
                    return;
                }

                if (DryRun)
                {
                    Log($"[dry run] would buy {pick.Quantity}x {_itemName}{(pick.Hq ? " (HQ)" : "")} at {pick.UnitPrice:N0}g = {pick.Total:N0}g from {pick.Seller}.");
                    Note(AuditKind.DryBuy, $"would buy from {pick.Seller}{(pick.Hq ? " (HQ)" : "")}",
                        _itemName, pick.Quantity, pick.UnitPrice, pick.Total);
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
                    Note(AuditKind.Refused, $"answered No — {reason}. Dialog: {Trim(text)}",
                        _itemName, _expectedQty, 0, _expectedTotal);
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
                    Note(AuditKind.Purchase, $"gil {_gilBeforeBuy:N0} -> {gil:N0}",
                        _itemName, _expectedQty,
                        _expectedQty > 0 ? _expectedTotal / _expectedQty : _expectedTotal,
                        _expectedTotal, cost);
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

            case BuyState.Paused:
                return;   // held until Resume() or Stop()

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
    /// <summary>
    /// Dry-run scratch tally. Real purchases go on the PLAN so they survive Stop → SEND; paper
    /// purchases must not, or a dry run would convince the next live run the order was filled.
    /// </summary>
    private readonly Dictionary<uint, int> _dryBought = new();

    /// <summary>Where buys are counted: the plan for real runs, a throwaway dict for dry runs.</summary>
    private Dictionary<uint, int> Tally => DryRun || _plan == null ? _dryBought : _plan.Bought;

    private void RecordBought(uint itemId, int qty)
    {
        Tally[itemId] = (Tally.TryGetValue(itemId, out var n) ? n : 0) + qty;
        if (DryRun || _plan == null) return;

        var key = BuyPlanResult.StopKey(_stopIdx, itemId);
        _plan.StopProgress[key] = (_plan.StopProgress.TryGetValue(key, out var m) ? m : 0) + qty;
    }

    /// <summary>Units bought (or, in a dry run, accounted for) this run — for the plan's Bought column.</summary>
    public int BoughtFor(uint itemId) => Tally.TryGetValue(itemId, out var n) ? n : 0;

    /// <summary>True once a run has produced numbers worth showing.</summary>
    public bool HasRunResults => Tally.Count > 0;

    private int UnitsBoughtFor(uint itemId)
        => Tally.TryGetValue(itemId, out var n) ? n : 0;

    private static string SimKey(MarketBoard.BoardListing l)
        => $"{l.Index}|{l.UnitPrice}|{l.Quantity}|{l.Seller}";

    /// <summary>
    /// Items to try at this stop: the ones the plan allocated here, PLUS any item still short of
    /// its order that the scan saw listed on this world at all.
    ///
    /// That second group is the point. The plan spreads an order over the globally cheapest
    /// listings, so a world holding 40 of something may have been allocated 5 — or none. Without
    /// this, a shortfall from an earlier world could never be made up even though the stock is
    /// sitting right here.
    /// </summary>
    private void BuildItemQueue()
    {
        if (_stop == null) { _itemQueue = new List<uint>(); _itemIdx = -1; return; }

        // Only what this stop was allocated. Extension stops carry their own lines, so a top-up
        // pass arrives here as ordinary allocated work.
        _itemQueue = _stop.Lines.Select(l => l.ItemId).Distinct().ToList();
        _itemIdx = -1;
    }

    /// <summary>
    /// End-of-run top-up. Called once the planned route is finished: for anything still short of
    /// its order, take the NEXT cheapest listings the scan found (the ones the plan didn't
    /// allocate — the 23rd, 24th and so on), build fresh stops from them, and append those to the
    /// route so the run simply continues.
    ///
    /// Deliberately at the END, never mid-route. Chasing a shortfall early means paying the
    /// current world's expensive tail while cheaper listings sit unbought two stops away; by the
    /// time the plan is done, every cheap listing it knew about has already been tried.
    ///
    /// Returns true when new stops were appended, in which case the run carries on.
    /// </summary>
    private bool TryExtendPlan()
    {
        if (_plan == null || !Cfg.BuyerTopUpShortfalls) return false;
        if (_extensionPasses >= Math.Max(1, Cfg.BuyerMaxTopUpPasses)) return false;

        var newStops = new Dictionary<string, WorldStop>(StringComparer.OrdinalIgnoreCase);
        var tooDear = 0;

        foreach (var summary in _plan.Items)
        {
            var cfgRow = Cfg.BuyerItems.FirstOrDefault(i => i.ItemId == summary.ItemId);
            var target = cfgRow?.Quantity ?? summary.Requested;
            var need = target - UnitsBoughtFor(summary.ItemId);
            if (need <= 0 || summary.Spare.Count == 0) continue;

            // Price ceiling for the extension. With a cap of your own, that governs. Without one,
            // the dearest price the ORIGINAL plan budgeted is the most you were ever willing to
            // pay — going far past it is a decision for a re-scan, not something to do silently.
            var ceiling = long.MaxValue;
            if (cfgRow?.UseMaxPrice == true) ceiling = cfgRow.MaxPrice;
            else if (Cfg.BuyerTopUpMaxOverPlanPercent > 0 && _plannedMax.TryGetValue(summary.ItemId, out var pm) && pm > 0)
                ceiling = (long)(pm * (1.0 + Cfg.BuyerTopUpMaxOverPlanPercent / 100.0));

            while (need > 0 && summary.Spare.Count > 0)
            {
                var next = summary.Spare[0];
                if (next.UnitPrice > ceiling) { tooDear++; break; }        // sorted — rest are dearer
                if (next.Quantity > need && !Cfg.BuyerAllowOvershoot) break;

                summary.Spare.RemoveAt(0);                                  // consumed, never reused

                if (!newStops.TryGetValue(next.World, out var stop))
                {
                    stop = new WorldStop { World = next.World, DataCenter = next.DataCenter };
                    newStops[next.World] = stop;
                }
                stop.Lines.Add(next);
                need -= next.Quantity;
            }
        }

        if (newStops.Count == 0)
        {
            if (tooDear > 0)
                Log($"Top-up skipped: the remaining listings are above the price rail (plan +{Cfg.BuyerTopUpMaxOverPlanPercent}%). Re-scan if you want them at current prices.");
            return false;
        }

        var here = WorldInfo.CurrentWorld();
        var hereDc = WorldInfo.CurrentDataCenter();
        var ordered = newStops.Values
            .OrderByDescending(w => string.Equals(w.World, here, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(w => string.Equals(w.DataCenter, hereDc, StringComparison.OrdinalIgnoreCase))
            .ThenBy(w => w.DataCenter, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(w => w.TotalCost)
            .ToList();

        foreach (var stop in ordered)
        {
            stop.Lines.Sort((a, b) => a.UnitPrice.CompareTo(b.UnitPrice));
            _plan.Stops.Add(stop);
        }

        _extensionPasses++;
        var units = ordered.Sum(st => st.TotalUnits);
        var cost = ordered.Sum(st => st.TotalCost);
        Log($"Still short — top-up pass {_extensionPasses}: {units} more unit(s) from the next cheapest listings across {ordered.Count} world(s), about {cost:N0}g.");
        Note(AuditKind.Item, $"top-up pass {_extensionPasses}: {units} unit(s) across {ordered.Count} world(s)",
            qty: units, expected: cost);
        return true;
    }

    /// <summary>Dearest unit price the plan actually budgeted for this item, across all stops.</summary>    /// <summary>Dearest unit price the plan actually budgeted for this item, across all stops.</summary>
    private long PlannedMaxUnitPrice(uint itemId)
    {
        if (_plan == null) return 0;
        long max = 0;
        foreach (var stop in _plan.Stops)
            foreach (var line in stop.Lines)
                if (line.ItemId == itemId && line.UnitPrice > max) max = line.UnitPrice;
        return max;
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

    /// <summary>
    /// Rewrite the shopping list to match reality after a REAL run.
    ///
    /// Partly filled rows have what was bought deducted, so a row that wanted 4 Bar Racks and got
    /// 3 comes back wanting 1 — run again later and it buys the remainder, not another four.
    /// Fully filled rows are unticked instead (quantity left intact as a record of the order).
    ///
    /// Never runs on a dry run: nothing was bought, so editing the list would be a lie. Saves
    /// ONCE at the end rather than per purchase, because config writes hit SQLite.
    /// </summary>
    private void UpdateListAfterRun()
    {
        if (DryRun || !Cfg.BuyerAutoDisableCompleted) return;

        var finished = new List<string>();
        var reduced = new List<string>();


        foreach (var row in Cfg.BuyerItems)
        {
            if (!row.Enabled || row.ItemId == 0) continue;

            // Only the units bought SINCE the last write-back. Bought is cumulative across the
            // whole plan, while the row has already been reduced by every previous run — using
            // the cumulative figure here deducts the same purchases again and would untick an
            // order with units still outstanding.
            var got = BoughtFor(row.ItemId);
            var alreadyBanked = _plan?.BankedFor(row.ItemId) ?? 0;
            var fresh = got - alreadyBanked;
            if (fresh <= 0) continue;

            var name = ItemSearch.FindById(row.ItemId);
            if (string.IsNullOrEmpty(name)) name = $"#{row.ItemId}";

            if (_plan != null) _plan.Banked[row.ItemId] = got;

            if (fresh >= row.Quantity)
            {
                row.Enabled = false;
                finished.Add(name);
            }
            else
            {
                var remaining = row.Quantity - fresh;
                row.Quantity = remaining;
                reduced.Add($"{name} x{remaining}");
            }
        }

        if (finished.Count == 0 && reduced.Count == 0) return;
        Cfg.Save();

        // The plan's tally is deliberately left alone. The shopping list is the target for the
        // NEXT scan; the plan tracks progress against the CURRENT one. They count different
        // things, so clearing the tally here would make the Buy Plan window forget purchases you
        // can plainly see in your bags — and blank out its Bought column mid-order.

        if (finished.Count > 0)
            Log(finished.Count <= 6
                ? $"Order filled, unticked: {string.Join(", ", finished)}."
                : $"Order filled for {finished.Count} item(s) — unticked on the shopping list.");

        if (reduced.Count > 0)
            Log(reduced.Count <= 6
                ? $"Still to buy: {string.Join(", ", reduced)}."
                : $"{reduced.Count} item(s) partly filled — the list now shows what's left.");
    }

    private void Finish()
    {
        // Planned route done — if anything is still short, extend and keep going.
        if (TryExtendPlan())
        {
            State = BuyState.NextStop;
            return;
        }

        UpdateListAfterRun();
        var finalSpend = DryRun ? _spent : Math.Max(0, _gilAtStart - MarketBoard.Gil());
        Note(AuditKind.Finish,
            $"{(DryRun ? "dry run" : "run")} complete — {_unitsBought} unit(s), {finalSpend:N0}g, ending gil {MarketBoard.Gil():N0}",
            qty: _unitsBought, actual: finalSpend);
        SaveAudit();

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

    private void Note(AuditKind kind, string detail, string item = "", int qty = 0,
                      long unit = 0, long expected = 0, long actual = 0)
    {
        if (Audit.Count > 4000) return;   // a runaway run must not eat memory
        Audit.Add(new BuyAuditEntry
        {
            Kind = kind,
            World = _stop?.World ?? WorldInfo.CurrentWorld(),
            DataCenter = _stop?.DataCenter ?? string.Empty,
            Item = item,
            Quantity = qty,
            UnitPrice = unit,
            Expected = expected,
            Actual = actual,
            Detail = detail,
        });
    }

    private void SaveAudit()
    {
        if (!Cfg.BuyerWriteAuditLog) return;
        LastAuditPath = BuyAuditWriter.Save(Audit, DryRun);
        if (LastAuditPath != null)
            Log($"Audit log saved: {LastAuditPath}");
    }

    private void Log(string msg)
    {
        Report.Add(msg);
        Status = msg;
        if (Cfg.Verbose) _plugin.Chat($"[Market Helper] {msg}");
    }
}
