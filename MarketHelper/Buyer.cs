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
    public string DataCenter = string.Empty;
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
    public string DataCenter = string.Empty;
    public readonly List<BuyLine> Lines = new();

    public long TotalCost => Lines.Sum(l => l.Total);
    public int TotalUnits => Lines.Sum(l => l.Quantity);
    public int ItemTypes => Lines.Select(l => l.ItemId).Distinct().Count();
}

/// <summary>A single cheapest-listing entry, shown regardless of whether it clears the cap.</summary>
public sealed class CheapestListing
{
    public string World = string.Empty;
    public string DataCenter = string.Empty;
    public string Seller = string.Empty;
    public long UnitPrice;
    public int Quantity;
    public bool Hq;
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
    public string CheapestDataCenter = string.Empty;
    public long MaxPrice;
    public bool Capped;              // false = no price ceiling on this item
    public bool AnyListingsAtAll;
    public readonly Dictionary<string, (int Units, long Cheapest)> PerWorld = new();

    /// <summary>
    /// Everything the scan saw on each world for this item, whether or not the plan allocated it.
    /// The plan spreads an order across the cheapest listings globally, so a world can hold far
    /// more than it was given — this is what lets a later stop top up an earlier shortfall.
    /// </summary>
    public readonly Dictionary<string, int> AvailableByWorld = new();

    /// <summary>
    /// Listings the scan found but the plan did NOT allocate — everything past the cut, still in
    /// price order. If the planned listings come up short on the road, the run extends itself from
    /// the front of this list: the 23rd, 24th cheapest and so on. Consumed entries are removed as
    /// they're used, so a second pass carries on where the first stopped.
    /// </summary>
    public readonly List<BuyLine> Spare = new();

    /// <summary>
    /// The N cheapest listings in scope, INCLUDING ones above your cap. This is what tells you
    /// "nothing at 1,000g, but here's what it actually costs" instead of just reporting nothing.
    /// </summary>
    public readonly List<CheapestListing> Cheapest = new();

    public bool Satisfied => FoundUnits >= Requested;
    public bool NothingUnderCap => Capped && AnyListingsAtAll && FoundUnits == 0;
    public string CapText => Capped ? $"{MaxPrice:N0}g" : "none";
}

/// <summary>The full result of a scan: what to buy, where, and what it'll cost.</summary>
public sealed class BuyPlanResult
{
    public DateTime ScannedAt = DateTime.Now;
    public string Scope = string.Empty;
    public readonly List<WorldStop> Stops = new();
    public readonly List<ItemSummary> Items = new();
    public readonly List<string> Warnings = new();

    /// <summary>
    /// Units REALLY bought against this plan, accumulated across every SEND that uses it.
    ///
    /// It lives on the plan rather than on the runner because progress has to survive Stop →
    /// SEND: stopping halfway through 5 Bar Racks with 3 bought and pressing SEND again must
    /// buy 2, not 5. A fresh SCAN builds a new plan, which starts this empty again — that is
    /// exactly the moment the tally SHOULD reset.
    ///
    /// Dry runs never write here; they keep their own throwaway tally.
    /// </summary>
    public readonly Dictionary<uint, int> Bought = new();

    public int BoughtFor(uint itemId) => Bought.TryGetValue(itemId, out var n) ? n : 0;

    /// <summary>
    /// Units bought at a specific stop, keyed "stopIndex|itemId". A stop must not re-buy its whole
    /// allocation on a second SEND just because it was interrupted partway — this is what lets it
    /// pick up the 1 it still owes instead of the 3 it was originally given.
    /// </summary>
    public readonly Dictionary<string, int> StopProgress = new();

    public static string StopKey(int stopIndex, uint itemId) => $"{stopIndex}|{itemId}";

    public int BoughtAtStop(int stopIndex, uint itemId)
        => StopProgress.TryGetValue(StopKey(stopIndex, itemId), out var n) ? n : 0;

    /// <summary>
    /// Units already written back to the shopping list. Bought is CUMULATIVE for the whole plan,
    /// but the list gets deducted each time a run ends — so banking must only ever push the
    /// difference. Without this, stopping twice deducts the same purchases twice and unticks an
    /// order that still has units outstanding.
    /// </summary>
    public readonly Dictionary<uint, int> Banked = new();

    public int BankedFor(uint itemId) => Banked.TryGetValue(itemId, out var n) ? n : 0;

    public void ClearProgress()
    {
        Bought.Clear();
        StopProgress.Clear();
        Banked.Clear();
    }

    public long GrandTotal => Stops.Sum(s => s.TotalCost);
    public int GrandUnits => Stops.Sum(s => s.TotalUnits);
    public bool HasWork => Stops.Any(s => s.Lines.Count > 0);
}

/// <summary>
/// Backing state for the Buyer tab's SCAN step. Queries Universalis off the UI thread, applies
/// each item's price cap, and groups the surviving listings into one stop per world.
///
/// MAIN-THREAD RULE — the reason this class looks the way it does.
///
/// Dalamud throws "Not on main thread!" the moment a background task touches client state
/// (the local player, the object table, IPC). The HTTP work genuinely has to be off-thread, so
/// everything game-side is captured into a <see cref="ScanContext"/> snapshot FIRST, on the
/// framework thread, inside <see cref="Scan"/>. <see cref="ScanAsync"/> is then pure: HTTP plus
/// arithmetic, reading only that snapshot. Nothing in the async body may call Svc, Player,
/// WorldInfo, ItemSearch or any bridge — if you need a new game value, add it to the snapshot.
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

    /// <summary>Everything the scan needs from the game, read once on the main thread.</summary>
    private sealed class ScanContext
    {
        public List<string> Locations = new();   // Universalis query targets (world, DC or region)
        public string CurrentWorld = string.Empty;
        public string CurrentDataCenter = string.Empty;
        public bool LifestreamPresent;
        public HashSet<string> MyRetainers = new();
        public HashSet<string> ExcludedWorlds = new();
        public Dictionary<string, string> WorldToDc = new();
        public Dictionary<uint, string> Names = new();
        public int DelayMs;
        public int CheapestCount;
        public int Depth;
        public bool DcPriority;
        public List<string> DcOrder = new();

        public string ScopeLabel => Locations.Count == 0
            ? "(nothing selected)"
            : string.Join(", ", Locations);
    }

    /// <summary>
    /// What to ask Universalis for, straight from the scope selector. Universalis takes a world,
    /// a data center or a region name at the same endpoint, so each mode is just a different
    /// location string — mode 3 is the only one that yields more than one.
    /// </summary>
    public List<string> ResolveLocations()
    {
        switch (Math.Clamp(Cfg.BuyerScopeMode, 0, 3))
        {
            case 0:
            {
                var world = WorldInfo.CurrentWorld();
                return string.IsNullOrWhiteSpace(world) ? new List<string>() : new List<string> { world };
            }
            case 1:
            {
                var dc = WorldInfo.CurrentDataCenter();
                return string.IsNullOrWhiteSpace(dc) ? new List<string>() : new List<string> { dc };
            }
            case 2:
            {
                // Expand the region into its data centers rather than querying "North-America"
                // once. The listing depth is PER QUERY, so a single region call capped the whole
                // region at one page — for a deep item that's a fraction of what's really listed.
                // Four DC calls give four times the depth and per-DC failure reporting.
                var dcs = WorldInfo.DataCentersInRegion(WorldInfo.CurrentRegionId());
                if (dcs.Count > 0) return dcs;

                var region = WorldInfo.CurrentRegion();
                return string.IsNullOrWhiteSpace(region) ? new List<string>() : new List<string> { region };
            }
            default:
            {
                var chosen = Cfg.BuyerDataCenters
                    .Where(d => !string.IsNullOrWhiteSpace(d))
                    .Select(d => d.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (chosen.Count > 0) return chosen;

                var here = WorldInfo.CurrentDataCenter();
                return string.IsNullOrWhiteSpace(here) ? new List<string>() : new List<string> { here };
            }
        }
    }

    /// <summary>
    /// Start a scan. MUST be called from the framework/UI thread — it reads client state to build
    /// the snapshot before handing off to the background task.
    /// </summary>
    public void Scan()
    {
        if (Scanning) return;
        var items = Cfg.BuyerItems.Where(i => i.Enabled && i.ItemId != 0 && i.Quantity > 0).ToList();
        if (items.Count == 0) { Error = "Nothing on the shopping list."; Status = Error; return; }

        ScanContext ctx;
        try
        {
            // ---- main-thread snapshot: every game read happens here and nowhere else ----
            ctx = new ScanContext
            {
                Locations = ResolveLocations(),
                CurrentWorld = WorldInfo.CurrentWorld(),
                CurrentDataCenter = WorldInfo.CurrentDataCenter(),
                LifestreamPresent = LifestreamBridge.Available,
                DelayMs = Math.Clamp(Cfg.BuyerScanDelayMs, 0, 2000),
                CheapestCount = Math.Clamp(Cfg.BuyerShowCheapestCount, 0, 25),
                Depth = Math.Clamp(Cfg.BuyerListingDepth, 20, 500),
                DcPriority = Cfg.BuyerDcPriorityEnabled,
                DcOrder = Cfg.BuyerDcPriority.Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => d.Trim()).ToList(),
                WorldToDc = WorldInfo.WorldToDataCenter(),
                MyRetainers = new HashSet<string>(
                    Cfg.MyRetainers.Where(n => !string.IsNullOrWhiteSpace(n)).Select(Normalise)),
                ExcludedWorlds = new HashSet<string>(
                    Cfg.BuyerExcludedWorlds.Where(w => !string.IsNullOrWhiteSpace(w)).Select(w => w.Trim()),
                    StringComparer.OrdinalIgnoreCase),
            };
            foreach (var item in items)
            {
                var name = ItemSearch.FindById(item.ItemId);
                if (string.IsNullOrEmpty(name)) name = ItemSearch.FindByIdAny(item.ItemId);
                if (string.IsNullOrEmpty(name)) name = $"Item #{item.ItemId}";
                ctx.Names[item.ItemId] = name;
            }
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Status = $"Scan failed while reading game data: {ex.Message}";
            return;
        }

        if (ctx.Locations.Count == 0)
        {
            Error = "Nothing to scan — pick a scope (and in Custom mode, tick at least one data center).";
            Status = Error;
            return;
        }

        Error = null;
        Scanning = true;
        Status = $"Scanning {ctx.ScopeLabel}...";
        _ = ScanAsync(items, ctx);
    }

    public void ClearPlan()
    {
        Plan = null;
        Status = "Not scanned yet.";
        Error = null;
    }

    /// <summary>
    /// Background half of the scan. Touches NOTHING game-side — only the snapshot, the config
    /// values captured through Cfg (plain POCO reads), and Universalis over HTTP.
    /// </summary>
    private async Task ScanAsync(List<BuyerItem> items, ScanContext ctx)
    {
        var result = new BuyPlanResult { Scope = ctx.ScopeLabel };
        try
        {
            var allowOvershoot = Cfg.BuyerAllowOvershoot;
            var byWorld = new Dictionary<string, WorldStop>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in items)
            {
                var name = ctx.Names.TryGetValue(item.ItemId, out var n) ? n : $"Item #{item.ItemId}";

                var summary = new ItemSummary
                {
                    ItemId = item.ItemId,
                    ItemName = name,
                    Requested = item.Quantity,
                    MaxPrice = item.MaxPrice,
                    Capped = item.UseMaxPrice,
                };

                var cap = item.EffectiveCap;

                // One query per location, fired in parallel — the same shape the Flipper tab has
                // used reliably for cross-region lookups. Universalis has no "several DCs at once"
                // endpoint, so in Custom mode the DC list you chose is exactly what gets queried:
                // nothing wider, nothing you didn't tick.
                var tasks = ctx.Locations
                    .Select(loc => (Location: loc, Task: Universalis.GetListingsAsync(loc, item.ItemId, ctx.Depth, item.HqOnly)))
                    .ToList();
                await Task.WhenAll(tasks.Select(t => t.Task));

                var merged = new List<Universalis.Listing>();
                var failures = new List<string>();
                foreach (var (loc, task) in tasks)
                {
                    var res = task.Result;
                    if (res.Error != null) { failures.Add($"{loc} ({res.Error})"); continue; }
                    merged.AddRange(res.Listings);
                }

                if (failures.Count > 0)
                    result.Warnings.Add($"{name}: lookup failed on {string.Join(", ", failures)}.");
                if (merged.Count == 0 && failures.Count == ctx.Locations.Count)
                {
                    result.Items.Add(summary);
                    continue;
                }

                // Pace between ITEMS rather than between locations, so a long shopping list can't
                // machine-gun Universalis even though each item's locations go out together.
                if (ctx.DelayMs > 0) await Task.Delay(ctx.DelayMs);

                // Own retainers are filtered out — you can't buy from yourself, and one of ours
                // showing up as "the cheapest" would just wedge the run on that row. Excluded
                // worlds are dropped here too, so they never reach the plan or the route.
                // Dedupe ONLY on Universalis's listing id, and only when it gave us one.
                // An earlier version keyed on world+retainer+price+quantity, which quietly threw
                // away real inventory: one retainer commonly lists 16 identical rows of the same
                // furnishing, and every one of them is separately purchasable. That showed up as
                // "only 3 of 26" for an item with hundreds listed.
                var seen = new HashSet<string>();
                var listings = merged
                    .Where(l => string.IsNullOrEmpty(l.ListingId) || seen.Add(l.ListingId))
                    .Where(l => l.PricePerUnit > 0 && l.Quantity > 0)
                    .Where(l => !item.HqOnly || l.Hq)
                    .Where(l => !ctx.MyRetainers.Contains(Normalise(l.Retainer)))
                    .Where(l => !ctx.ExcludedWorlds.Contains(l.World))
                    .OrderBy(l => l.PricePerUnit)
                    .ToList();

                summary.AnyListingsAtAll = listings.Count > 0;
                if (listings.Count > 0)
                {
                    summary.CheapestPrice = listings[0].PricePerUnit;
                    summary.CheapestWorld = listings[0].World;
                    summary.CheapestDataCenter = DcOf(ctx, listings[0].World);

                    // Captured BEFORE the cap filter below, deliberately — the whole point is to
                    // show real prices when nothing clears the cap.
                    foreach (var l in listings.Take(ctx.CheapestCount))
                        summary.Cheapest.Add(new CheapestListing
                        {
                            World = l.World,
                            DataCenter = DcOf(ctx, l.World),
                            Seller = l.Retainer,
                            UnitPrice = l.PricePerUnit,
                            Quantity = l.Quantity,
                            Hq = l.Hq,
                        });
                }

                foreach (var l in listings)
                {
                    if (cap != long.MaxValue && l.PricePerUnit > cap) continue;
                    var w = l.World;
                    if (string.IsNullOrWhiteSpace(w)) continue;
                    summary.AvailableByWorld[w] = (summary.AvailableByWorld.TryGetValue(w, out var have) ? have : 0) + l.Quantity;
                }

                var remaining = item.Quantity;
                foreach (var l in listings)
                {
                    if (remaining <= 0) break;
                    if (l.PricePerUnit > cap) break;   // sorted, so everything after is dearer

                    // A market listing is bought whole — you cannot take part of a stack.
                    if (l.Quantity > remaining && !allowOvershoot) continue;

                    var world = l.World;
                    if (string.IsNullOrWhiteSpace(world)) continue;   // can't route to an unknown world
                    var dcOfWorld = DcOf(ctx, world);

                    if (!byWorld.TryGetValue(world, out var stop))
                    {
                        stop = new WorldStop { World = world, DataCenter = dcOfWorld };
                        byWorld[world] = stop;
                    }
                    stop.Lines.Add(new BuyLine
                    {
                        ItemId = item.ItemId,
                        ItemName = name,
                        World = world,
                        DataCenter = dcOfWorld,
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

                // Everything the plan didn't take, cheapest first, kept for a top-up pass.
                var allocatedKeys = new HashSet<string>(
                    result.Stops.SelectMany(st => st.Lines)
                        .Where(bl => bl.ItemId == item.ItemId)
                        .Select(bl => $"{bl.World}|{bl.Seller}|{bl.UnitPrice}|{bl.Quantity}"));
                var takenHere = new HashSet<string>();
                foreach (var line in byWorld.Values.SelectMany(st => st.Lines).Where(bl => bl.ItemId == item.ItemId))
                    takenHere.Add($"{line.World}|{line.Seller}|{line.UnitPrice}|{line.Quantity}");

                var spareBudget = 300;
                foreach (var l in listings)
                {
                    if (spareBudget <= 0) break;
                    if (l.PricePerUnit > cap) break;                 // sorted — the rest are dearer
                    var key = $"{l.World}|{l.Retainer}|{l.PricePerUnit}|{l.Quantity}";
                    if (takenHere.Remove(key) || allocatedKeys.Remove(key)) continue;   // already planned
                    if (string.IsNullOrWhiteSpace(l.World)) continue;

                    summary.Spare.Add(new BuyLine
                    {
                        ItemId = item.ItemId,
                        ItemName = name,
                        World = l.World,
                        DataCenter = DcOf(ctx, l.World),
                        UnitPrice = l.PricePerUnit,
                        Quantity = l.Quantity,
                        Hq = l.Hq,
                        Seller = l.Retainer,
                    });
                    spareBudget--;
                }

                if (summary.NothingUnderCap)
                    result.Warnings.Add($"{name}: none available at {item.MaxPrice:N0}g — cheapest is {summary.CheapestPrice:N0}g on {summary.CheapestWorld}. See the cheapest listings under \"By item\".");
                else if (!summary.AnyListingsAtAll)
                    result.Warnings.Add($"{name}: no listings on {ctx.ScopeLabel}.");
                else if (!summary.Satisfied)
                    result.Warnings.Add(summary.Capped
                        ? $"{name}: only {summary.FoundUnits} of {summary.Requested} available under cap."
                        : $"{name}: only {summary.FoundUnits} of {summary.Requested} listed anywhere in {ctx.ScopeLabel}.");

                result.Items.Add(summary);
            }

            var dcValue = byWorld.Values
                .GroupBy(w => w.DataCenter, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Sum(w => w.TotalCost), StringComparer.OrdinalIgnoreCase);

            IEnumerable<WorldStop> ordered;
            if (ctx.DcPriority && ctx.DcOrder.Count > 0)
            {
                // Strict data-center order. Every world on the first DC is cleared before moving
                // to the second, so the whole run costs one transfer per DC. The trade-off is
                // real: if your bags fill early you'll have bought whatever the first DC had,
                // not the most valuable listings overall.
                ordered = byWorld.Values
                    .OrderBy(w => DcRank(ctx, w.DataCenter))
                    .ThenByDescending(w => string.Equals(w.World, ctx.CurrentWorld, StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(w => w.TotalCost);
            }
            else
            {
                // Default: cheapest travel first —
                //   1. the world we're already standing on (free),
                //   2. the rest of our current DC (a plain world visit),
                //   3. other DCs, each visited contiguously so we pay ONE transfer per DC,
                //      richest DC first, and richest world first inside each.
                ordered = byWorld.Values
                    .OrderByDescending(w => string.Equals(w.World, ctx.CurrentWorld, StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(w => string.Equals(w.DataCenter, ctx.CurrentDataCenter, StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(w => dcValue.TryGetValue(w.DataCenter, out var v) ? v : 0)
                    .ThenBy(w => w.DataCenter, StringComparer.OrdinalIgnoreCase)
                    .ThenByDescending(w => w.TotalCost);
            }

            foreach (var stop in ordered)
            {
                stop.Lines.Sort((a, b) => a.UnitPrice.CompareTo(b.UnitPrice));
                result.Stops.Add(stop);
            }

            if (ctx.DcPriority && ctx.DcOrder.Count > 0)
                result.Warnings.Add($"Data-center order is fixed: {string.Join(" -> ", ctx.DcOrder)}. Stops follow that order rather than value.");

            if (result.Stops.Count > 1 && !ctx.LifestreamPresent)
                result.Warnings.Add("Lifestream isn't loaded — world hops will be skipped; only the world you're on can be bought from.");

            var foreignDcs = result.Stops
                .Select(s2 => s2.DataCenter)
                .Where(d => !string.IsNullOrWhiteSpace(d)
                            && !string.Equals(d, ctx.CurrentDataCenter, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (foreignDcs.Count > 0)
                result.Warnings.Add($"Route crosses {foreignDcs.Count} other data center(s): {string.Join(", ", foreignDcs)}. Data-center travel queues can be slow, and you must not be in a party.");

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

    /// <summary>Position of a DC in the user's priority list; anything unlisted sorts last.</summary>
    private static int DcRank(ScanContext ctx, string dc)
    {
        if (string.IsNullOrWhiteSpace(dc)) return int.MaxValue;
        for (var i = 0; i < ctx.DcOrder.Count; i++)
            if (string.Equals(ctx.DcOrder[i], dc, StringComparison.OrdinalIgnoreCase)) return i;
        return int.MaxValue;
    }

    private static string DcOf(ScanContext ctx, string world)
        => !string.IsNullOrWhiteSpace(world) && ctx.WorldToDc.TryGetValue(world, out var dc) ? dc : string.Empty;

    private static string Normalise(string s)
        => new((s ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
