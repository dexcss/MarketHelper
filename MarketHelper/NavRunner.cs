using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.DalamudServices;

namespace MarketHelper;

public enum NavState
{
    Idle,
    FindBell,
    InteractBell,
    WaitRetainerList,
    NextRetainer,
    SelectRetainer,
    WaitSelectString,
    EnterSellMenu,
    WaitSellList,
    NextItem,
    OpenItem,
    WaitContextOrSell,
    WaitRetainerSell,
    Search,
    WaitSearch,
    Price,
    WaitPriceClose,
    CloseRetainer,
    PostprocessBackout,
    WaitClosed,
    Done,
    Error,
}

/// <summary>
/// Full standalone flow, replicating the MarketBotty SND script: find and open the summoning
/// bell, walk every retainer on it, and undercut each listed item. Tick-driven (non-blocking),
/// the C# equivalent of the script's WaitFor/goto loop.
///
/// Every callback value is taken from the working script; bell interaction is ported from
/// AutoRetainer's verified primitives. Prices are read from InfoProxyItemSearch (cleaner than
/// the script's node scraping).
/// </summary>
public sealed class NavRunner
{
    private readonly Plugin _plugin;
    private Configuration Cfg => _plugin.Config;

    public NavState State { get; private set; } = NavState.Idle;
    public bool Running => State is not (NavState.Idle or NavState.Done or NavState.Error);
    public string Status { get; private set; } = "Idle.";
    public readonly List<string> Report = new();

    private IGameObject? _bell;
    private int _retainerCount;
    private int _retainerIdx;       // 0-based sorted index
    private int _itemCount;
    private int _itemSlot;          // 0-based
    private double _deadline;
    private int _ticks;
    private uint _lastPricedFirst;  // lowest price seen on the last completed item
    private bool _refired;
    private uint _stableProbe = uint.MaxValue; // last-seen lowest price, to confirm stability across polls
    private int _stableCount;       // consecutive polls the lowest price has held steady
    private string _openItem = string.Empty;
    private string _keyName = string.Empty;  // stable per-item memory key (raw node name)
    private bool _openHq;
    private string _nameProbe = string.Empty;
    private bool _closeActed;   // fired an exit action this close-cycle; wait before acting again

    // AutoRetainer postprocess mode: AR has already opened a retainer; we only price the currently
    // open retainer's items, then return the UI to the SelectString menu and signal AR via the
    // callback. We do NOT touch the bell, retainer selection, or Quit in this mode.
    private bool _postprocessMode;
    private Action? _onPostprocessDone;

    // Session price memory: once an item is searched and priced, remember the result so any
    // later identical item (same name + HQ) this run is set instantly without re-searching.
    // Mirrors the script's repeat behaviour, extended across the whole run. Cleared on Start.
    private readonly Dictionary<string, (int Price, string Name)> _priceMemory = new();

    private static double Now => Environment.TickCount64;
    private bool AtBell => Svc.Condition[ConditionFlag.OccupiedSummoningBell];

    public NavRunner(Plugin plugin) => _plugin = plugin;

    public void Start()
    {
        Report.Clear();
        _priceMemory.Clear();
        _retainerIdx = -1;
        if (Addons.Exists("RetainerList"))
        {
            // Already at the bell — skip straight to walking retainers.
            InitRetainers();
            State = NavState.NextRetainer;
            Log($"Bell already open. {_retainerCount} retainer(s) to process.");
        }
        else
        {
            State = NavState.FindBell;
            Status = "Looking for a summoning bell...";
        }
    }

    public void Stop()
    {
        State = NavState.Idle;
        Status = "Stopped.";
    }

    /// <summary>
    /// AutoRetainer integration entry point. AR has already summoned the bell and opened this
    /// retainer (we're at its SelectString menu). Price only this retainer's listings, then return
    /// to the SelectString menu and call onDone so AR can proceed to ventures. Never touches the
    /// bell, retainer list, or Quit.
    /// </summary>
    public void StartPostprocess(string retainerName, Action onDone)
    {
        if (Running)
        {
            // Busy with a manual run — don't interfere; just release AR immediately.
            onDone();
            return;
        }
        Report.Clear();
        _priceMemory.Clear();
        _postprocessMode = true;
        _onPostprocessDone = onDone;
        _itemSlot = 0;
        _ticks = 0;
        Status = $"AutoRetainer: undercutting {retainerName}...";
        Log($"AutoRetainer postprocess: undercutting {retainerName}.");
        // AR leaves us at the retainer's SelectString menu; enter the sell-items screen.
        State = NavState.EnterSellMenu;
    }

    /// <summary>Finish postprocess: return to SelectString (where AR resumes) and release AR.</summary>
    private void EndPostprocess()
    {
        var done = _onPostprocessDone;
        _postprocessMode = false;
        _onPostprocessDone = null;
        State = NavState.Idle;
        Status = "Idle.";
        done?.Invoke();
    }

    public void Tick()
    {
        if (!Running) return;
        try { Step(); }
        catch (Exception ex)
        {
            _plugin.Log.Error(ex, "MarketHelper nav failed");
            SetError($"Exception: {ex.Message}");
        }
    }

    private void Step()
    {
        switch (State)
        {
            case NavState.FindBell:
                _bell = Bell.GetNearest(out var dist);
                if (_bell == null || dist > 20f)
                {
                    SetError("No summoning bell within range. Stand near a bell and try again.");
                    return;
                }
                State = NavState.InteractBell;
                break;

            case NavState.InteractBell:
                Status = "Opening the summoning bell...";
                if (_bell == null || !Bell.Interact(_bell)) { State = NavState.FindBell; return; }
                Wait(300);
                State = NavState.WaitRetainerList;
                break;

            case NavState.WaitRetainerList:
                if (Now < _deadline) return;
                if (AtBell && Addons.IsVisible("RetainerList"))
                {
                    InitRetainers();
                    Log($"Bell open. {_retainerCount} retainer(s) to process.");
                    State = NavState.NextRetainer;
                    return;
                }
                if (Now > _deadline + 8000) { SetError("RetainerList didn't open."); return; }
                break;

            case NavState.NextRetainer:
                _retainerIdx++;
                if (_retainerIdx >= _retainerCount)
                {
                    State = NavState.Done;
                    Status = $"Done. Processed {_retainerCount} retainer(s).";
                    Log("Finished all retainers.");
                    return;
                }
                // Must be at the RetainerList to select the next one. If not, keep backing out.
                if (!Addons.IsVisible("RetainerList"))
                {
                    _retainerIdx--;        // undo; we'll retry this same index
                    _ticks = 0;
                    _closeActed = false;
                    State = NavState.CloseRetainer;
                    return;
                }
                // Let the list settle before selecting — the game rejects a too-fast re-summon
                // right after closing the previous retainer (script waits 0.3s here).
                Wait(500);
                State = NavState.SelectRetainer;
                break;

            case NavState.SelectRetainer:
                if (Now < _deadline) return;
                if (!Addons.IsVisible("RetainerList"))
                {
                    _retainerIdx--;
                    _ticks = 0;
                    _closeActed = false;
                    State = NavState.CloseRetainer;
                    return;
                }
                var rname = RetainerReader.NameAtSorted(_retainerIdx);
                if (IsBlacklisted(rname))
                {
                    Log($"Skipping blacklisted retainer: {rname}");
                    State = NavState.NextRetainer;
                    return;
                }
                Status = $"Opening retainer {_retainerIdx + 1}/{_retainerCount}: {rname}";
                if (Cfg.Verbose) _plugin.Chat($"[Market Helper] Selecting retainer index {_retainerIdx} (name: '{rname}')");
                Addons.SelectRetainer(_retainerIdx);
                Wait(500);
                State = NavState.WaitSelectString;
                break;

            case NavState.WaitSelectString:
                if (Now < _deadline) return;
                // Dismiss the retainer greeting bubble (Talk) if it's up (script does this).
                if (Addons.AdvanceTalk()) { Wait(120); return; }
                if (Addons.IsReady("SelectString"))
                {
                    State = NavState.EnterSellMenu;
                    return;
                }
                // Sometimes RetainerSellList opens directly; handle that.
                if (Addons.IsReady("RetainerSellList")) { StartItems(); return; }
                if (Now > _deadline + 12000) { SetError("Retainer menu (SelectString) didn't open."); return; }
                break;

            case NavState.EnterSellMenu:
                // Entry 3 = "Sell items on the market" (script: SafeCallback SelectString true, 3)
                Addons.SelectStringEntry(3);
                Wait(150);
                State = NavState.WaitSellList;
                break;

            case NavState.WaitSellList:
                if (Now < _deadline) return;
                if (Addons.IsReady("RetainerSellList")) { StartItems(); return; }
                if (Now > _deadline + 8000) { SetError("RetainerSellList didn't open."); return; }
                break;

            case NavState.NextItem:
                if (_itemSlot >= _itemCount)
                {
                    _ticks = 0;
                    _closeActed = false;
                    // In AutoRetainer mode, return only to the SelectString menu (where AR resumes)
                    // and release AR — do NOT Quit back to the retainer list.
                    State = _postprocessMode ? NavState.PostprocessBackout : NavState.CloseRetainer;
                    return;
                }
                if (!Addons.IsReady("RetainerSellList")) { Wait(100); return; }

                // Skip mannequin / display items WITHOUT opening them. Primary signal is the game's
                // own mannequin icon on the sell-list row (deterministic). Fallback is the price
                // threshold. Opening a mannequin item risks a different context menu whose first
                // entry can delist it, so we never open them.
                if (Cfg.SkipMannequinItems)
                {
                    var isMannequinRow = Addons.IsSellListRowMannequin(_itemSlot);
                    var slotPrice = RetainerReader.MarketPriceAtSlot(_itemSlot);
                    var overThreshold = Cfg.MannequinUsePriceFallback && slotPrice >= (ulong)Cfg.MannequinPriceThreshold;
                    if (Cfg.Verbose)
                        _plugin.Chat($"[Market Helper] slot {_itemSlot}: icon={isMannequinRow}, price={slotPrice:N0}");
                    if (isMannequinRow || overThreshold)
                    {
                        var why = isMannequinRow ? "mannequin icon" : $"{slotPrice:N0}g over threshold";
                        Log($"Item {_itemSlot + 1}: {why} — display item, skipping (not opened).");
                        _itemSlot++;
                        Wait(60);
                        return; // stay in NextItem for the next slot
                    }
                }

                Status = $"Retainer {_retainerIdx + 1}: item {_itemSlot + 1}/{_itemCount}";
                Addons.OpenSellListItem(_itemSlot);
                Wait(120);
                State = NavState.WaitContextOrSell;
                break;

            case NavState.WaitContextOrSell:
                if (Now < _deadline) return;
                if (Addons.IsReady("RetainerSell")) { _ticks = 0; _nameProbe = string.Empty; State = NavState.WaitRetainerSell; return; }

                if (Addons.Exists("ContextMenu"))
                {
                    // HARD SAFETY: before clicking the context menu (whose first entry we assume is
                    // "Adjust Price"), verify this slot's price isn't in mannequin territory. The
                    // direct price read (GetRetainerMarketPrice) is proven reliable. If the item is
                    // a display piece, clicking entry 0 could delist it — so we cancel and skip.
                    // This guard always runs, independent of the user's skip-preference toggles.
                    var guardPrice = RetainerReader.MarketPriceAtSlot(_itemSlot);
                    if (guardPrice >= (ulong)Cfg.MannequinSafetyPrice)
                    {
                        Log($"Item {_itemSlot + 1}: {guardPrice:N0}g at context menu — display item, cancelling (not clicking).");
                        Addons.CloseAddon("ContextMenu");
                        _itemSlot++;
                        Wait(250);
                        State = NavState.NextItem;
                        return;
                    }
                    Addons.ContextMenuFirst();
                    Wait(120);
                    return;
                }
                if (Now > _deadline + 5000)
                {
                    Log($"Item {_itemSlot + 1}: sell window didn't open, skipping.");
                    _itemSlot++;
                    State = NavState.NextItem;
                }
                break;

            case NavState.WaitRetainerSell:
                if (!Addons.IsReady("RetainerSell")) { Wait(100); return; }

                // The item-name node can still hold the PREVIOUS item's text for a frame or two
                // after the window is "ready". Reading it too early — then hitting price memory on
                // that stale name — assigns the wrong price. Require the name to be non-empty and
                // stable across two checks before trusting it.
                {
                    var name = Addons.GetOpenItemName();
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        if (_ticks++ > 40) { Log("Item name never loaded, skipping."); _itemSlot++; State = NavState.NextItem; }
                        Wait(80);
                        return;
                    }
                    if (name != _nameProbe)
                    {
                        // Name changed since last check — not settled yet. Store and re-check.
                        _nameProbe = name;
                        Wait(80);
                        return;
                    }
                    // Two consecutive identical reads: the name is stable.
                    _openItem = name;
                    _keyName = name;   // stable key for memory (before any clean-name overwrite)
                    _openHq = Cfg.CheckForHq && Addons.GetOpenItemIsHq();
                    _ticks = 0;
                    _nameProbe = string.Empty;
                }

                // Session memory: if we've already priced this exact item (name + HQ) this run,
                // apply the remembered price without searching the market again.
                if (Cfg.UsePriceMemory && _priceMemory.TryGetValue(MemoryKey(_keyName, _openHq), out var remembered))
                {
                    if (!string.IsNullOrEmpty(remembered.Name)) _openItem = remembered.Name;
                    ApplyRemembered(remembered.Price);
                    return;
                }

                State = NavState.Search;
                break;

            case NavState.Search:
                Status = $"Searching market for {_openItem}...";
                _stableProbe = uint.MaxValue;
                _stableCount = 0;
                Addons.FireComparePrices();
                _ticks = 0;
                _refired = false;
                // Short settle; correctness now comes from the item-name match in WaitSearch, not
                // from waiting out the previous item's data.
                Wait(200);
                State = NavState.WaitSearch;
                break;

            case NavState.WaitSearch:
                if (Now < _deadline) return;
                _ticks++;

                // The search data is only valid once it's for the item we actually opened. Compare
                // the sheet name behind SearchItemId against the (garbled but readable) node name.
                // This is what stops us reading the PREVIOUS item's still-present listings — the
                // real cause of "Dravanian priced at Moongrass's 63,795".
                var searchName = MarketData.SearchItemName();
                var itemMatches = !string.IsNullOrEmpty(searchName)
                                  && NameMatches(searchName, _openItem);

                if (itemMatches && MarketData.ListingsReady() && !MarketData.IsWaiting())
                {
                    var fp = MarketData.FirstPrice();
                    if (fp == _stableProbe && fp != 0)
                    {
                        _stableCount++;
                        // Require 3 consecutive identical reads with the board not in a loading
                        // state. This defeats stale/cached snapshots that briefly show old listings
                        // (e.g. our own just-changed prices) right after a previous run.
                        if (_stableCount >= 3)
                        {
                            _lastPricedFirst = fp;
                            // Adopt the clean name now that we've confirmed the match.
                            _openItem = searchName;
                            State = NavState.Price;
                            return;
                        }
                        Wait(150);   // space probes so the 3 reads span ~450ms, not 3 frames
                    }
                    else
                    {
                        _stableProbe = fp;
                        _stableCount = 1;
                    }
                }
                else if (itemMatches && MarketData.ListingCount() == 0 && _ticks > 15 && !MarketData.IsWaiting())
                {
                    // Genuinely empty market for the CORRECT item.
                    _lastPricedFirst = 0;
                    _openItem = searchName;
                    State = NavState.Price;
                    return;
                }

                // Re-fire Compare Prices ONCE if the search never matched our item after ~3s.
                if (!_refired && _ticks == 30 && !itemMatches)
                {
                    Addons.FireComparePrices();
                    _refired = true;
                }

                if (_ticks > 150)
                {
                    Log($"{_openItem}: search timed out, skipping.");
                    Addons.CloseSearchWindows();
                    Addons.CloseAddon("RetainerSell");
                    _itemSlot++;
                    Wait(400);
                    State = NavState.WaitPriceClose;
                    return;
                }
                Wait(100);
                break;

            case NavState.Price:
            {
                var listings = MarketData.GetListings();

                // Memory key uses the raw node name (consistent per item, even if it renders ugly),
                // because the pre-search memory lookup only has the node text to go on. Display and
                // logs use the clean sheet name (AllaganMarket's approach) via SearchItemId.
                var keyName = _keyName;
                var cleanName = MarketData.SearchItemName();
                if (!string.IsNullOrEmpty(cleanName)) _openItem = cleanName;

                var result = PricingLogic.Compute(Cfg, _openItem, _openHq, listings, 0);
                Addons.CloseSearchWindows();

                // Remember this item's price for the rest of the run (skips re-searching duplicates).
                if (Cfg.UsePriceMemory && !string.IsNullOrEmpty(keyName))
                    _priceMemory[MemoryKey(keyName, _openHq)] = (result.Price, _openItem);

                var current = Addons.GetCurrentAskingPrice();
                var notes = new List<string>();
                if (result.OverrideApplied) notes.Add("override");
                if (result.MatchedOwnRetainer) notes.Add("matched-own");
                if (result.FloorApplied) notes.Add("floor");
                var note = notes.Count > 0 ? $" [{string.Join(",", notes)}]" : string.Empty;

                if (Cfg.SkipItemsAlreadyLowest && current == result.Price)
                {
                    Log($"{_openItem}: already {result.Price:N0}g{note}, skipped.");
                    Addons.CloseAddon("RetainerSell");
                }
                else if (Cfg.AutoConfirm)
                {
                    Addons.SetPriceAndConfirm(result.Price);
                    Log($"{_openItem}: {current:N0} -> {result.Price:N0}g (low {result.LowestSeen:N0}){note}");
                }
                else
                {
                    Addons.SetPriceOnly(result.Price);
                    Addons.CloseAddon("RetainerSell");
                    Log($"{_openItem}: set {result.Price:N0}g{note} (no confirm)");
                }

                _itemSlot++;
                Wait(250);
                State = NavState.WaitPriceClose;
                break;
            }

            case NavState.WaitPriceClose:
                if (Now < _deadline) return;
                if (Addons.Exists("RetainerSell"))
                {
                    Addons.CloseAddon("RetainerSell");
                    Wait(150);
                    return;
                }
                State = NavState.NextItem;
                break;

            case NavState.PostprocessBackout:
                // Return the UI to the retainer's SelectString menu (where AR handed it to us), then
                // release AR. Close the sell list; AR takes over from the SelectString menu.
                if (Now < _deadline) return;
                if (Addons.AdvanceTalk()) { Wait(150); return; }
                if (Addons.IsReady("SelectString"))
                {
                    Log("AutoRetainer postprocess done; handing back to AutoRetainer.");
                    EndPostprocess();
                    return;
                }
                if (Addons.IsVisible("RetainerSellList"))
                {
                    Addons.CloseAddon("RetainerSellList");
                    Wait(500);
                    return;
                }
                if (Addons.IsVisible("RetainerSell"))
                {
                    Addons.CloseAddon("RetainerSell");
                    Wait(400);
                    return;
                }
                _ticks++;
                if (_ticks > 60)
                {
                    // Give up gracefully but STILL release AR so we never leave it hung.
                    Log("AutoRetainer postprocess: couldn't cleanly return to menu; releasing anyway.");
                    EndPostprocess();
                    return;
                }
                Wait(200);
                break;

            case NavState.CloseRetainer:
                // Return to the retainer list. Fire the appropriate exit action ONCE, then wait
                // patiently — the game takes a moment to close the retainer (you'll see "now selling
                // items in the Kugane markets") before the list reappears. Hammering the exit during
                // that animation races the transition and fails.
                if (Now < _deadline) return;   // honour Wait(); without this the state spins every frame

                if (Addons.IsVisible("RetainerList"))
                {
                    _ticks = 0;
                    _closeActed = false;
                    State = NavState.NextRetainer;
                    return;
                }

                // Advance any greeting bubble regardless.
                if (Addons.AdvanceTalk()) { Wait(200); break; }

                if (!_closeActed)
                {
                    // Take exactly one exit action based on what's currently open.
                    if (Addons.IsVisible("RetainerSellList"))
                    {
                        Addons.CloseAddon("RetainerSellList"); // -> retainer SelectString menu
                        _closeActed = true;
                        Wait(600);
                        break;
                    }
                    if (Addons.IsVisible("SelectString"))
                    {
                        Addons.QuitRetainer();                 // select "Quit" -> RetainerList
                        _closeActed = true;
                        Wait(800);
                        break;
                    }
                    if (Addons.IsVisible("RetainerSell"))
                    {
                        Addons.CloseAddon("RetainerSell");
                        _closeActed = true;
                        Wait(400);
                        break;
                    }
                    // Nothing recognized open yet — the game is mid-transition. Just wait.
                    Wait(200);
                    break;
                }

                // We've acted; now wait for the next window to appear. Once a new actionable
                // window shows (or the list), allow another action.
                if (Addons.IsVisible("SelectString") || Addons.IsVisible("RetainerSellList")
                    || Addons.IsVisible("RetainerSell"))
                {
                    _closeActed = false; // a sub-window is up again; act on it next tick
                    break;
                }

                _ticks++;
                if (_ticks > 100) // ~ generous wait for the list to appear
                {
                    var seen = new List<string>();
                    foreach (var n in new[] { "RetainerList", "SelectString", "RetainerSellList",
                                              "RetainerSell", "SelectYesno", "Talk", "ContextMenu",
                                              "RetainerTaskList", "RetainerTaskAsk", "Bank" })
                        if (Addons.IsVisible(n)) seen.Add(n);
                    SetError($"Couldn't return to the retainer list. Visible: {(seen.Count > 0 ? string.Join(", ", seen) : "none")}");
                    return;
                }
                Wait(200);
                break;

            case NavState.WaitClosed:
                // (retained for compatibility; CloseRetainer now transitions directly)
                _ticks = 0;
                State = NavState.NextRetainer;
                break;
        }
    }

    private void InitRetainers()
    {
        _retainerCount = RetainerReader.Count;
        _retainerIdx = -1;
        // opportunistic own-retainer detection
        if (Cfg.AutoDetectMyRetainers)
        {
            foreach (var n in MarketData.GetMyRetainerNames())
            {
                var norm = PricingLogic.Normalize(n);
                if (!string.IsNullOrEmpty(norm) && !Cfg.MyRetainers.Contains(norm))
                    Cfg.MyRetainers.Add(norm);
            }
            Cfg.Save();
        }
    }

    private void StartItems()
    {
        _itemCount = RetainerReader.ActiveMarketItems();
        _itemSlot = 0;
        if (_itemCount <= 0)
        {
            Log($"Retainer {_retainerIdx + 1}: no items listed, skipping.");
            _ticks = 0;
            _closeActed = false;
            State = NavState.CloseRetainer;
            return;
        }
        Log($"Retainer {_retainerIdx + 1}: {_itemCount} item(s).");
        State = NavState.NextItem;
    }

    private bool IsBlacklisted(string name)
    {
        var norm = PricingLogic.Normalize(name);
        foreach (var b in Cfg.BlacklistRetainers)
            if (PricingLogic.Normalize(b) == norm) return true;
        return false;
    }

    private static string MemoryKey(string name, bool hq) =>
        PricingLogic.Normalize(name) + (hq ? "#hq" : "#nq");

    /// <summary>
    /// True if the clean search name corresponds to the opened item. The node name is often
    /// wrapped in glyph payload junk, so we normalize both (letters/digits only) and check the
    /// clean sheet name is contained in the node name.
    /// </summary>
    private static bool NameMatches(string searchName, string openedName)
    {
        var a = PricingLogic.Normalize(searchName);
        var b = PricingLogic.Normalize(openedName);
        if (a.Length == 0 || b.Length == 0) return false;
        return b.Contains(a) || a.Contains(b);
    }

    /// <summary>Apply a remembered price to the currently open item, no market search.</summary>
    private void ApplyRemembered(int price)
    {
        var current = Addons.GetCurrentAskingPrice();
        if (Cfg.SkipItemsAlreadyLowest && current == price)
        {
            Log($"{_openItem}: already {price:N0}g (remembered), skipped.");
            Addons.CloseAddon("RetainerSell");
        }
        else if (Cfg.AutoConfirm)
        {
            Addons.SetPriceAndConfirm(price);
            Log($"{_openItem}: {current:N0} -> {price:N0}g (remembered)");
        }
        else
        {
            Addons.SetPriceOnly(price);
            Addons.CloseAddon("RetainerSell");
            Log($"{_openItem}: set {price:N0}g (remembered, no confirm)");
        }
        _itemSlot++;
        Wait(250);
        State = NavState.WaitPriceClose;
    }

    private void Wait(int ms) => _deadline = Now + (int)(ms * Math.Clamp(Cfg.SpeedMultiplier, 0.3f, 3.0f));

    private void SetError(string msg)
    {
        State = NavState.Error;
        Status = msg;
        _plugin.Chat($"[Market Helper] {msg}");
        // Never leave AutoRetainer blocked waiting on us — release it even on failure.
        if (_postprocessMode)
        {
            var done = _onPostprocessDone;
            _postprocessMode = false;
            _onPostprocessDone = null;
            done?.Invoke();
        }
    }

    private void Log(string msg)
    {
        Report.Add(msg);
        Status = msg;
        if (Cfg.Verbose) _plugin.Chat($"[Market Helper] {msg}");
    }
}
