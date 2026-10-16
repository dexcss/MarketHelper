using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace MarketHelper;

/// <summary>
/// Minimal Universalis API client (https://docs.universalis.app). Public, no auth.
/// Used by the Flipper tab to compare prices across regions/DCs/worlds, and by the Buyer's
/// scan to enumerate everything listed on each world in scope.
/// </summary>
public static class Universalis
{
    private static readonly HttpClient Http = new()
    {
        BaseAddress = new Uri("https://universalis.app/"),
        Timeout = TimeSpan.FromSeconds(40),
    };

    static Universalis()
    {
        // Universalis asks apps to identify themselves.
        Http.DefaultRequestHeaders.Add("User-Agent", "MarketHelper-Dalamud/1.0");
    }

    /// <summary>Universalis refuses more than this many item ids in one multi-item request.</summary>
    public const int MaxItemsPerRequest = 100;

    /// <summary>One market listing (cheapest-first).</summary>
    public readonly struct Listing
    {
        public readonly long PricePerUnit;
        public readonly int Quantity;
        public readonly bool Hq;
        public readonly string World;   // populated for DC/region queries
        public readonly string Retainer;

        /// <summary>
        /// Universalis's own listing id. The ONLY safe way to tell two listings apart: one
        /// retainer routinely posts many identical rows (same price, same quantity), and they are
        /// separate purchasable listings, not duplicates.
        /// </summary>
        public readonly string ListingId;

        public Listing(long price, int qty, bool hq, string world, string retainer, string listingId)
        { PricePerUnit = price; Quantity = qty; Hq = hq; World = world; Retainer = retainer; ListingId = listingId; }
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
    /// Fetch listings for an item at a location (world, data center, or region name).
    /// Region names Universalis accepts: "Japan", "North-America", "Europe", "Oceania".
    ///
    /// <paramref name="count"/> of 0 or less omits the <c>listings</c> parameter entirely, which
    /// asks Universalis for everything it has. That matters for bulk orders: the parameter is a
    /// hard ceiling on the response, so a deep item in x99 stacks looks far scarcer than it is
    /// when the request only asks for the cheapest couple of hundred rows.
    /// </summary>
    public static async Task<LocationResult> GetListingsAsync(string location, uint itemId, int count = 10, bool hqOnly = false)
    {
        var map = await GetListingsMultiAsync(location, new[] { itemId }, count, hqOnly);
        return map.TryGetValue(itemId, out var res)
            ? res
            : new LocationResult { Location = location, Error = "no data for item" };
    }

    /// <summary>
    /// Fetch listings for MANY items at one location in a single request. Universalis takes up to
    /// <see cref="MaxItemsPerRequest"/> comma-separated ids and answers with an <c>items</c> map;
    /// longer lists are chunked here. One request per world instead of one per world per item is
    /// the difference between ~30 calls and ~1,500 for a full shopping list.
    /// </summary>
    public static async Task<Dictionary<uint, LocationResult>> GetListingsMultiAsync(
        string location, IEnumerable<uint> itemIds, int count = 10, bool hqOnly = false)
    {
        var ids = (itemIds ?? Enumerable.Empty<uint>()).Where(i => i != 0).Distinct().ToList();
        var output = new Dictionary<uint, LocationResult>();
        foreach (var id in ids) output[id] = new LocationResult { Location = location };
        if (ids.Count == 0) return output;

        for (var offset = 0; offset < ids.Count; offset += MaxItemsPerRequest)
        {
            var chunk = ids.Skip(offset).Take(MaxItemsPerRequest).ToList();
            try
            {
                var hq = hqOnly ? "&hq=true" : string.Empty;
                // listings=0 (or less) means "don't cap me" — leave the parameter off.
                var cap = count > 0 ? $"?listings={count}{hq}" : (hqOnly ? "?hq=true" : string.Empty);
                // The general current-data endpoint accepts world, data-center AND region names,
                // and in multi-item form returns { "items": { "<id>": { "listings": [...] } } }.
                var url = $"api/v2/{Uri.EscapeDataString(location)}/{string.Join(",", chunk)}{cap}";

                using var resp = await Http.GetAsync(url);
                if (!resp.IsSuccessStatusCode)
                {
                    foreach (var id in chunk) output[id].Error = $"HTTP {(int)resp.StatusCode}";
                    continue;
                }

                await using var stream = await resp.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(stream);
                var root = doc.RootElement;

                // A one-id request answers with the flat single-item shape, not the items map.
                if (root.TryGetProperty("items", out var itemsMap) && itemsMap.ValueKind == JsonValueKind.Object)
                {
                    foreach (var id in chunk)
                    {
                        if (!itemsMap.TryGetProperty(id.ToString(), out var node)) continue;
                        ReadListingsInto(node, output[id].Listings, count);
                    }
                }
                else if (chunk.Count == 1)
                {
                    ReadListingsInto(root, output[chunk[0]].Listings, count);
                }
                else
                {
                    foreach (var id in chunk) output[id].Error = "unexpected response shape";
                }
            }
            catch (Exception ex)
            {
                foreach (var id in chunk)
                    if (output[id].Error == null) output[id].Error = ex.Message;
            }
        }

        return output;
    }

    /// <summary>Pull the <c>listings</c> array out of one item node.</summary>
    private static void ReadListingsInto(JsonElement node, List<Listing> into, int count)
    {
        if (!node.TryGetProperty("listings", out var listings) || listings.ValueKind != JsonValueKind.Array)
            return;

        foreach (var l in listings.EnumerateArray())
        {
            var price = l.TryGetProperty("pricePerUnit", out var p) ? p.GetInt64() : 0;
            var qty = l.TryGetProperty("quantity", out var q) ? q.GetInt32() : 1;
            var isHq = l.TryGetProperty("hq", out var h) && h.GetBoolean();
            var world = l.TryGetProperty("worldName", out var w) ? (w.GetString() ?? string.Empty) : string.Empty;
            var ret = l.TryGetProperty("retainerName", out var r) ? (r.GetString() ?? string.Empty) : string.Empty;
            var lid = l.TryGetProperty("listingID", out var li) ? (li.GetString() ?? string.Empty) : string.Empty;
            into.Add(new Listing(price, qty, isHq, world, ret, lid));
            if (count > 0 && into.Count >= count) break;
        }
    }
}
