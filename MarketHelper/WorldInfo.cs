using System;
using System.Collections.Generic;
using System.Linq;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using Lumina.Excel.Sheets;

namespace MarketHelper;

/// <summary>
/// Resolves the player's current world and data center, and enumerates worlds/DCs for the
/// Lister's server/DC selectors. Names here are the game's, which match Universalis's world/DC
/// names (Universalis sources them from the same data).
/// </summary>
public static class WorldInfo
{
    /// <summary>The player's current (home) world name, or empty if unavailable.</summary>
    public static string CurrentWorld()
    {
        if (!Player.Available) return string.Empty;
        return Player.Object.CurrentWorld.Value.Name.ExtractText();
    }

    /// <summary>The data center name for the player's current world, or empty.</summary>
    public static string CurrentDataCenter()
    {
        if (!Player.Available) return string.Empty;
        var dc = Player.Object.CurrentWorld.Value.DataCenter;
        return dc.ValueNullable?.Name.ExtractText() ?? string.Empty;
    }

    /// <summary>All data-center names that have at least one public world.</summary>
    public static List<string> AllDataCenters()
    {
        var worlds = Svc.Data.GetExcelSheet<World>();
        if (worlds == null) return new List<string>();
        return worlds
            .Where(w => w.IsPublic && w.DataCenter.RowId != 0)
            .Select(w => w.DataCenter.ValueNullable?.Name.ExtractText() ?? string.Empty)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct()
            .OrderBy(n => n)
            .ToList();
    }

    /// <summary>Public world names on the given data center.</summary>
    public static List<string> WorldsOnDataCenter(string dcName)
    {
        var worlds = Svc.Data.GetExcelSheet<World>();
        if (worlds == null) return new List<string>();
        return worlds
            .Where(w => w.IsPublic
                        && w.DataCenter.ValueNullable?.Name.ExtractText() == dcName
                        && !string.IsNullOrWhiteSpace(w.Name.ExtractText()))
            .Select(w => w.Name.ExtractText())
            .OrderBy(n => n)
            .ToList();
    }

    // Universalis region names, keyed by the data center's Region id (from the DC group sheet).
    // 1=Japan, 2=North-America, 3=Europe, 4=Oceania (Materia). Region 5+ (e.g. 中国) unsupported here.
    private static readonly Dictionary<uint, string> RegionNames = new()
    {
        [1] = "Japan",
        [2] = "North-America",
        [3] = "Europe",
        [4] = "Oceania",
    };

    /// <summary>The Universalis region name for the player's current world, or empty.</summary>
    public static string CurrentRegion()
    {
        if (!Player.Available) return string.Empty;
        var dc = Player.Object.CurrentWorld.Value.DataCenter;
        var regionId = dc.ValueNullable?.Region.RowId ?? 0;
        return RegionNames.TryGetValue(regionId, out var name) ? name : string.Empty;
    }

    /// <summary>The player's current region id (1=Japan, 2=NA, 3=EU, 4=Oceania), or 0.</summary>
    public static uint CurrentRegionId()
    {
        if (!Player.Available) return 0;
        var dc = Player.Object.CurrentWorld.Value.DataCenter;
        return dc.ValueNullable?.Region.RowId ?? 0;
    }

    /// <summary>Region id for a named data center, or 0 if unknown.</summary>
    public static uint RegionIdOfDataCenter(string dcName)
    {
        var worlds = Svc.Data.GetExcelSheet<World>();
        if (worlds == null) return 0;
        foreach (var w in worlds)
        {
            if (!w.IsPublic || w.DataCenter.RowId == 0) continue;
            var dc = w.DataCenter.ValueNullable;
            if (dc == null) continue;
            if (dc.Value.Name.ExtractText() == dcName) return dc.Value.Region.RowId;
        }
        return 0;
    }

    /// <summary>
    /// Public data centers in the given region, e.g. region 2 (North America) yields Aether,
    /// Crystal, Dynamis and Primal. These are the DCs a character in that region can travel to.
    /// </summary>
    public static List<string> DataCentersInRegion(uint regionId)
    {
        var worlds = Svc.Data.GetExcelSheet<World>();
        if (worlds == null) return new List<string>();
        return worlds
            .Where(w => w.IsPublic && w.DataCenter.RowId != 0
                        && (w.DataCenter.ValueNullable?.Region.RowId ?? 0) == regionId)
            .Select(w => w.DataCenter.ValueNullable?.Name.ExtractText() ?? string.Empty)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct()
            .OrderBy(n => n)
            .ToList();
    }

    /// <summary>Data center name for a named world, or empty.</summary>
    public static string DataCenterOfWorld(string worldName)
    {
        var worlds = Svc.Data.GetExcelSheet<World>();
        if (worlds == null) return string.Empty;
        foreach (var w in worlds)
        {
            if (!w.IsPublic) continue;
            if (w.Name.ExtractText() != worldName) continue;
            return w.DataCenter.ValueNullable?.Name.ExtractText() ?? string.Empty;
        }
        return string.Empty;
    }

    /// <summary>Every public world name mapped to its data center. One sheet pass.</summary>
    public static Dictionary<string, string> WorldToDataCenter()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var worlds = Svc.Data.GetExcelSheet<World>();
        if (worlds == null) return map;
        foreach (var w in worlds)
        {
            if (!w.IsPublic || w.DataCenter.RowId == 0) continue;
            var name = w.Name.ExtractText();
            var dc = w.DataCenter.ValueNullable?.Name.ExtractText() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(dc)) continue;
            map[name] = dc;
        }
        return map;
    }
}
