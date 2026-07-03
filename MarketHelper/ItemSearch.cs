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

    private static List<Hit> Marketable()
    {
        if (_marketable != null) return _marketable;
        _marketable = new List<Hit>();
        var sheet = Svc.Data.GetExcelSheet<Item>();
        if (sheet == null) return _marketable;
        foreach (var row in sheet)
        {
            // ItemSearchCategory.RowId != 0 => sellable on the market board.
            if (row.ItemSearchCategory.RowId == 0) continue;
            var name = row.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(name)) continue;
            _marketable.Add(new Hit(row.RowId, name));
        }
        return _marketable;
    }

    /// <summary>Case-insensitive substring search, capped. Exact/prefix matches first.</summary>
    public static List<Hit> Find(string query, int max = 15)
    {
        var q = query.Trim();
        if (q.Length < 2) return new List<Hit>();
        var ql = q.ToLowerInvariant();
        return Marketable()
            .Where(h => h.Name.ToLowerInvariant().Contains(ql))
            .OrderBy(h => h.Name.ToLowerInvariant().StartsWith(ql) ? 0 : 1)
            .ThenBy(h => h.Name.Length)
            .Take(max)
            .ToList();
    }

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
}
