using System;
using System.Collections.Generic;
using System.Linq;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using Dalamud.Game.ClientState.Conditions;

namespace MarketHelper;

public enum GatherState
{
    Idle, FindBell, InteractBell, WaitRetainerList, NextRetainer, SelectRetainer, WaitSelectString,
    OpenMarket, MarketReturn, WaitMarketCtx, OpenInventory, InvRetrieve, WaitInvCtx,
    BackToSelectString, CloseRetainer, WaitClosed, Done, Error,
}

/// <summary>
/// Item Gatherer: walks the summoning bell and pulls DESIGNATED items off each retainer into the
/// player's main bags. Optionally from the retainer's market listings (returns them to the
/// retainer first) and/or the retainer's inventory. Stops when the player's bags are full.
/// All moves use the safe context-menu-by-name path (never blind clicks).
/// </summary>
public sealed class GatherRunner
{
    private readonly Plugin _plugin;
    private Configuration Cfg => _plugin.Config;

    public GatherState State { get; private set; } = GatherState.Idle;
    public string Status { get; private set; } = "Idle.";
    public List<string> Report { get; } = new();
    public bool Running => State is not (GatherState.Idle or GatherState.Done or GatherState.Error);

    private int _retainerIdx = -1;
    private int _retainerCount;
    private double _deadline;
    private int _ticks;
    private bool _closeActed;
    private int _pulled;   // total items retrieved this run

    private HashSet<uint> _wanted = new();

    private static double Now => Environment.TickCount64;
    private bool AtBell => Svc.Condition[ConditionFlag.OccupiedSummoningBell];

    public GatherRunner(Plugin plugin) => _plugin = plugin;

    public void Start()
    {
        if (Cfg.GathererItems.Count == 0) { SetError("No items in the gather list."); return; }
        if (!Cfg.GatherFromInventory && !Cfg.GatherFromMarket)
        { SetError("Enable 'from inventory' and/or 'from market' first."); return; }
        if (RetainerReader.PlayerBagsFull()) { SetError("Your inventory is already full — empty it first."); return; }

        _wanted = new HashSet<uint>(Cfg.GathererItems);
        _pulled = 0;
        _retainerIdx = -1;
        Report.Clear();
        State = GatherState.FindBell;
        Status = "Looking for bell...";
    }

    public void Stop()
    {
        State = GatherState.Idle;
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
        // Global stop condition: bags full.
        if (Running && State is not (GatherState.CloseRetainer or GatherState.WaitClosed
                or GatherState.BackToSelectString) && RetainerReader.PlayerBagsFull())
        {
            Log($"Inventory full — stopping. Pulled {_pulled} item(s). Empty your bags and run again.");
            _ticks = 0; _closeActed = false;
            State = GatherState.CloseRetainer;
            _finishAfterClose = true;
            return;
        }

        switch (State)
        {
            case GatherState.FindBell:
            {
                var bell = Bell.GetNearest(out var dist);
                if (bell == null || dist > 20f) { SetError("No summoning bell within range."); return; }
                State = GatherState.InteractBell;
                break;
            }

            case GatherState.InteractBell:
            {
                Status = "Opening the summoning bell...";
                var bell = Bell.GetNearest(out _);
                if (bell == null || !Bell.Interact(bell)) { State = GatherState.FindBell; return; }
                Wait(300);
                State = GatherState.WaitRetainerList;
                break;
            }

            case GatherState.WaitRetainerList:
                if (Now < _deadline) return;
                if (AtBell && Addons.IsVisible("RetainerList"))
                {
                    _retainerCount = RetainerReader.Count;
                    Log($"Bell open. {_retainerCount} retainer(s). Gathering {_wanted.Count} item type(s).");
                    State = GatherState.NextRetainer;
                    return;
                }
                if (Now > _deadline + 8000) { SetError("RetainerList didn't open."); return; }
                break;

            case GatherState.NextRetainer:
                _retainerIdx++;
                if (_retainerIdx >= _retainerCount)
                {
                    _ticks = 0; _finishAfterClose = true;
                    State = GatherState.WaitClosed;
                    Status = $"Done. Pulled {_pulled} item(s).";
                    Log($"Finished all retainers. Pulled {_pulled} item(s).");
                    return;
                }
                if (!Addons.IsVisible("RetainerList"))
                {
                    _retainerIdx--; _ticks = 0; _closeActed = false;
                    State = GatherState.CloseRetainer;
                    return;
                }
                Wait(500);
                State = GatherState.SelectRetainer;
                break;

            case GatherState.SelectRetainer:
                if (Now < _deadline) return;
                if (!Addons.IsVisible("RetainerList"))
                {
                    _retainerIdx--; _ticks = 0; _closeActed = false;
                    State = GatherState.CloseRetainer;
                    return;
                }
                Status = $"Opening retainer {_retainerIdx + 1}/{_retainerCount}...";
                Addons.SelectRetainer(_retainerIdx);
                Wait(500);
                State = GatherState.WaitSelectString;
                break;

            case GatherState.WaitSelectString:
                if (Now < _deadline) return;
                if (Addons.AdvanceTalk()) { Wait(120); return; }
                if (Addons.IsReady("SelectString"))
                {
                    // Market first (so returned items land in inventory and get pulled next), then inv.
                    State = Cfg.GatherFromMarket ? GatherState.OpenMarket : GatherState.OpenInventory;
                    _ticks = 0;
                    return;
                }
                if (Now > _deadline + 12000) { SetError("Retainer menu didn't open."); return; }
                break;

            // ---- MARKET: return designated listings to the retainer's inventory ----
            case GatherState.OpenMarket:
                if (Now < _deadline) return;
                if (!Cfg.GatherFromMarket) { State = GatherState.OpenInventory; _ticks = 0; return; }
                if (Addons.AdvanceTalk()) { Wait(150); return; }
                if (Addons.IsReady("RetainerSellList")) { State = GatherState.MarketReturn; _ticks = 0; return; }
                if (Addons.IsVisible("SelectString"))
                {
                    if (Addons.OpenSellOnMarket()) { Wait(800); _ticks = 0; return; }
                }
                if (++_ticks > 60) { Log("Couldn't open sell list; skipping market for this retainer."); State = GatherState.OpenInventory; _ticks = 0; return; }
                Wait(200);
                break;

            case GatherState.MarketReturn:
            {
                if (Now < _deadline) return;
                if (RetainerReader.PlayerBagsFull()) { State = GatherState.OpenInventory; _ticks = 0; return; }
                var hit = RetainerReader.FindRetainerMarketItem(_wanted);
                if (hit == null)
                {
                    // No more designated market items — move on to inventory.
                    State = Cfg.GatherFromInventory ? GatherState.OpenInventory : GatherState.BackToSelectString;
                    _ticks = 0;
                    return;
                }
                if (!RetainerRetrieve.OpenMarketItemContext(hit.Value.Type, hit.Value.Slot))
                {
                    Log("Couldn't open market item context; skipping to inventory.");
                    State = Cfg.GatherFromInventory ? GatherState.OpenInventory : GatherState.BackToSelectString;
                    _ticks = 0;
                    return;
                }
                Wait(400);
                State = GatherState.WaitMarketCtx;
                _ticks = 0;
                break;
            }

            case GatherState.WaitMarketCtx:
                if (Now < _deadline) return;
                if (Addons.Exists("ContextMenu"))
                {
                    if (RetainerRetrieve.SelectReturnToInventory())
                    {
                        _pulled++;
                        Log($"Returned a listing to inventory.");
                        Wait(600);
                        State = GatherState.MarketReturn;   // look for the next one
                        return;
                    }
                    Log("No 'Return to Inventory' entry; skipping market.");
                    Addons.CloseAddon("ContextMenu");
                    State = Cfg.GatherFromInventory ? GatherState.OpenInventory : GatherState.BackToSelectString;
                    return;
                }
                if (++_ticks > 50) { State = GatherState.MarketReturn; }   // retry the scan
                break;

            // ---- INVENTORY: retrieve designated items into player bags ----
            case GatherState.OpenInventory:
                if (Now < _deadline) return;
                if (!Cfg.GatherFromInventory) { State = GatherState.BackToSelectString; _ticks = 0; return; }
                if (Addons.AdvanceTalk()) { Wait(150); return; }
                if (RetainerRetrieve.RetainerInventoryReady) { State = GatherState.InvRetrieve; _ticks = 0; return; }
                // From the sell list we must back to SelectString, then open inventory.
                if (Addons.IsVisible("RetainerSellList")) { Addons.CloseAddon("RetainerSellList"); Wait(500); return; }
                if (Addons.IsVisible("SelectString"))
                {
                    if (Addons.OpenRetainerInventory()) { Wait(800); _ticks = 0; return; }
                }
                if (++_ticks > 60) { Log("Couldn't open retainer inventory; skipping."); State = GatherState.BackToSelectString; _ticks = 0; return; }
                Wait(200);
                break;

            case GatherState.InvRetrieve:
            {
                if (Now < _deadline) return;
                if (RetainerReader.PlayerBagsFull()) { State = GatherState.BackToSelectString; _ticks = 0; return; }
                var hit = RetainerReader.FindRetainerInventoryItem(_wanted);
                if (hit == null) { State = GatherState.BackToSelectString; _ticks = 0; return; }
                if (!RetainerRetrieve.OpenInventoryItemContext(hit.Value.Type, hit.Value.Slot))
                {
                    Log("Couldn't open inventory item context; skipping.");
                    State = GatherState.BackToSelectString;
                    _ticks = 0;
                    return;
                }
                Wait(400);
                State = GatherState.WaitInvCtx;
                _ticks = 0;
                break;
            }

            case GatherState.WaitInvCtx:
                if (Now < _deadline) return;
                if (Addons.Exists("ContextMenu"))
                {
                    if (RetainerRetrieve.SelectRetrieve())
                    {
                        _pulled++;
                        Log($"Retrieved an item into your bags. ({RetainerReader.FreePlayerBagSlots()} slot(s) free)");
                        Wait(600);
                        State = GatherState.InvRetrieve;   // next item
                        return;
                    }
                    Log("No 'Retrieve' entry; skipping.");
                    Addons.CloseAddon("ContextMenu");
                    State = GatherState.BackToSelectString;
                    return;
                }
                if (++_ticks > 50) { State = GatherState.InvRetrieve; }
                break;

            // ---- Back out to the retainer list and move on ----
            case GatherState.BackToSelectString:
                if (Now < _deadline) return;
                if (Addons.AdvanceTalk()) { Wait(150); return; }
                if (Addons.IsVisible("InventoryRetainer")) { Addons.CloseAddon("InventoryRetainer"); Wait(400); return; }
                if (Addons.IsVisible("InventoryRetainerLarge")) { Addons.CloseAddon("InventoryRetainerLarge"); Wait(400); return; }
                if (Addons.IsVisible("RetainerSellList")) { Addons.CloseAddon("RetainerSellList"); Wait(400); return; }
                _ticks = 0; _closeActed = false;
                State = GatherState.CloseRetainer;
                break;

            case GatherState.CloseRetainer:
                if (Now < _deadline) return;
                if (Addons.AdvanceTalk()) { Wait(150); return; }
                if (Addons.IsVisible("RetainerList"))
                {
                    // Already back at the list.
                    if (_finishAfterClose) { State = GatherState.WaitClosed; _ticks = 0; return; }
                    State = GatherState.NextRetainer;
                    return;
                }
                if (!_closeActed && Addons.IsVisible("SelectString"))
                {
                    Addons.QuitRetainer();
                    _closeActed = true;
                    Wait(600);
                    return;
                }
                if (++_ticks > 80) { SetError("Couldn't return to the retainer list."); return; }
                Wait(200);
                break;

            case GatherState.WaitClosed:
                if (Now < _deadline) return;
                if (Addons.IsVisible("RetainerList")) { Addons.CloseAddon("RetainerList"); Wait(400); _ticks++; if (_ticks > 20) State = GatherState.Done; return; }
                State = GatherState.Done;
                break;
        }
    }

    private bool _finishAfterClose;

    private void Wait(int ms)
    {
        var scale = Math.Clamp(Cfg.SearchPacingMs / 600f, 0.35f, 2.5f);
        _deadline = Now + (int)(ms * scale);
    }

    private void SetError(string msg)
    {
        State = GatherState.Error;
        Status = msg;
        _plugin.Chat($"[Market Helper] {msg}");
    }

    private void Log(string msg)
    {
        Report.Add(msg);
        Status = msg;
        if (Cfg.Verbose) _plugin.Chat($"[Market Helper] {msg}");
    }
}
