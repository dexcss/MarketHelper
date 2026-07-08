using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using ECommons.DalamudServices;

namespace MarketHelper;

public enum ListState
{
    Idle, FindBell, InteractBell, WaitRetainerList, NextRetainer, SelectRetainer, WaitSelectString,
    OpenInventory, NextItem, BeginSell, WaitDryPrice, WaitSell, Price, WaitRealPrice, WaitClose, CloseRetainer, PostprocessBackout, WaitClosed, Done, Error,
}

/// <summary>
/// Lists queued items on the market by walking the summoning bell. For each retainer it finds
/// each queued item in inventory (player + this retainer), opens its sell window via the native
/// RetainerItemCommand, prices it to undercut the market, and confirms.
///
/// DRY RUN by default: logs what it *would* list and the price, without opening/confirming.
/// </summary>
public sealed class ListRunner
{
    private readonly Plugin _plugin;
    private Configuration Cfg => _plugin.Config;

    public ListState State { get; private set; } = ListState.Idle;
    public bool Running => State is not (ListState.Idle or ListState.Done or ListState.Error);
    public string Status { get; private set; } = "Idle.";
    public readonly List<string> Report = new();
    public bool DryRun = true;

    private Dalamud.Game.ClientState.Objects.Types.IGameObject? _bell;
    private int _retainerCount, _retainerIdx;
    private double _deadline;
    private int _ticks;
    private bool _closeActed;

    // Per-retainer queue of item ids still to try.
    private readonly List<uint> _queue = new();
    private uint _currentItem;
    private (InventoryType Type, ushort Slot)? _currentLoc;

    // 20-listing cap: how many free market slots this retainer has left this run.
    private int _slotsLeft;
    private const int MaxListingsPerRetainer = 20;

    // Dry-run only: items already "listed" in the preview, so they don't repeat per retainer.
    private readonly HashSet<uint> _dryListed = new();

    // Dry-run price preview (async Universalis lookup) state.
    private uint _dryPriceItem;
    private string _dryPriceName = string.Empty;
    private (InventoryType Type, ushort Slot) _dryPriceLoc;
    private volatile bool _dryPricePending;
    private long _dryPriceResult;   // computed listing price, or 0 if no data
    private string? _dryPriceSanity; // note if the sanity check overrode the raw lowest

    // Real-mode async pricing (DC/region scope via Universalis).
    private volatile bool _realPricePending;
    private long _realPriceResult;
    private string? _realPriceNote;

    // AutoRetainer postprocess mode: AR already has the retainer open; list preset items on THIS
    // retainer only, then back out to SelectString and signal AR via the callback.
    private bool _postprocessMode;
    private Action? _onPostprocessDone;

    private static double Now => Environment.TickCount64;
    private bool AtBell => Svc.Condition[ConditionFlag.OccupiedSummoningBell];

    public ListRunner(Plugin plugin) => _plugin = plugin;

    public void Start(bool dryRun)
    {
        if (_plugin.AllListerItems().Count == 0) { SetError("No items queued."); return; }
        DryRun = dryRun;
        Report.Clear();
        _dryListed.Clear();
        _retainerIdx = -1;
        State = ListState.FindBell;
        Status = dryRun ? "Dry run: looking for bell..." : "Listing: looking for bell...";
        if (!dryRun)
            Log("Real listing uses the market-board 'Put Up for Sale' menu (selected by name — never the vendor 'Sell' option).");
    }

    /// <summary>
    /// AutoRetainer integration: AR already opened this retainer (we're at its SelectString menu).
    /// List any preset items found in inventory on this retainer only, then return to SelectString
    /// and call onDone. Never touches the bell, retainer list, or Quit.
    /// </summary>
    public void StartPostprocess(string retainerName, Action onDone)
    {
        if (Running || _plugin.AllListerItems().Count == 0) { onDone(); return; }
        DryRun = false;
        _postprocessMode = true;
        _onPostprocessDone = onDone;
        Report.Clear();
        _dryListed.Clear();
        _ticks = 0;
        // Build this retainer's queue and free-slot count, then enter the sell screen.
        _queue.Clear();
        _queue.AddRange(_plugin.AllListerItems());
        var current = RetainerReader.ActiveMarketItems();
        _slotsLeft = Math.Max(0, MaxListingsPerRetainer - current);
        Log($"AutoRetainer auto-list: {retainerName}, {_slotsLeft} free slot(s).");
        if (_slotsLeft == 0) { EndPostprocess(); return; }
        State = ListState.OpenInventory;
    }

    private void EndPostprocess()
    {
        var done = _onPostprocessDone;
        _postprocessMode = false;
        _onPostprocessDone = null;
        State = ListState.Idle;
        Status = "Idle.";
        done?.Invoke();
    }

    public void Stop() { State = ListState.Idle; Status = "Stopped."; }

    public void Tick()
    {
        if (!Running) return;
        try { Step(); }
        catch (Exception ex) { _plugin.Log.Error(ex, "Lister failed"); SetError($"Exception: {ex.Message}"); }
    }

    private void Step()
    {
        switch (State)
        {
            case ListState.FindBell:
                _bell = Bell.GetNearest(out var dist);
                if (_bell == null || dist > 20f) { SetError("No summoning bell within range."); return; }
                State = ListState.InteractBell;
                break;

            case ListState.InteractBell:
                Status = "Opening the summoning bell...";
                if (_bell == null || !Bell.Interact(_bell)) { State = ListState.FindBell; return; }
                Wait(500);
                State = ListState.WaitRetainerList;
                break;

            case ListState.WaitRetainerList:
                if (Now < _deadline) return;
                if (AtBell && Addons.IsVisible("RetainerList"))
                {
                    _retainerCount = RetainerReader.Count;
                    Log($"Bell open. {_retainerCount} retainer(s). {_plugin.AllListerItems().Count} item(s) queued.");
                    State = ListState.NextRetainer;
                    return;
                }
                if (Now > _deadline + 8000) { SetError("RetainerList didn't open."); return; }
                break;

            case ListState.NextRetainer:
                _retainerIdx++;
                // Stop as soon as there's nothing left to list. Real mode removes listed items from
                // the config queue; dry run tracks them in _dryListed. Check remaining accordingly.
                var remaining = DryRun
                    ? _plugin.AllListerItems().Count(id => !_dryListed.Contains(id))
                    : _plugin.AllListerItems().Count;
                if (_retainerIdx >= _retainerCount || remaining == 0)
                {
                    Status = "Done.";
                    Log(remaining == 0
                        ? (DryRun ? "Dry run complete — all items accounted for." : "All items listed. Done.")
                        : (DryRun ? "Dry run complete." : "Finished (ran out of retainers with free slots)."));
                    _ticks = 0;
                    State = ListState.WaitClosed;   // close the retainer window / bell, then stop
                    return;
                }
                if (!Addons.IsVisible("RetainerList")) { Wait(150); return; }
                // Settle before selecting — the game rejects a too-fast re-summon right after
                // closing the previous retainer ("cannot summon that retainer" error).
                Wait(500);
                State = ListState.SelectRetainer;
                break;

            case ListState.SelectRetainer:
                if (Now < _deadline) return;
                if (!Addons.IsVisible("RetainerList"))
                {
                    _retainerIdx--;
                    _closeActed = false; _ticks = 0;
                    State = ListState.CloseRetainer;
                    return;
                }
                Addons.SelectRetainer(_retainerIdx);
                Wait(600);
                State = ListState.WaitSelectString;
                break;

            case ListState.WaitSelectString:
                if (Now < _deadline) return;
                if (Addons.AdvanceTalk()) { Wait(150); return; }
                if (Addons.IsReady("SelectString") || Addons.IsReady("RetainerSellList"))
                {
                    // Prepare the queue for this retainer (items still queued, minus any already
                    // handled this run — real mode removes from config; dry run tracks locally).
                    _queue.Clear();
                    _queue.AddRange(_plugin.AllListerItems().Where(id => !_dryListed.Contains(id)));
                    // 20-item market cap: only as many free slots as this retainer has left.
                    var current = RetainerReader.ActiveMarketItems();
                    _slotsLeft = Math.Max(0, MaxListingsPerRetainer - current);
                    Log($"Retainer {_retainerIdx + 1}: {current}/{MaxListingsPerRetainer} listed, {_slotsLeft} slot(s) free.");
                    if (_slotsLeft == 0)
                    {
                        _closeActed = false; _ticks = 0;
                        State = ListState.CloseRetainer;
                        return;
                    }
                    // Real mode needs the retainer inventory window open for the native sell
                    // command to work. Dry run only reads prices, so it skips this.
                    if (DryRun)
                    {
                        State = ListState.NextItem;
                        return;
                    }
                    State = ListState.OpenInventory;
                    _ticks = 0;
                    return;
                }
                if (Now > _deadline + 10000) { SetError("Retainer menu didn't open."); return; }
                break;

            case ListState.OpenInventory:
                if (Now < _deadline) return;
                if (Addons.AdvanceTalk()) { Wait(150); return; }
                // The market-listing context needs the "Sell items" screen (RetainerSellList) open.
                if (Addons.IsReady("RetainerSellList"))
                {
                    State = ListState.NextItem;
                    return;
                }
                // Select "Sell items on the market" on the SelectString menu (entry 3 in the
                // standard retainer menu; falls back to text match).
                if (Addons.IsVisible("SelectString"))
                {
                    if (Addons.OpenSellOnMarket())
                    {
                        Wait(800);
                        _ticks = 0;
                        return;
                    }
                }
                _ticks++;
                if (_ticks > 60)
                {
                    Log($"Retainer {_retainerIdx + 1}: couldn't open the sell-items screen, skipping retainer.");
                    _closeActed = false; _ticks = 0;
                    State = ListState.CloseRetainer;
                    return;
                }
                Wait(200);
                break;

            case ListState.NextItem:
                // Retainer full, or nothing left queued for it — close and move on.
                if (_queue.Count == 0 || _slotsLeft <= 0)
                {
                    if (_slotsLeft <= 0 && _queue.Count > 0 && !_postprocessMode)
                        Log($"Retainer {_retainerIdx + 1} full ({MaxListingsPerRetainer} items); remaining items go to the next retainer.");
                    _closeActed = false; _ticks = 0;
                    // In AR mode, back out to SelectString and release AR (no bell walk).
                    State = _postprocessMode ? ListState.PostprocessBackout : ListState.CloseRetainer;
                    return;
                }
                _currentItem = _queue[0];
                _queue.RemoveAt(0);
                _currentLoc = RetainerReader.FindItemSlot(_currentItem, includeRetainer: true);
                if (_currentLoc == null)
                    return; // not on this retainer/inventory; try next queued item
                State = ListState.BeginSell;
                break;

            case ListState.BeginSell:
            {
                var name = ItemSearch.FindById(_currentItem);
                if (DryRun)
                {
                    // Price preview via Universalis (our world) — no in-game action at all.
                    _dryPriceItem = _currentItem;
                    _dryPriceName = name;
                    _dryPriceLoc = _currentLoc!.Value;
                    _dryPriceResult = 0;
                    _dryPricePending = true;
                    var scope = PriceScope();
                    if (string.IsNullOrEmpty(scope))
                    {
                        Log($"[dry] Would list {name} from {_currentLoc.Value.Type} slot {_currentLoc.Value.Slot} (price unknown — location not detected).");
                        FinishDryItem();
                        return;
                    }
                    _ = PreviewPriceAsync(scope, _currentItem);
                    State = ListState.WaitDryPrice;
                    _ticks = 0;
                    Wait(100);
                    return;
                }
                if (!RetainerSellCommand.OpenItemContext(_currentLoc!.Value.Type, _currentLoc.Value.Slot))
                {
                    Log($"{name}: couldn't open item context (retainer inventory not ready), skipping.");
                    State = ListState.NextItem;
                    return;
                }
                Wait(400);
                _ticks = 0;
                State = ListState.WaitSell;   // wait for ContextMenu, then pick "Put Up for Sale"
                break;
            }

            case ListState.WaitDryPrice:
                if (Now < _deadline) return;
                _ticks++;
                if (!_dryPricePending)
                {
                    FinishDryItem();
                    return;
                }
                if (_ticks > 100) // ~10s timeout on the Universalis call
                {
                    Log($"[dry] Would list {_dryPriceName} from {_dryPriceLoc.Type} slot {_dryPriceLoc.Slot} (price lookup timed out).");
                    _dryPricePending = false;
                    FinishDryItem();
                    return;
                }
                Wait(100);
                break;

            case ListState.WaitSell:
                if (Now < _deadline) return;
                _ticks++;
                // Sell window already open? price it.
                if (Addons.IsReady("RetainerSell")) { State = ListState.Price; return; }
                // Look for the inventory context menu and select "Put Up for Sale" BY NAME.
                if (Addons.Exists("ContextMenu"))
                {
                    if (RetainerSellCommand.SelectPutUpForSale())
                    {
                        Wait(700);
                        return; // wait for RetainerSell to open
                    }
                    Log($"{ItemSearch.FindById(_currentItem)}: no 'Put Up for Sale' entry — skipping (nothing sold).");
                    Addons.CloseAddon("ContextMenu");
                    State = ListState.NextItem;
                    return;
                }
                if (_ticks > 60)
                {
                    Log($"{ItemSearch.FindById(_currentItem)}: sell window didn't open, skipping.");
                    State = ListState.NextItem;
                }
                break;

            case ListState.Price:
            {
                // Price source must match the chosen scope. The live in-game Compare-Prices board
                // only shows your own world/DC — it CANNOT see region prices. So:
                //   world scope  -> live board (most accurate for your world)
                //   DC / region  -> Universalis at that scope (board can't provide it)
                if (Cfg.ListerPriceScope == 0)
                {
                    Addons.FireComparePrices();
                    Wait(1200);
                    _ticks = 0;
                    State = ListState.WaitClose;
                }
                else
                {
                    // Async Universalis fetch at the DC/region scope.
                    _realPricePending = true;
                    _realPriceResult = 0;
                    _realPriceNote = null;
                    var scope = PriceScope();
                    if (string.IsNullOrEmpty(scope))
                    {
                        // Fall back to the live board if we couldn't resolve the scope name.
                        Addons.FireComparePrices();
                        Wait(1200);
                        _ticks = 0;
                        _realPricePending = false;
                        State = ListState.WaitClose;
                    }
                    else
                    {
                        _ = RealPriceAsync(scope, _currentItem);
                        _ticks = 0;
                        Wait(100);
                        State = ListState.WaitRealPrice;
                    }
                }
                break;
            }

            case ListState.WaitRealPrice:
                if (Now < _deadline) return;
                _ticks++;
                if (!_realPricePending || _ticks > 100)
                {
                    var name = ItemSearch.FindById(_currentItem);
                    long price = _realPriceResult > 0 ? Math.Max(1, _realPriceResult - Cfg.ListerUndercutBy) : 0;
                    if (price <= 0)
                    {
                        Log($"{name}: no {ScopeLabel()} market data — leaving sell window open for manual pricing.");
                    }
                    else
                    {
                        Addons.SetPriceAndConfirm((int)Math.Min(price, int.MaxValue));
                        var extra = string.IsNullOrEmpty(_realPriceNote) ? "" : $" [{_realPriceNote}]";
                        Log($"{name}: listed at {price:N0}g ({ScopeLabel()} base {_realPriceResult:N0}){extra}.");
                        _slotsLeft--;
                        _plugin.RemoveListerItem(_currentItem);
                    }
                    Addons.CloseSearchWindows();
                    Wait(500);
                    State = ListState.NextItem;
                }
                Wait(100);
                break;

            case ListState.WaitClose:
                if (Now < _deadline) return;
                _ticks++;
                if (MarketData.ListingsReady() || _ticks > 60)
                {
                    var listings = MarketData.GetListings();
                    var name = ItemSearch.FindById(_currentItem);
                    var ascending = listings.Select(l => (long)l.Price).OrderBy(p => p).ToList();
                    var (basePrice, note) = ChooseBasePrice(ascending);
                    long price = basePrice > 0 ? Math.Max(1, basePrice - Cfg.ListerUndercutBy) : 0;

                    if (price <= 0)
                    {
                        Log($"{name}: no market data — leaving sell window open for manual pricing.");
                    }
                    else
                    {
                        Addons.SetPriceAndConfirm((int)Math.Min(price, int.MaxValue));
                        var extra = string.IsNullOrEmpty(note) ? "" : $" [{note}]";
                        Log($"{name}: listed at {price:N0}g (base {basePrice:N0}){extra}.");
                        _slotsLeft--;   // used one of this retainer's 20 slots
                        _plugin.RemoveListerItem(_currentItem);
                    }
                    Addons.CloseSearchWindows();
                    Wait(500);
                    State = ListState.NextItem;
                }
                break;

            case ListState.PostprocessBackout:
                if (Now < _deadline) return;
                if (Addons.AdvanceTalk()) { Wait(150); return; }
                if (Addons.IsReady("SelectString"))
                {
                    Log("AutoRetainer auto-list done; handing back to AutoRetainer.");
                    EndPostprocess();
                    return;
                }
                if (Addons.IsVisible("RetainerSellList")) { Addons.CloseAddon("RetainerSellList"); Wait(500); return; }
                if (Addons.IsVisible("RetainerSell")) { Addons.CloseAddon("RetainerSell"); Wait(400); return; }
                _ticks++;
                if (_ticks > 60) { Log("AutoRetainer auto-list: couldn't cleanly return; releasing anyway."); EndPostprocess(); return; }
                Wait(200);
                break;

            case ListState.CloseRetainer:
                if (Now < _deadline) return;
                if (Addons.IsVisible("RetainerList")) { _closeActed = false; State = ListState.NextRetainer; return; }
                if (Addons.AdvanceTalk()) { Wait(200); break; }
                if (!_closeActed)
                {
                    if (Addons.IsVisible("RetainerSellList")) { Addons.CloseAddon("RetainerSellList"); _closeActed = true; Wait(600); break; }
                    if (Addons.IsVisible("SelectString")) { Addons.QuitRetainer(); _closeActed = true; Wait(800); break; }
                    if (Addons.IsVisible("RetainerSell")) { Addons.CloseAddon("RetainerSell"); _closeActed = true; Wait(400); break; }
                    Wait(200); break;
                }
                if (Addons.IsVisible("SelectString") || Addons.IsVisible("RetainerSellList") || Addons.IsVisible("RetainerSell"))
                { _closeActed = false; break; }
                _ticks++;
                if (_ticks > 100) { SetError("Couldn't return to retainer list."); return; }
                Wait(200);
                break;

            case ListState.WaitClosed:
                if (Now < _deadline) return;
                // Close the retainer list to end the bell session, then finish.
                if (Addons.IsVisible("RetainerList"))
                {
                    Addons.CloseAddon("RetainerList");
                    Wait(400);
                    _ticks++;
                    if (_ticks > 20) { State = ListState.Done; return; }
                    return;
                }
                State = ListState.Done;
                break;
        }
    }

    private void Wait(int ms) => _deadline = Now + ms;

    /// <summary>Resolve the Universalis location string for the configured pricing scope.</summary>
    private string PriceScope() => Cfg.ListerPriceScope switch
    {
        2 => WorldInfo.CurrentRegion(),
        1 => WorldInfo.CurrentDataCenter(),
        _ => WorldInfo.CurrentWorld(),
    };

    private string ScopeLabel() => Cfg.ListerPriceScope switch { 2 => "region", 1 => "DC", _ => "world" };

    /// <summary>
    /// Given ascending prices, choose the base price to undercut, applying the outlier gap check:
    /// Prices at the mode (most common listing price); lone cheaper outliers are ignored.
    /// price against the next one (repeatedly, in case several trolls stack). Returns the base
    /// price and an optional note describing any skip.
    /// </summary>
    private (long Base, string? Note) ChooseBasePrice(IReadOnlyList<long> ascending)
    {
        if (ascending.Count == 0) return (0, null);
        if (ascending.Count == 1) return (ascending[0], null);

        // Price at the MODE — the most common price among the listings. Undercutters cluster on the
        // same round numbers, so the mode is the real "wall" price; lone low outliers (trolls or
        // stale listings) don't repeat and are naturally ignored. Falls back to the cheapest if no
        // price repeats at all.
        var counts = new Dictionary<long, int>();
        foreach (var p in ascending)
            counts[p] = counts.TryGetValue(p, out var c) ? c + 1 : 1;

        long modePrice = ascending[0];
        var modeCount = 0;
        foreach (var kv in counts)
        {
            // Prefer the most frequent price; on a tie, prefer the LOWER price (stay competitive).
            if (kv.Value > modeCount || (kv.Value == modeCount && kv.Key < modePrice))
            {
                modeCount = kv.Value;
                modePrice = kv.Key;
            }
        }

        // If nothing repeats (every listing unique), there's no cluster — just use the cheapest.
        if (modeCount <= 1)
            return (ascending[0], null);

        string? note = modePrice > ascending[0]
            ? $"priced at the most common listing {modePrice:N0} (skipped {ascending[0]:N0} and other cheaper outliers)"
            : null;
        return (modePrice, note);
    }

    /// <summary>Real-mode price fetch at DC/region scope via Universalis (mirrors the dry preview).</summary>
    private async System.Threading.Tasks.Task RealPriceAsync(string scope, uint itemId)
    {
        try
        {
            var res = await Universalis.GetListingsAsync(scope, itemId, 10, false);
            if (res.HasData)
            {
                var prices = res.Listings.Select(l => (long)l.PricePerUnit).OrderBy(p => p).ToList();
                var (basePrice, note) = ChooseBasePrice(prices);
                _realPriceNote = note;
                _realPriceResult = basePrice;
            }
            else _realPriceResult = 0;
        }
        catch { _realPriceResult = 0; }
        finally { _realPricePending = false; }
    }

    /// <summary>
    /// Dry-run price preview: query Universalis for the cheapest listing at the given scope
    /// (world or DC) and compute the price we'd list at (lowest − undercut). Runs off-thread;
    /// sets _dryPriceResult and clears _dryPricePending when done.
    /// </summary>
    private async System.Threading.Tasks.Task PreviewPriceAsync(string scope, uint itemId)
    {
        try
        {
            var res = await Universalis.GetListingsAsync(scope, itemId, 10, false);
            if (res.HasData)
            {
                var prices = res.Listings.Select(l => (long)l.PricePerUnit).OrderBy(p => p).ToList();
                var (basePrice, note) = ChooseBasePrice(prices);
                _dryPriceSanity = note;
                _dryPriceResult = System.Math.Max(1, basePrice - Cfg.ListerUndercutBy);
            }
            else
            {
                _dryPriceResult = 0;
            }
        }
        catch
        {
            _dryPriceResult = 0;
        }
        finally
        {
            _dryPricePending = false;
        }
    }

    /// <summary>Log the dry-run preview line for the current item and advance.</summary>
    private void FinishDryItem()
    {
        var loc = $"{_dryPriceLoc.Type} slot {_dryPriceLoc.Slot}";
        if (_dryPriceResult > 0)
        {
            var note = string.IsNullOrEmpty(_dryPriceSanity) ? "" : $" [{_dryPriceSanity}]";
            Log($"[dry] {_dryPriceName}: would list at {_dryPriceResult:N0}g (from {loc}; {ScopeLabel()} base − {Cfg.ListerUndercutBy} undercut){note}.");
        }
        else
        {
            Log($"[dry] {_dryPriceName}: would list from {loc} — no market data, price set manually.");
        }
        _dryListed.Add(_dryPriceItem);
        _slotsLeft--;
        State = ListState.NextItem;
    }

    private void SetError(string msg)
    {
        State = ListState.Error; Status = msg; _plugin.Chat($"[Market Helper] {msg}");
        if (_postprocessMode) { var d = _onPostprocessDone; _postprocessMode = false; _onPostprocessDone = null; d?.Invoke(); }
    }
    private void Log(string msg) { Report.Add(msg); Status = msg; if (Cfg.Verbose) _plugin.Chat($"[Market Helper] {msg}"); }
}
