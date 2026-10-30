using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MarketHelper;

/// <summary>
/// Backing state for the Flipper tab. Runs Universalis queries off the UI thread and exposes
/// results for the window to draw. Cross-region: buy at the cheapest location, sell at the
/// dearest, profit = sell - buy.
/// </summary>
public sealed class Flipper
{
    // Universalis region names.
    public static readonly string[] Regions = { "Japan", "North-America", "Europe", "Oceania" };
    // Friendly labels (Oceania's DC is "Materia").
    public static readonly Dictionary<string, string> RegionLabel = new()
    {
        ["Japan"] = "Japan",
        ["North-America"] = "America",
        ["Europe"] = "Europe",
        ["Oceania"] = "Materia (Oceania)",
    };

    public uint ItemId { get; private set; }
    public string ItemName { get; private set; } = string.Empty;
    public bool Loading { get; private set; }
    public string? Error { get; private set; }
    public bool HqOnly;

    /// <summary>
    /// Snapshot of the last search, replaced wholesale when a query finishes. Never mutated in
    /// place: a query completes on the thread pool while the UI thread enumerates this, and a
    /// Dictionary mutated during enumeration throws. Publishing a fresh instance (a single
    /// reference assignment) gives readers a stable view instead.
    /// </summary>
    public IReadOnlyDictionary<string, Universalis.LocationResult> Results { get; private set; }
        = new Dictionary<string, Universalis.LocationResult>();

    public bool HasResults => Results.Count > 0 && Results.Values.Any(r => r.HasData);

    public void Search(uint itemId, string name)
    {
        ItemId = itemId;
        ItemName = name;
        Error = null;
        Results = new Dictionary<string, Universalis.LocationResult>();
        Loading = true;
        _searchGen++;
        var gen = _searchGen;
        _ = RunAsync(gen);
    }

    // Bumped per search. A slow search for item A that finishes after you searched item B must
    // not publish A's results under B's name (or clear B's Loading flag early).
    private int _searchGen;

    private async Task RunAsync(int gen)
    {
        try
        {
            var tasks = Regions.ToDictionary(
                r => r,
                r => Universalis.GetListingsAsync(r, ItemId, 10, HqOnly));
            await Task.WhenAll(tasks.Values);
            if (gen != _searchGen) return;   // a newer search owns the published state now
            // Build off-thread, publish in one assignment — see the Results property.
            var fresh = new Dictionary<string, Universalis.LocationResult>();
            foreach (var (region, task) in tasks)
                fresh[region] = task.Result;
            Results = fresh;
        }
        catch (Exception ex)
        {
            if (gen == _searchGen) Error = ex.Message;
        }
        finally
        {
            if (gen == _searchGen) Loading = false;
        }
    }

    // --- Flip analysis across all regions with data ---

    public readonly struct Flip
    {
        public readonly bool Valid;
        public readonly string BuyRegion, BuyWorld;
        public readonly long BuyPrice;
        public readonly string SellRegion, SellWorld;
        public readonly long SellPrice;
        public long Profit => SellPrice - BuyPrice;
        public Flip(string br, string bw, long bp, string sr, string sw, long sp)
        { Valid = true; BuyRegion = br; BuyWorld = bw; BuyPrice = bp; SellRegion = sr; SellWorld = sw; SellPrice = sp; }
    }

    /// <summary>Cheapest buy vs dearest "market floor" sell across regions.</summary>
    public Flip BestFlip()
    {
        string? buyR = null, buyW = null, sellR = null, sellW = null;
        long buyP = long.MaxValue, sellP = long.MinValue;
        foreach (var (region, res) in Results)
        {
            if (!res.HasData) continue;
            if (res.CheapestPrice < buyP) { buyP = res.CheapestPrice; buyR = region; buyW = res.CheapestWorld; }
            if (res.CheapestPrice > sellP) { sellP = res.CheapestPrice; sellR = region; sellW = res.CheapestWorld; }
        }
        if (buyR == null || sellR == null || buyR == sellR) return default;
        return new Flip(buyR, buyW ?? "", buyP, sellR, sellW ?? "", sellP);
    }
}
