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
}
