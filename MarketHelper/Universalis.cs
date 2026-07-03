using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace MarketHelper;

/// <summary>
/// Minimal Universalis API client (https://docs.universalis.app). Public, no auth.
/// Used by the Flipper tab to compare prices across regions/DCs/worlds.
/// </summary>
public static class Universalis
{
    private static readonly HttpClient Http = new()
    {
        BaseAddress = new Uri("https://universalis.app/"),
        Timeout = TimeSpan.FromSeconds(20),
    };

    static Universalis()
    {
        // Universalis asks apps to identify themselves.
        Http.DefaultRequestHeaders.Add("User-Agent", "MarketHelper-Dalamud/1.0");
    }

    /// <summary>One market listing (cheapest-first).</summary>
    public readonly struct Listing
    {
        public readonly long PricePerUnit;
        public readonly int Quantity;
        public readonly bool Hq;
        public readonly string World;   // populated for DC/region queries
        public readonly string Retainer;
        public Listing(long price, int qty, bool hq, string world, string retainer)
        { PricePerUnit = price; Quantity = qty; Hq = hq; World = world; Retainer = retainer; }
    }

    /// <summary>Result of a listings query for one location (world/DC/region).</summary>
    public sealed class LocationResult
    {
        public string Location = string.Empty;
        public List<Listing> Listings = new();
        public long CheapestPrice => Listings.Count > 0 ? Listings[0].PricePerUnit : 0;
        public string CheapestWorld => Listings.Count > 0 ? Listings[0].World : string.Empty;
        public bool HasData => Listings.Count > 0;
        public string? Error;
    }

    /// <summary>
    /// Fetch the cheapest listings for an item at a location (world, data center, or region name).
    /// Region names Universalis accepts: "Japan", "North-America", "Europe", "Oceania".
    /// </summary>
    public static async Task<LocationResult> GetListingsAsync(string location, uint itemId, int count = 10, bool hqOnly = false)
    {
        var result = new LocationResult { Location = location };
        try
        {
            var hq = hqOnly ? "&hq=true" : string.Empty;
            // Use the general current-data endpoint (api/v2/{location}/{itemId}) — it accepts world,
            // data-center AND region names and returns a `listings` array. The dedicated
            // /listings/ path does not reliably support region-scope queries.
            var url = $"api/v2/{Uri.EscapeDataString(location)}/{itemId}?listings={count}{hq}";
            using var resp = await Http.GetAsync(url);
            if (!resp.IsSuccessStatusCode)
            {
                result.Error = $"HTTP {(int)resp.StatusCode}";
                return result;
            }
            await using var stream = await resp.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            var root = doc.RootElement;
            if (root.TryGetProperty("listings", out var listings) && listings.ValueKind == JsonValueKind.Array)
            {
                foreach (var l in listings.EnumerateArray())
                {
                    var price = l.TryGetProperty("pricePerUnit", out var p) ? p.GetInt64() : 0;
                    var qty = l.TryGetProperty("quantity", out var q) ? q.GetInt32() : 1;
                    var isHq = l.TryGetProperty("hq", out var h) && h.GetBoolean();
                    var world = l.TryGetProperty("worldName", out var w) ? (w.GetString() ?? string.Empty) : string.Empty;
                    var ret = l.TryGetProperty("retainerName", out var r) ? (r.GetString() ?? string.Empty) : string.Empty;
                    result.Listings.Add(new Listing(price, qty, isHq, world, ret));
                    if (result.Listings.Count >= count) break;
                }
            }
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }
        return result;
    }
}
