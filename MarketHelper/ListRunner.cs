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
    OpenInventory, NextItem, WaitStackSettle, BeginSell, WaitDryPrice, WaitSell, Price, WaitRealPrice, WaitClose, CloseRetainer, PostprocessBackout, WaitClosed, Done, Error,
}

/// <summary>
/// Lists queued items on the market by walking the summoning bell. For each retainer it finds
/// each queued item — in the open retainer's inventory first, then the player's bags (the game
/// offers "Put Up for Sale" on bag items while a retainer's market screen is open at the bell) —
/// opens its sell window via the item context menu, prices it to undercut the market, and confirms.
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
    private uint _justListedItem;   // item just listed, pending stack-settle check
    private (InventoryType Type, ushort Slot)? _justListedLoc;   // where it was listed from
    private readonly Dictionary<uint, long> _runPriceCache = new();   // itemId -> listed price, this run

    // Units listed per item this run, against the optional per-item listing amount. Counts UNITS,
    // not listings, so "list 20 in lots of 6" stops at 20 rather than at 20 listings.
    private readonly Dictionary<uint, int> _listedUnits = new();
    private int _lastListedUnits;

    // 20-listing cap: how many free market slots this retainer has left this run.
    private int _slotsLeft;
    private const int MaxListingsPerRetainer = 20;

    // Run summary: stacks actually listed, and queued items never found in any inventory. The
    // not-found case used to skip SILENTLY (a bare return), which is how 1.17.9.0 cycled nine
    // retainers listing nothing without one explaining message.
    private int _listedStacks;
    private int _notFoundCount;

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

    // Bumped every time the runner advances to a new item. An async price fetch captures the
    // generation it was fired for and ignores its own completion if the runner has moved on —
    // without this, a timed-out (>10s) fetch for item A could land AFTER item B's wait began and
    // hand item B either A's price (real mode: a confirmed listing at the wrong price) or a
    // spurious "data arrived" that truncated B's own wait. Same generation-guard pattern
    // NavRunner got in 1.17.9.0.
    private int _priceGen;

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
        _justListedItem = 0; _justListedLoc = null; _runPriceCache.Clear();
        _listedUnits.Clear();
        _lastListedUnits = 0;
        _retainerIdx = -1;
        _listedStacks = 0;
        _notFoundCount = 0;
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
        _justListedItem = 0; _justListedLoc = null; _runPriceCache.Clear();
        _listedUnits.Clear();
        _lastListedUnits = 0;
        _ticks = 0;
        _listedStacks = 0;
        _notFoundCount = 0;
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

    public void Stop()
    {
        // If AutoRetainer is blocked waiting on our postprocess callback, release it — otherwise
        // AR hangs believing a plugin never finished its pass.
        if (_postprocessMode) { EndPostprocess(); return; }
        State = ListState.Idle;
        Status = "Stopped.";
    }

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
                    if (!DryRun) Log($"Run summary: listed {_listedStacks} stack(s){(_notFoundCount > 0 ? $"; {_notFoundCount} queued item(s) were never found in your bags or any retainer's inventory" : "")}.");
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

            case ListState.WaitStackSettle:
            {
                if (Now < _deadline) return;
                _ticks++;
                // If no listing happened (manual-price branch), just move on.
                if (_justListedItem == 0) { State = ListState.NextItem; return; }
                // Wait until the just-listed stack has actually left its inventory slot (listing is
                // async). This is usually near-instant, so poll quickly with a short cap.
                var stillThere = _justListedLoc != null
                    && RetainerReader.SlotHasItem(_justListedLoc.Value.Type, _justListedLoc.Value.Slot, _justListedItem);
                if (stillThere && _ticks < 12) { Wait(80); return; }   // up to ~1s, fast polling

                // Decide what to do with this item now that a stack was listed.
                var item = _justListedItem;
                _justListedItem = 0;
                _justListedLoc = null;

                // Requested amount reached — leave the rest alone, even though stock remains.
                if (RemainingAllowance(item) <= 0)
                {
                    Log($"{ItemSearch.FindById(item)}: listed the {DesiredListAmount(item)} you asked for — keeping the rest.");
                    _plugin.RemoveListerItem(item);
                    _queue.Remove(item);
                    State = ListState.NextItem;
                    return;
                }

                var stacksRemain = RetainerReader.FindListableItemSlot(item) != null;
                if (!stacksRemain)
                {
                    // Genuinely done with this item — nothing left in inventory.
                    _plugin.RemoveListerItem(item);
                    _queue.Remove(item);
                }
                else if (_slotsLeft > 0)
                {
                    // Room left on this retainer and more stacks to go — list another now.
                    if (!_queue.Contains(item)) _queue.Insert(0, item);
                    Log($"{ItemSearch.FindById(item)}: more stacks remain — listing another.");
                }
                else
                {
                    // Retainer is FULL but stacks remain: DO NOT remove. Keep it queued so the next
                    // retainer continues listing it. (This was the bug that dropped items.)
                    if (!_queue.Contains(item)) _queue.Insert(0, item);
                    Log($"{ItemSearch.FindById(item)}: retainer full — remaining stacks roll to the next retainer.");
                }
                State = ListState.NextItem;
                return;
            }

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
                // Retainer stock first, then the player's bags. The game offers "Put Up for Sale"
                // on bag items while a retainer's market screen is open at the bell (AutoRetainer's
                // quick-sell does exactly this), so 1.17.9.0's retainer-only lookup was a
                // regression: every bag-stocked order skipped with NO message and the run cycled
                // every retainer listing nothing ("0/20 listed" with 49 items queued).
                _currentLoc = RetainerReader.FindListableItemSlot(_currentItem);
                if (_currentLoc == null)
                {
                    // This used to be a bare `return` with no log — 49 invisible skips.
                    Log($"{ItemSearch.FindById(_currentItem)}: not in this retainer's inventory or your bags — skipping (already listed, wrong id, or removed from the game?).");
                    _notFoundCount++;
                    return; // try the next queued item on the next tick
                }
                if (_currentLoc.Value.Type is InventoryType.Inventory1 or InventoryType.Inventory2
                    or InventoryType.Inventory3 or InventoryType.Inventory4)
                {
                    Log($"{ItemSearch.FindById(_currentItem)}: listing from your bags (bag slot {_currentLoc.Value.Slot}).");
                }
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
                    _priceGen++;
                    var gen = _priceGen;
                    var scope = PriceScope();
                    if (string.IsNullOrEmpty(scope))
                    {
                        Log($"[dry] Would list {name} from {_currentLoc.Value.Type} slot {_currentLoc.Value.Slot} (price unknown — location not detected).");
                        FinishDryItem();
                        return;
                    }
                    _ = PreviewPriceAsync(scope, _currentItem, gen);
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
                    Log($"{ItemSearch.FindById(_currentItem)}: no 'Put Up for Sale' entry — skipping (nothing sold). Menu had: {Addons.DumpContextMenu()}");
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
                // Fast path: if we already priced this exact item earlier in this run (e.g. a
                // previous stack of the same 1000-item pile), reuse it — no market re-scan.
                if (_runPriceCache.TryGetValue(_currentItem, out var cachedPrice) && cachedPrice > 0)
                {
                    if (!ApplyStackSize(_currentItem, out var qDetailCached))
                    {
                        Log($"{ItemSearch.FindById(_currentItem)}: {qDetailCached} — skipping rather than listing the wrong amount.");
                        Addons.CloseAddon("RetainerSell");
                        Addons.CloseSearchWindows();
                        State = ListState.NextItem;
                        return;
                    }
                    Addons.SetPriceAndConfirm((int)Math.Min(cachedPrice, int.MaxValue));
                    Log($"{ItemSearch.FindById(_currentItem)}: listed{(string.IsNullOrEmpty(qDetailCached) ? "" : $" {qDetailCached}")} at {cachedPrice:N0}g (same as previous stack).");
                    AfterListed();
                    Addons.CloseSearchWindows();
                    Wait(300);
                    State = ListState.WaitStackSettle;
                    return;
                }

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
                        var gen = _priceGen;
                        _ = RealPriceAsync(scope, _currentItem, gen);
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
                        if (!ApplyStackSize(_currentItem, out var qDetailReal))
                        {
                            Log($"{name}: {qDetailReal} — skipping rather than listing the wrong amount.");
                            Addons.CloseAddon("RetainerSell");
                            Addons.CloseSearchWindows();
                            State = ListState.NextItem;
                            return;
                        }
                        Addons.SetPriceAndConfirm((int)Math.Min(price, int.MaxValue));
                        var extra = string.IsNullOrEmpty(_realPriceNote) ? "" : $" [{_realPriceNote}]";
                        var qtyTagReal = string.IsNullOrEmpty(qDetailReal) ? "" : $" ({qDetailReal})";
                        Log($"{name}: listed{qtyTagReal} at {price:N0}g ({ScopeLabel()} base {_realPriceResult:N0}){extra}.");
                        _runPriceCache[_currentItem] = price;
                        AfterListed();
                    }
                    Addons.CloseSearchWindows();
                    Wait(500);
                    State = ListState.WaitStackSettle;
                }
                Wait(100);
                break;

            case ListState.WaitClose:
                if (Now < _deadline) return;
                _ticks++;
                if (MarketData.ListingsReady() || _ticks > 60)
                {
                    // Only trust the board when the proxy is actually showing OUR item. It keeps the
                    // previous item's rows resident after a new search starts, so without this the
                    // previous item's prices get applied to this one.
                    var listings = MarketData.SearchItemId() == _currentItem
                        ? MarketData.GetListings()
                        : new List<Listing>();
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
                        if (!ApplyStackSize(_currentItem, out var qDetailBoard))
                        {
                            Log($"{name}: {qDetailBoard} — skipping rather than listing the wrong amount.");
                            Addons.CloseAddon("RetainerSell");
                            Addons.CloseSearchWindows();
                            State = ListState.NextItem;
                            return;
                        }
                        Addons.SetPriceAndConfirm((int)Math.Min(price, int.MaxValue));
                        var extra = string.IsNullOrEmpty(note) ? "" : $" [{note}]";
                        var qtyTagBoard = string.IsNullOrEmpty(qDetailBoard) ? "" : $" ({qDetailBoard})";
                        Log($"{name}: listed{qtyTagBoard} at {price:N0}g (base {basePrice:N0}){extra}.");
                        _runPriceCache[_currentItem] = price;
                        AfterListed();
                    }
                    Addons.CloseSearchWindows();
                    Wait(500);
                    State = ListState.WaitStackSettle;
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

    private void Wait(int ms)
    {
        var scale = Math.Clamp(Cfg.SearchPacingMs / 600f, 0.35f, 2.5f);
        _deadline = Now + (int)(ms * scale);
    }

    /// <summary>
    /// Resolve the Universalis location string for the configured pricing scope.
    ///
    /// The Lister tab's manual Data Center / World selectors (shown when auto-detection fails)
    /// write ListerDcOverride / ListerWorldOverride — which nothing read before 1.17.24.0, so a
    /// manually picked world was silently ignored and pricing fell back to a location we couldn't
    /// resolve at all. An override now wins for its scope; empty means auto-detect as before.
    /// </summary>
    private string PriceScope() => Cfg.ListerPriceScope switch
    {
        2 => WorldInfo.CurrentRegion(),
        1 => string.IsNullOrWhiteSpace(Cfg.ListerDcOverride) ? WorldInfo.CurrentDataCenter() : Cfg.ListerDcOverride.Trim(),
        _ => string.IsNullOrWhiteSpace(Cfg.ListerWorldOverride) ? WorldInfo.CurrentWorld() : Cfg.ListerWorldOverride.Trim(),
    };

    private string ScopeLabel() => Cfg.ListerPriceScope switch { 2 => "region", 1 => "DC", _ => "world" };

    /// <summary>
    /// Called after a stack is successfully listed. If more of the same item remains in inventory
    /// and the retainer still has free slots, queue another stack of it (so 1000 items across many
    /// stacks all get listed, not just the first). Otherwise remove it and move to the next item.
    /// </summary>
    private void AfterListed()
    {
        _slotsLeft--;
        _listedStacks++;
        _listedUnits[_currentItem] = ListedSoFar(_currentItem) + Math.Max(1, _lastListedUnits);
        // Record the slot we just listed so the settle check can confirm it actually cleared before
        // we look for more stacks (listing moves the item out of inventory asynchronously).
        _justListedItem = _currentItem;
        _justListedLoc = _currentLoc;
        // Decision (list another stack vs move on) happens in the WaitStackSettle state.
    }

    /// <summary>
    /// Given ascending prices, choose the base price to undercut, in two passes:
    ///
    /// Pass 1 — troll-cluster guard. A price cluster sitting less than 1/Nth of the NEXT cluster
    /// up (N = <see cref="Configuration.ListerTrollPriceMultiple"/>, 0 disables) is a troll floor
    /// and is skipped. Without this, THREE troll listings at 1 gil under a 980 gil wall win the
    /// mode vote and the item lists at 1 gil — seen live on Thick-rimmed Goggles (2026-10-30).
    /// Clusters (distinct prices with counts) are compared, not consecutive rows, so a repeated
    /// troll price can't hide inside its own cluster, and the ratio rule leaves genuine sparse
    /// markets ([100g, 200g] is only 2x) and junk items ([1g, 2g, 3g]) untouched.
    ///
    /// Pass 2 — mode of the survivors: price at the most common listing (the real "wall");
    /// undercutter prices cluster on round numbers, so lone cheaper outliers don't repeat and are
    /// naturally out-voted. Falls back to the cheapest survivor if nothing repeats.
    /// </summary>
    private (long Base, string? Note) ChooseBasePrice(IReadOnlyList<long> ascending)
    {
        if (ascending.Count == 0) return (0, null);
        if (ascending.Count == 1) return (ascending[0], null);

        // Cluster the ascending prices: distinct price -> how many listings carry it.
        var clusters = new List<(long Price, int Count)>();
        foreach (var p in ascending)
        {
            if (clusters.Count > 0 && clusters[^1].Price == p)
                clusters[^1] = (clusters[^1].Price, clusters[^1].Count + 1);
            else
                clusters.Add((p, 1));
        }

        // Pass 1: skip troll floors — a cluster priced below 1/N of the next cluster up.
        var start = 0;
        var skipped = 0;
        long skippedPrice = 0;
        var multiple = Math.Clamp(Cfg.ListerTrollPriceMultiple, 0f, 1000f);
        if (multiple > 0)
        {
            while (start < clusters.Count - 1)
            {
                var cur = clusters[start];
                var next = clusters[start + 1];
                if (cur.Price <= 0 || next.Price <= 0) break;   // zero/nonsense prices: stop guessing
                if (next.Price >= cur.Price * multiple)
                {
                    skipped += cur.Count;
                    skippedPrice = cur.Price;
                    start++;
                    continue;
                }
                break;
            }
        }

        // Pass 2: mode of the survivors; on a tie, prefer the LOWER price (stay competitive).
        long modePrice = clusters[start].Price;
        var modeCount = 0;
        for (var i = start; i < clusters.Count; i++)
        {
            if (clusters[i].Count > modeCount
                || (clusters[i].Count == modeCount && clusters[i].Price < modePrice))
            {
                modeCount = clusters[i].Count;
                modePrice = clusters[i].Price;
            }
        }
        // Nothing repeats among the survivors — there is no wall to price against; use the
        // cheapest survivor, exactly as the old all-distinct fallback did.
        if (modeCount <= 1) modePrice = clusters[start].Price;

        string? note = null;
        if (skipped > 0)
            note = $"skipped {skipped} listing(s) at {skippedPrice:N0}g and below (troll floor)";
        if (modePrice > clusters[start].Price)
            note = (note == null ? "" : note + "; ") + $"priced at the most common listing {modePrice:N0}";
        return (modePrice, note);
    }

    /// <summary>Real-mode price fetch at DC/region scope via Universalis (mirrors the dry preview).</summary>
    private async System.Threading.Tasks.Task RealPriceAsync(string scope, uint itemId, int gen)
    {
        long result = 0;
        string? note = null;
        try
        {
            var res = await Universalis.GetListingsAsync(scope, itemId, 10, false);
            if (res.HasData)
            {
                var prices = res.Listings.Select(l => (long)l.PricePerUnit).OrderBy(p => p).ToList();
                var (basePrice, n) = ChooseBasePrice(prices);
                note = n;
                result = basePrice;
            }
        }
        catch { result = 0; }

        // The runner may have advanced (timeout skip) while this was in flight — a late result
        // for the PREVIOUS item must never be confirmed as the CURRENT item's price.
        if (gen != _priceGen) return;
        _realPriceNote = note;
        _realPriceResult = result;
        _realPricePending = false;
    }

    /// <summary>
    /// Dry-run price preview: query Universalis for the cheapest listing at the given scope
    /// (world or DC) and compute the price we'd list at (lowest − undercut). Runs off-thread;
    /// sets _dryPriceResult and clears _dryPricePending when done.
    /// </summary>
    private async System.Threading.Tasks.Task PreviewPriceAsync(string scope, uint itemId, int gen)
    {
        long result = 0;
        string? sanity = null;
        try
        {
            var res = await Universalis.GetListingsAsync(scope, itemId, 10, false);
            if (res.HasData)
            {
                var prices = res.Listings.Select(l => (long)l.PricePerUnit).OrderBy(p => p).ToList();
                var (basePrice, note) = ChooseBasePrice(prices);
                sanity = note;
                result = System.Math.Max(1, basePrice - Cfg.ListerUndercutBy);
            }
        }
        catch
        {
            result = 0;
        }

        // Same generation guard as RealPriceAsync: never publish a previous item's price.
        if (gen != _priceGen) return;
        _dryPriceSanity = sanity;
        _dryPriceResult = result;
        _dryPricePending = false;
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

    /// <summary>
    /// Units per listing for this item, set per item in the Lister tab.
    /// 0 (the default) means the whole stack in one listing — the game's own behaviour.
    /// </summary>
    private int DesiredStackSize(uint itemId)
        => Cfg.ListerStackSizes.TryGetValue(itemId, out var per) && per > 0 ? per : 0;

    /// <summary>
    /// Set the sell window's quantity before pricing, and VERIFY it took.
    ///
    /// Returns false if the field won't hold the value, in which case the caller must not confirm:
    /// listing a stack of 18 at a per-unit price meant for 1 is an irreversible loss, so a failed
    /// quantity is a reason to skip the item, never to carry on and hope.
    /// </summary>
    /// <summary>
    /// Total units to list for this item in one run. 0 (the default) means everything held.
    /// </summary>
    private int DesiredListAmount(uint itemId)
        => Cfg.ListerListAmounts.TryGetValue(itemId, out var n) && n > 0 ? n : 0;

    /// <summary>Units of this item already listed during this run.</summary>
    private int ListedSoFar(uint itemId)
        => _listedUnits.TryGetValue(itemId, out var n) ? n : 0;

    /// <summary>
    /// Units still allowed for this item, or int.MaxValue when no limit was set.
    /// </summary>
    private int RemainingAllowance(uint itemId)
    {
        var cap = DesiredListAmount(itemId);
        return cap <= 0 ? int.MaxValue : Math.Max(0, cap - ListedSoFar(itemId));
    }

    private bool ApplyStackSize(uint itemId, out string detail)
    {
        detail = string.Empty;

        var max = Addons.ReadMaxQuantity();
        var allowance = RemainingAllowance(itemId);
        var want = DesiredStackSize(itemId);

        // The listing amount trims the LAST listing: asking for 20 in lots of 6 gives 6, 6, 6, 2 —
        // not 6, 6, 6, 6 and two extra sold by accident.
        if (allowance != int.MaxValue)
            want = want <= 0 ? allowance : Math.Min(want, allowance);

        _lastListedUnits = want > 0 ? want : (max > 0 ? max : 1);

        if (want <= 0) return true;                       // whole stack: leave the field alone
        if (max <= 0)
        {
            // A specific per-listing size was asked for but the stack size can't be read, so there
            // is no way to verify the field took it. Listing the whole stack here is exactly the
            // mistake this method exists to prevent, so skip rather than carry on.
            detail = "couldn't read the stack size to verify the listing quantity";
            return false;
        }
        if (want >= max)
        {
            _lastListedUnits = max;
            detail = $"stack of {max}";
            return true;                                  // already the whole stack
        }

        var got = Addons.SetQuantity(want, Cfg.ListerQuantityOpcode);
        if (got != want)
        {
            detail = $"quantity field would not accept {want} (reads {got})";
            return false;
        }
        detail = $"{want} of {max}";
        return true;
    }

    private void Log(string msg) { Report.Add(msg); Status = msg; if (Cfg.Verbose) _plugin.Chat($"[Market Helper] {msg}"); }
}
