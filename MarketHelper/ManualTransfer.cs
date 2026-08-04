using System;
using System.Collections.Generic;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace MarketHelper;

public enum ManualMode { None, WithdrawAll, DepositRetainer, DepositFc }
public enum ManualState { Idle, Act, WaitCtx, Settle, Done, Error }

/// <summary>
/// Manual, no-navigation transfers on whatever window is ALREADY open:
///  - WithdrawAll: pull all gather-list items from the open retainer inventory into your bags.
///  - DepositRetainer: entrust all gather-list items from your bags into the open retainer.
///  - DepositFc: deposit all gather-list items from your bags into the open FC chest.
/// Uses the same safe context-menu-by-name path. Does NOT walk the bell — you open the window.
/// </summary>
public sealed class ManualTransfer
{
    private readonly Plugin _plugin;
    private Configuration Cfg => _plugin.Config;

    public ManualState State { get; private set; } = ManualState.Idle;
    public ManualMode Mode { get; private set; } = ManualMode.None;
    public string Status { get; private set; } = "Idle.";
    public bool Running => State is ManualState.Act or ManualState.WaitCtx or ManualState.Settle;

    private HashSet<uint> _wanted = new();
    private int _moved;
    private double _deadline;
    private int _ticks;
    private (InventoryType Type, ushort Slot)? _pendingLoc;
    private uint _pendingItem;
    private (InventoryType Type, ushort Slot)? _lastLoc;
    private uint _lastItem;

    private static double Now => Environment.TickCount64;

    public ManualTransfer(Plugin plugin) => _plugin = plugin;

    public void Start(ManualMode mode)
    {
        if (Cfg.GathererItems.Count == 0) { Fail("No items in the gather list."); return; }
        // Verify the required window is open for the chosen mode.
        switch (mode)
        {
            case ManualMode.WithdrawAll when !RetainerRetrieve.RetainerInventoryReady:
                Fail("Open a retainer's inventory first."); return;
            case ManualMode.DepositRetainer when !RetainerRetrieve.RetainerInventoryReady:
                Fail("Open a retainer's inventory first."); return;
            case ManualMode.DepositFc when !Addons.IsFcChestOpen():
                Fail("Open the Free Company chest first."); return;
        }
        Mode = mode;
        _wanted = new HashSet<uint>(Cfg.GathererItems);
        _moved = 0; _ticks = 0;
        _pendingLoc = null; _pendingItem = 0; _lastLoc = null; _lastItem = 0;
        State = ManualState.Act;
        Status = mode switch
        {
            ManualMode.WithdrawAll => "Withdrawing items...",
            ManualMode.DepositRetainer => "Entrusting items to retainer...",
            ManualMode.DepositFc => "Depositing items to FC chest...",
            _ => "Working...",
        };
    }

    public void Stop() { State = ManualState.Idle; Status = "Stopped."; }

    public void Tick()
    {
        if (!Running) return;
        try { Step(); }
        catch (Exception ex) { Fail($"Exception: {ex.Message}"); }
    }

    private void Step()
    {
        switch (State)
        {
            case ManualState.Act:
            {
                if (Now < _deadline) return;

                // Wait for the previous move to clear its slot before finding the next.
                if (_lastLoc != null)
                {
                    var still = RetainerReader.SlotHasItem(_lastLoc.Value.Type, _lastLoc.Value.Slot, _lastItem);
                    if (still && _ticks < 15) { _ticks++; Wait(120); return; }
                    _lastLoc = null; _lastItem = 0; _ticks = 0;
                }

                if (Mode == ManualMode.WithdrawAll)
                {
                    // Guard: stop if bags confirmed full.
                    if (RetainerReader.PlayerBagsFull()) { Done($"Bags full. Withdrew {_moved} stack(s)."); return; }
                    var hit = RetainerReader.FindRetainerInventoryItem(_wanted);
                    if (hit == null) { Done($"Withdrew {_moved} stack(s)."); return; }
                    if (!RetainerRetrieve.OpenInventoryItemContext(hit.Value.Type, hit.Value.Slot))
                    { Done($"Couldn't open item menu. Withdrew {_moved} stack(s)."); return; }
                    _pendingLoc = (hit.Value.Type, hit.Value.Slot); _pendingItem = hit.Value.ItemId;
                }
                else // DepositRetainer or DepositFc: source is player inventory
                {
                    var hit = RetainerReader.FindPlayerInventoryItem(_wanted);
                    if (hit == null) { Done($"Deposited {_moved} stack(s)."); return; }
                    var owner = Mode == ManualMode.DepositFc ? Addons.FcChestAddonName() : RetainerRetrieve.InventoryAddonNamePublic;
                    if (!RetainerRetrieve.OpenPlayerItemContext(hit.Value.Type, hit.Value.Slot, owner))
                    { Done($"Couldn't open item menu. Deposited {_moved} stack(s)."); return; }
                    _pendingLoc = (hit.Value.Type, hit.Value.Slot); _pendingItem = hit.Value.ItemId;
                }
                Wait(350);
                State = ManualState.WaitCtx;
                _ticks = 0;
                break;
            }

            case ManualState.WaitCtx:
                if (Now < _deadline) return;
                if (Addons.Exists("ContextMenu"))
                {
                    var ok = Mode switch
                    {
                        ManualMode.WithdrawAll => RetainerRetrieve.SelectRetrieve(),
                        ManualMode.DepositRetainer => RetainerRetrieve.SelectEntrustToRetainer(),
                        ManualMode.DepositFc => RetainerRetrieve.SelectDepositFreeCompany(),
                        _ => false,
                    };
                    if (ok)
                    {
                        _moved++;
                        _lastLoc = _pendingLoc; _lastItem = _pendingItem;
                        if (Cfg.Debug) _plugin.Chat($"[Market Helper] Manual: moved item {_pendingItem} ({_moved} total).");
                        Wait(500);
                        State = ManualState.Act;
                        return;
                    }
                    if (Cfg.Debug) _plugin.Chat($"[Market Helper] Manual: entry not found. Menu had: {Addons.DumpContextMenu()}");
                    Addons.CloseAddon("ContextMenu");
                    Done($"Menu entry not found after {_moved} move(s).");
                    return;
                }
                if (++_ticks > 40) { State = ManualState.Act; }
                break;
        }
    }

    private void Wait(int ms)
    {
        var scale = Math.Clamp(Cfg.SearchPacingMs / 600f, 0.35f, 2.5f);
        _deadline = Now + (int)(ms * scale);
    }

    private void Done(string msg) { State = ManualState.Done; Status = msg; _plugin.Chat($"[Market Helper] {msg}"); }
    private void Fail(string msg) { State = ManualState.Error; Status = msg; _plugin.Chat($"[Market Helper] {msg}"); }
}
