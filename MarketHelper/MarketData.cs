using System;
using System.Collections.Generic;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace MarketHelper;

public struct Listing
{
    public uint Price;
    public bool IsHq;
    public string Retainer;
    public ulong RetainerId;
    public uint Quantity;
}

/// <summary>
/// Reads the game's already-parsed market board data out of InfoProxyItemSearch,
/// rather than scraping AtkTextNodes. Populated by the game after "Compare Prices"
/// is fired on the RetainerSell addon.
/// </summary>
public static unsafe class MarketData
{
    public static InfoProxyItemSearch* GetProxy()
    {
        var agent = AgentItemSearch.Instance();
        if (agent == null) return null;
        return agent->InfoProxyItemSearch;
    }

    /// <summary>True while the client is still waiting on listing data from the server.</summary>
    public static bool IsWaiting()
    {
        var p = GetProxy();
        return p == null || p->WaitingForListings;
    }

    public static uint SearchItemId()
    {
        var p = GetProxy();
        return p == null ? 0 : p->SearchItemId;
    }

    /// <summary>
    /// Number of general-market listings that actually belong to the current SearchItemId.
    /// The proxy's Listings buffer can transiently hold the PREVIOUS item's rows while
    /// SearchItemId has already advanced — filtering by each listing's own ItemId rejects those.
    /// </summary>
    public static int ListingCount()
    {
        var p = GetProxy();
        if (p == null) return 0;
        var searchId = p->SearchItemId;
        if (searchId == 0) return 0;
        var raw = (int)Math.Min(p->ListingCount, (uint)p->Listings.Length);
        var n = 0;
        for (var i = 0; i < raw; i++)
            if (p->Listings[i].ItemId == searchId && p->Listings[i].UnitPrice > 0) n++;
        return n;
    }

    /// <summary>Clean item name for the current search item, from the Item sheet (AllaganMarket's approach).</summary>
    public static string SearchItemName()
    {
        var id = SearchItemId();
        if (id == 0) return string.Empty;
        var row = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>()?.GetRowOrDefault(id);
        return row?.Name.ExtractText() ?? string.Empty;
    }

    /// <summary>
    /// The lowest non-zero unit price currently in the listings, or 0 if none are loaded.
    /// This is a data-presence signal: reading the actual price rather than trusting the
    /// WaitingForListings flag (which clears a frame before the array is populated).
    /// </summary>
    public static uint FirstPrice()
    {
        var p = GetProxy();
        if (p == null) return 0;
        var searchId = p->SearchItemId;
        if (searchId == 0) return 0;
        var count = (int)Math.Min(p->ListingCount, (uint)p->Listings.Length);
        uint min = 0;
        for (var i = 0; i < count; i++)
        {
            if (p->Listings[i].ItemId != searchId) continue; // reject stale cross-item rows
            var price = p->Listings[i].UnitPrice;
            if (price == 0) continue;
            if (min == 0 || price < min) min = price;
        }
        return min;
    }

    /// <summary>True when real listing data is readable (count &gt; 0 and a non-zero price present).</summary>
    public static bool ListingsReady() => ListingCount() > 0 && FirstPrice() > 0;

    /// <summary>Snapshot the current general-market listings, sorted ascending by unit price.</summary>
    public static List<Listing> GetListings()
    {
        var result = new List<Listing>();
        var p = GetProxy();
        if (p == null) return result;

        var searchId = p->SearchItemId;
        if (searchId == 0) return result;
        var count = (int)Math.Min(p->ListingCount, (uint)p->Listings.Length);
        for (var i = 0; i < count; i++)
        {
            ref var l = ref p->Listings[i];
            if (l.ItemId != searchId) continue; // reject stale rows from a previous item's search
            if (l.UnitPrice == 0) continue;
            result.Add(new Listing
            {
                Price = l.UnitPrice,
                IsHq = l.IsHqItem,
                Retainer = l.CharacterName.ToString(),
                RetainerId = l.RetainerId,
                Quantity = l.Quantity,
            });
        }

        result.Sort((a, b) => a.Price.CompareTo(b.Price));
        return result;
    }

    /// <summary>The player's own retainer names, as cached by the InfoProxy.</summary>
    public static List<string> GetMyRetainerNames()
    {
        var names = new List<string>();
        var p = GetProxy();
        if (p == null) return names;

        var count = (int)Math.Min(p->PlayerRetainerCount, (uint)p->PlayerRetainers.Length);
        for (var i = 0; i < count; i++)
        {
            ref var r = ref p->PlayerRetainers[i];
            var n = r.Name.ToString();
            if (!string.IsNullOrEmpty(n)) names.Add(n);
        }
        return names;
    }
}
