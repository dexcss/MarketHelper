using System;
using System.Collections.Generic;
using System.Linq;
using ECommons.DalamudServices;
using Lumina.Excel.Sheets;

namespace MarketHelper;

/// <summary>
/// Resolves item names to IDs from the game's Item sheet, for the Flipper search box.
/// Only items that are marketable (have an ItemSearchCategory) are offered.
/// </summary>
public static class ItemSearch
{
    public readonly struct Hit
    {
        public readonly uint Id;
        public readonly string Name;
        public Hit(uint id, string name) { Id = id; Name = name; }
    }

    private static List<Hit>? _marketable;
    private static List<string>? _marketableLower;   // parallel to _marketable: pre-lowered names

    private static List<Hit> Marketable()
    {
        if (_marketable != null) return _marketable;
        var hits = new List<Hit>();
        var lower = new List<string>();
        var sheet = Svc.Data.GetExcelSheet<Item>();
        if (sheet != null)
        {
            foreach (var row in sheet)
            {
                // ItemSearchCategory.RowId != 0 => sellable on the market board.
                if (row.ItemSearchCategory.RowId == 0) continue;
                var name = row.Name.ExtractText();
                if (string.IsNullOrWhiteSpace(name)) continue;
                hits.Add(new Hit(row.RowId, name));
                lower.Add(name.ToLowerInvariant());
            }
        }
        _marketable = hits;
        _marketableLower = lower;
        return _marketable;
    }

    /// <summary>
    /// Case-insensitive substring search, capped. Exact/prefix matches first.
    ///
    /// The result for the last query is cached: the UI calls this every frame while the query
    /// box holds text, and re-lowering ~15k item names per frame was thousands of throwaway
    /// string allocations a second for an unchanged query.
    /// </summary>
    public static List<Hit> Find(string query, int max = 15)
    {
        var q = query.Trim();
        if (q.Length < 2) return new List<Hit>();
        if (_lastFindQuery == q && _lastFindMax == max && _lastFindResults != null) return _lastFindResults;

        var ql = q.ToLowerInvariant();
        Marketable();
        var hits = new List<Hit>();
        for (var i = 0; i < _marketable!.Count; i++)
        {
            var nameLower = _marketableLower![i];
            if (!nameLower.Contains(ql)) continue;
            hits.Add(_marketable[i]);
        }
        hits.Sort((a, b) =>
        {
            var aPrefix = a.Name.ToLowerInvariant().StartsWith(ql, StringComparison.Ordinal) ? 0 : 1;
            var bPrefix = b.Name.ToLowerInvariant().StartsWith(ql, StringComparison.Ordinal) ? 0 : 1;
            if (aPrefix != bPrefix) return aPrefix - bPrefix;
            return a.Name.Length - b.Name.Length;
        });
        _lastFindQuery = q; _lastFindMax = max; _lastFindResults = hits.Take(max).ToList();
        return _lastFindResults;
    }

    private static string? _lastFindQuery;
    private static int _lastFindMax = -1;
    private static List<Hit>? _lastFindResults;

    private static Dictionary<uint, string>? _byId;

    /// <summary>Name for a marketable item id, or empty if not marketable / not found.</summary>
    public static string FindById(uint id)
    {
        if (_byId == null)
        {
            _byId = new Dictionary<uint, string>();
            foreach (var h in Marketable()) _byId[h.Id] = h.Name;
        }
        return _byId.TryGetValue(id, out var name) ? name : string.Empty;
    }

    /// <summary>
    /// Exact (case-insensitive) name lookup over MARKETABLE items. Returns 0 if not found.
    /// Used by the MakePlace importer, which has full item names and must not fuzzy-match —
    /// "Everkeep Sofa" and "Curved Everkeep Sofa" are different furnishings.
    /// </summary>
    public static uint FindExact(string name)
    {
        var n = (name ?? string.Empty).Trim();
        if (n.Length == 0) return 0;
        foreach (var h in Marketable())
            if (string.Equals(h.Name, n, StringComparison.OrdinalIgnoreCase)) return h.Id;
        return 0;
    }

    /// <summary>Exact name lookup over ALL items, marketable or not. Returns 0 if not found.</summary>
    public static uint FindExactAny(string name)
    {
        var n = (name ?? string.Empty).Trim();
        if (n.Length == 0) return 0;
        foreach (var h in AllItems())
            if (string.Equals(h.Name, n, StringComparison.OrdinalIgnoreCase)) return h.Id;
        return 0;
    }

    /// <summary>
    /// Game icon id for an item, or 0 if unknown. Cached because the Lister redraws every frame
    /// and a sheet lookup per row per frame is wasteful.
    /// </summary>
    private static readonly Dictionary<uint, ushort> IconCache = new();

    public static ushort IconId(uint itemId)
    {
        if (IconCache.TryGetValue(itemId, out var cached)) return cached;

        ushort icon = 0;
        try
        {
            var row = Svc.Data.GetExcelSheet<Item>()?.GetRowOrDefault(itemId);
            icon = row?.Icon ?? 0;
        }
        catch { icon = 0; }

        IconCache[itemId] = icon;
        return icon;
    }

    // ---- Unfiltered index: ALL named items, including market-prohibited ones. Used by the Item
    // Gatherer, which just moves items between retainer and bags and doesn't care about the market.

    private static List<Hit>? _allItems;
    private static Dictionary<uint, string>? _allById;
    private static List<string>? _allLower;   // parallel to _allItems: pre-lowered names

    private static List<Hit> AllItems()
    {
        if (_allItems != null) return _allItems;
        var hits = new List<Hit>();
        var lower = new List<string>();
        var byId = new Dictionary<uint, string>();
        var sheet = Svc.Data.GetExcelSheet<Item>();
        if (sheet != null)
        {
            foreach (var row in sheet)
            {
                var name = row.Name.ExtractText();
                if (string.IsNullOrWhiteSpace(name)) continue;
                hits.Add(new Hit(row.RowId, name));
                lower.Add(name.ToLowerInvariant());
                byId[row.RowId] = name;
            }
        }
        _allItems = hits;
        _allLower = lower;
        _allById = byId;
        return _allItems;
    }

    /// <summary>
    /// Case-insensitive substring search over ALL items (incl. unmarketable), capped.
    /// Cached for the last query — see <see cref="Find"/> for why.
    /// </summary>
    public static List<Hit> FindAny(string query, int max = 15)
    {
        var q = query.Trim();
        if (q.Length < 2) return new List<Hit>();
        if (_lastFindAnyQuery == q && _lastFindAnyMax == max && _lastFindAnyResults != null) return _lastFindAnyResults;

        var ql = q.ToLowerInvariant();
        AllItems();
        var hits = new List<Hit>();
        for (var i = 0; i < _allItems!.Count; i++)
        {
            if (!_allLower![i].Contains(ql)) continue;
            hits.Add(_allItems[i]);
        }
        hits.Sort((a, b) =>
        {
            var aPrefix = a.Name.ToLowerInvariant().StartsWith(ql, StringComparison.Ordinal) ? 0 : 1;
            var bPrefix = b.Name.ToLowerInvariant().StartsWith(ql, StringComparison.Ordinal) ? 0 : 1;
            if (aPrefix != bPrefix) return aPrefix - bPrefix;
            return a.Name.Length - b.Name.Length;
        });
        _lastFindAnyQuery = q; _lastFindAnyMax = max; _lastFindAnyResults = hits.Take(max).ToList();
        return _lastFindAnyResults;
    }

    private static string? _lastFindAnyQuery;
    private static int _lastFindAnyMax = -1;
    private static List<Hit>? _lastFindAnyResults;

    /// <summary>Name for ANY item id (incl. unmarketable), or empty if not found.</summary>
    public static string FindByIdAny(uint id)
    {
        if (_allById == null) AllItems();
        return _allById != null && _allById.TryGetValue(id, out var name) ? name : string.Empty;
    }
}
