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

    public readonly Dictionary<string, Universalis.LocationResult> Results = new();

    public bool HasResults => Results.Count > 0 && Results.Values.Any(r => r.HasData);

    public void Search(uint itemId, string name)
    {
        ItemId = itemId;
        ItemName = name;
        Error = null;
        Results.Clear();
        Loading = true;
        _ = RunAsync();
    }

    private async Task RunAsync()
    {
        try
        {
            var tasks = Regions.ToDictionary(
                r => r,
                r => Universalis.GetListingsAsync(r, ItemId, 10, HqOnly));
            await Task.WhenAll(tasks.Values);
            foreach (var (region, task) in tasks)
                Results[region] = task.Result;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            Loading = false;
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
