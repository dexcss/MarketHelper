using System;
using System.Collections.Generic;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;

namespace MarketHelper;

/// <summary>
/// Reactive undercut engine, modeled on Marketbuddy's AddonLifecycle approach.
///
/// Flow: when a RetainerSell window opens (you clicked an item, OR AutoRetainer did during
/// its loop), we fire Compare Prices, wait for the market listings to arrive, then set and
/// (optionally) confirm the undercut price. Because the trigger is the window opening, the
/// same path serves both the manual "Run" button and hands-off autorun.
/// </summary>
public sealed class MarketListener : IDisposable
{
    private readonly Plugin _plugin;
    private Configuration Cfg => _plugin.Config;

    // Pending-price state: after firing Compare Prices we wait (on framework tick) for the
    // InfoProxyItemSearch listings to populate, then price the item once.
    private bool _awaitingListings;
    private double _deadline;
    private int _searchStartTicks;
    private string _pendingItem = string.Empty;
    private bool _pendingHq;

    public bool Busy => _awaitingListings;
    public string Status { get; private set; } = "Idle.";
    public readonly List<string> Report = new();

    private static double Now => Environment.TickCount64;

    public MarketListener(Plugin plugin)
    {
        _plugin = plugin;
        _plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "RetainerSell", OnRetainerSellSetup);
        _plugin.Framework.Update += OnTick;
    }

    public void Dispose()
    {
        _plugin.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "RetainerSell", OnRetainerSellSetup);
        _plugin.Framework.Update -= OnTick;
    }

    /// <summary>On-demand: price the item whose RetainerSell is open right now.</summary>
    public void PriceOpenItemNow()
    {
        if (!Addons.Exists("RetainerSell"))
        {
            Status = "Open an item's sell window first.";
            return;
        }
        BeginSearch();
    }

    private void OnRetainerSellSetup(AddonEvent type, AddonArgs args)
    {
        if (!Cfg.MarketHelperOnOpen) return;
        // Stand down while the full walk (NavRunner) is driving — otherwise both race to
        // price the same item and corrupt each other's market search.
        if (_plugin.Nav.Running) return;
        BeginSearch();
    }

    private void BeginSearch()
    {
        if (_awaitingListings) return;
        if (!Addons.IsReady("RetainerSell")) return;

        _pendingItem = Addons.GetOpenItemName();
        _pendingHq = Cfg.CheckForHq && Addons.GetOpenItemIsHq();

        // Auto-detect our retainers opportunistically.
        if (Cfg.AutoDetectMyRetainers)
        {
            foreach (var n in MarketData.GetMyRetainerNames())
            {
                var norm = PricingLogic.Normalize(n);
                if (!string.IsNullOrEmpty(norm) && !Cfg.MyRetainers.Contains(norm))
                    Cfg.MyRetainers.Add(norm);
            }
        }

        Addons.FireComparePrices();
        _awaitingListings = true;
        _searchStartTicks = 0;
        _deadline = Now + 300; // small grace before first poll
        Status = $"Searching market for {_pendingItem}...";
    }

    private void OnTick(Dalamud.Plugin.Services.IFramework _)
    {
        if (!_awaitingListings) return;
        if (Now < _deadline) return;

        _searchStartTicks++;

        // RetainerSell closed under us (user backed out) — abort cleanly.
        if (!Addons.Exists("RetainerSell"))
        {
            Abort();
            return;
        }

        // Ready only when the listing data is for the item we opened (guards against reading a
        // previous item's still-present listings), and is actually populated.
        var searchName = MarketData.SearchItemName();
        var matches = !string.IsNullOrEmpty(searchName)
                      && PricingLogic.Normalize(_pendingItem).Contains(PricingLogic.Normalize(searchName));
        if (matches && MarketData.ListingsReady())
        {
            PriceNow();
            return;
        }

        if (_searchStartTicks > 100) // ~10s grace
        {
            Log($"{_pendingItem}: market search timed out, skipped.");
            Addons.CloseSearchWindows();
            Abort();
            return;
        }
        _deadline = Now + 100;
    }

    private void PriceNow()
    {
        try
        {
            var listings = MarketData.GetListings();

            // Prefer the clean sheet name (via SearchItemId) over the garbled node text.
            var cleanName = MarketData.SearchItemName();
            if (!string.IsNullOrEmpty(cleanName)) _pendingItem = cleanName;

            var result = PricingLogic.Compute(Cfg, _pendingItem, _pendingHq, listings, 0);

            Addons.CloseSearchWindows();

            var current = Addons.GetCurrentAskingPrice();
            var notes = new List<string>();
            if (result.OverrideApplied) notes.Add("override");
            if (result.MatchedOwnRetainer) notes.Add("matched-own");
            if (result.FloorApplied) notes.Add("floor");
            var note = notes.Count > 0 ? $" [{string.Join(",", notes)}]" : string.Empty;

            if (Cfg.SkipItemsAlreadyLowest && current == result.Price)
            {
                Log($"{_pendingItem}: already {result.Price:N0}g{note}, skipped.");
            }
            else if (Cfg.AutoConfirm)
            {
                Addons.SetPriceAndConfirm(result.Price);
                Log($"{_pendingItem}: {current:N0} -> {result.Price:N0}g (low {result.LowestSeen:N0}){note}");
            }
            else
            {
                // Set price only, leave confirm to the user.
                Addons.SetPriceOnly(result.Price);
                Log($"{_pendingItem}: price set to {result.Price:N0}g{note} (confirm manually)");
            }
        }
        catch (Exception ex)
        {
            _plugin.Log.Error(ex, "MarketHelper price step failed");
            Status = $"Error: {ex.Message}";
        }
        finally
        {
            Abort();
        }
    }

    private void Abort()
    {
        _awaitingListings = false;
        if (Status.StartsWith("Searching")) Status = "Idle.";
    }

    private void Log(string msg)
    {
        Report.Add(msg);
        Status = msg;
        if (Cfg.Verbose) _plugin.Chat($"[Market Helper] {msg}");
    }
}
