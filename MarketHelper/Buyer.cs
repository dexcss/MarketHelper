using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MarketHelper;

/// <summary>One listing we intend to buy, pinned to the world it lives on.</summary>
public sealed class BuyLine
{
    public uint ItemId;
    public string ItemName = string.Empty;
    public string World = string.Empty;
    public long UnitPrice;
    public int Quantity;
    public bool Hq;
    public string Seller = string.Empty;

    public long Total => UnitPrice * Quantity;
}

/// <summary>All the buying to be done on one world, in one visit.</summary>
public sealed class WorldStop
{
    public string World = string.Empty;
    public readonly List<BuyLine> Lines = new();

    public long TotalCost => Lines.Sum(l => l.Total);
    public int TotalUnits => Lines.Sum(l => l.Quantity);
    public int ItemTypes => Lines.Select(l => l.ItemId).Distinct().Count();
}

/// <summary>Per-item view of a scan: what we wanted vs what's actually available under the cap.</summary>
public sealed class ItemSummary
{
    public uint ItemId;
    public string ItemName = string.Empty;
    public int Requested;
    public int FoundUnits;                 // units available at or under the cap
    public long CheapestPrice;             // cheapest unit price anywhere in scope (even over cap)
    public string CheapestWorld = string.Empty;
    public long MaxPrice;
    public bool AnyListingsAtAll;
    public readonly Dictionary<string, (int Units, long Cheapest)> PerWorld = new();

    public bool Satisfied => FoundUnits >= Requested;
    public bool NothingUnderCap => AnyListingsAtAll && FoundUnits == 0;
}

/// <summary>The full result of a scan: what to buy, where, and what it'll cost.</summary>
public sealed class BuyPlanResult
{
    public DateTime ScannedAt = DateTime.Now;
    public string Scope = string.Empty;
    public readonly List<WorldStop> Stops = new();
    public readonly List<ItemSummary> Items = new();
    public readonly List<string> Warnings = new();

    public long GrandTotal => Stops.Sum(s => s.TotalCost);
    public int GrandUnits => Stops.Sum(s => s.TotalUnits);
    public bool HasWork => Stops.Any(s => s.Lines.Count > 0);
}

/// <summary>
/// Backing state for the Buyer tab's SCAN step. Queries Universalis off the UI thread, applies
/// each item's price cap, and groups the surviving listings into one stop per world.
///
/// Nothing here touches the game — a scan is read-only and free. The plan it produces is a
/// SNAPSHOT: Universalis data is cached and can be minutes old, so BuyRunner re-verifies every
/// price against the live board before spending a single gil.
/// </summary>
public sealed class Buyer
{
    private readonly Plugin _plugin;
    private Configuration Cfg => _plugin.Config;

    public bool Scanning { get; private set; }
    public string? Error { get; private set; }
    public string Status { get; private set; } = "Not scanned yet.";
    public BuyPlanResult? Plan { get; private set; }

    public Buyer(Plugin plugin) => _plugin = plugin;

    /// <summary>The scan scope: the player's DC unless overridden, or the whole region.</summary>
    public string ResolveScope()
    {
        if (!string.IsNullOrWhiteSpace(Cfg.BuyerScopeOverride)) return Cfg.BuyerScopeOverride.Trim();
        if (Cfg.BuyerScanRegion)
        {
            var region = WorldInfo.CurrentRegion();
            if (!string.IsNullOrWhiteSpace(region)) return region;
        }
        var dc = WorldInfo.CurrentDataCenter();
        return string.IsNullOrWhiteSpace(dc) ? "Aether" : dc;
    }

    public void Scan()
    {
        if (Scanning) return;
        var items = Cfg.BuyerItems.Where(i => i.Enabled && i.ItemId != 0 && i.Quantity > 0).ToList();
        if (items.Count == 0) { Error = "Nothing on the shopping list."; Status = Error; return; }

        Error = null;
        Scanning = true;
        Status = "Scanning...";
        _ = ScanAsync(items, ResolveScope());
    }

    public void ClearPlan()
    {
        Plan = null;
        Status = "Not scanned yet.";
        Error = null;
    }

    private async Task ScanAsync(List<BuyerItem> items, string scope)
    {
        var result = new BuyPlanResult { Scope = scope };
        try
        {
            // Own retainers are filtered out — you can't buy from yourself, and a listing of ours
            // showing up as "the cheapest" would just wedge the run at that row.
            var mine = new HashSet<string>(
                Cfg.MyRetainers.Where(n => !string.IsNullOrWhiteSpace(n)).Select(Normalise));

            var byWorld = new Dictionary<string, WorldStop>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in items)
            {
                var name = ItemSearch.FindById(item.ItemId);
                if (string.IsNullOrEmpty(name)) name = ItemSearch.FindByIdAny(item.ItemId);
                if (string.IsNullOrEmpty(name)) name = $"Item #{item.ItemId}";

                var summary = new ItemSummary
                {
                    ItemId = item.ItemId,
                    ItemName = name,
                    Requested = item.Quantity,
                    MaxPrice = item.MaxPrice,
                };

                var res = await Universalis.GetListingsAsync(scope, item.ItemId, 100, item.HqOnly);
                if (res.Error != null)
                {
                    result.Warnings.Add($"{name}: lookup failed ({res.Error}) — skipped.");
                    result.Items.Add(summary);
                    continue;
                }

                var listings = res.Listings
                    .Where(l => l.PricePerUnit > 0 && l.Quantity > 0)
                    .Where(l => !item.HqOnly || l.Hq)
                    .Where(l => !mine.Contains(Normalise(l.Retainer)))
                    .OrderBy(l => l.PricePerUnit)
                    .ToList();

                summary.AnyListingsAtAll = listings.Count > 0;
                if (listings.Count > 0)
                {
                    summary.CheapestPrice = listings[0].PricePerUnit;
                    summary.CheapestWorld = listings[0].World;
                }

                var remaining = item.Quantity;
                foreach (var l in listings)
                {
                    if (remaining <= 0) break;
                    if (l.PricePerUnit > item.MaxPrice) break;   // sorted, so everything after is dearer

                    // A market listing is bought whole — you cannot take part of a stack.
                    if (l.Quantity > remaining && !Cfg.BuyerAllowOvershoot) continue;

                    var world = string.IsNullOrWhiteSpace(l.World) ? scope : l.World;
                    if (!byWorld.TryGetValue(world, out var stop))
                    {
                        stop = new WorldStop { World = world };
                        byWorld[world] = stop;
                    }
                    stop.Lines.Add(new BuyLine
                    {
                        ItemId = item.ItemId,
                        ItemName = name,
                        World = world,
                        UnitPrice = l.PricePerUnit,
                        Quantity = l.Quantity,
                        Hq = l.Hq,
                        Seller = l.Retainer,
                    });

                    summary.FoundUnits += l.Quantity;
                    if (!summary.PerWorld.TryGetValue(world, out var pw))
                        summary.PerWorld[world] = (l.Quantity, l.PricePerUnit);
                    else
                        summary.PerWorld[world] = (pw.Units + l.Quantity, Math.Min(pw.Cheapest, l.PricePerUnit));

                    remaining -= l.Quantity;
                }

                if (summary.NothingUnderCap)
                    result.Warnings.Add($"{name}: nothing at or under {item.MaxPrice:N0}g (cheapest is {summary.CheapestPrice:N0}g on {summary.CheapestWorld}).");
                else if (!summary.AnyListingsAtAll)
                    result.Warnings.Add($"{name}: no listings anywhere on {scope}.");
                else if (!summary.Satisfied)
                    result.Warnings.Add($"{name}: only {summary.FoundUnits} of {summary.Requested} available under cap.");

                result.Items.Add(summary);
            }

            // Visit order: whatever world we're already standing on first (free), then the most
            // valuable stops, so an interrupted run has still banked the biggest wins.
            var here = WorldInfo.CurrentWorld();
            foreach (var stop in byWorld.Values
                         .OrderByDescending(s => string.Equals(s.World, here, StringComparison.OrdinalIgnoreCase))
                         .ThenByDescending(s => s.TotalCost))
            {
                stop.Lines.Sort((a, b) => a.UnitPrice.CompareTo(b.UnitPrice));
                result.Stops.Add(stop);
            }

            if (result.Stops.Count > 1 && !LifestreamBridge.Available)
                result.Warnings.Add("Lifestream isn't loaded — world hops will be skipped; only the world you're on can be bought from.");

            Plan = result;
            Status = result.HasWork
                ? $"{result.GrandUnits} unit(s) across {result.Stops.Count} world(s) for {result.GrandTotal:N0}g."
                : "Scan finished — nothing to buy under your caps.";
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Status = $"Scan failed: {ex.Message}";
        }
        finally
        {
            Scanning = false;
        }
    }

    private static string Normalise(string s) =>
        new(( s ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
