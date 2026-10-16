using System;
using System.Collections.Generic;
using System.Linq;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;

namespace MarketHelper;

/// <summary>
/// Resolves the player's current world and data center, and enumerates worlds/DCs for the
/// Lister's server/DC selectors. Names here are the game's, which match Universalis's world/DC
/// names (Universalis sources them from the same data).
/// </summary>
public static class WorldInfo
{
    /// <summary>
    /// The world the player is standing on right now, or empty if unavailable. During a world visit
    /// this is NOT the home world — callers that mean "home" should read Player.Object.HomeWorld.
    /// </summary>
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

    // Universalis region names, keyed by the data center's WorldRegionGroup row id — the sheet
    // WorldDCGroupType.Region points at. Verified against the installed client's sheets:
    // 1=Japan, 2=North America, 3=Europe, 4=Oceania, 5=China, 6=Korea, 7=NA Cloud,
    // 8=Traditional Chinese. Only the four Universalis supports are named here; the rest resolve
    // to an empty name. Row id 2 is spelled "North-America" because that is what Universalis
    // expects (the sheet itself writes "North America").
    private static readonly Dictionary<uint, string> RegionNames = new()
    {
        [1] = "Japan",
        [2] = "North-America",
        [3] = "Europe",
        [4] = "Oceania",
    };

    /// <summary>The Universalis region name for a data center, by name. Empty if unknown.</summary>
    ///
    /// Straight from the sheet's grouping: WorldDCGroupType.Region → WorldRegionGroup. There is
    /// no name table here any more — one existed only to paper over a region-resolution failure
    /// that, checked against the live game data, the sheet does not actually have.
    public static string RegionOfDataCenter(string dcName)
    {
        var id = RegionIdOfDataCenter(dcName);
        return RegionNames.TryGetValue(id, out var byId) ? byId : string.Empty;
    }

    /// <summary>The Universalis region name for the player's current world, or empty.</summary>
    public static string CurrentRegion()
    {
        var byName = RegionOfDataCenter(CurrentDataCenter());
        if (!string.IsNullOrWhiteSpace(byName)) return byName;

        if (!Player.Available) return string.Empty;
        var dc = Player.Object.CurrentWorld.Value.DataCenter;
        var regionId = dc.ValueNullable?.Region.RowId ?? 0;
        return RegionNames.TryGetValue(regionId, out var name) ? name : string.Empty;
    }

    /// <summary>The player's current region id (1=Japan, 2=NA, 3=EU, 4=Oceania), or 0.</summary>
    ///
    /// Reached through the data-center NAME first. The previous version read
    /// Player.Object.CurrentWorld.Value.DataCenter.ValueNullable?.Region.RowId straight off the
    /// player object, which depends on a RowRef resolving at runtime — if that ever comes back
    /// null the id is 0 and a region scan quietly collapses. Scanning the sheet by name cannot go
    /// null, so the direct read is kept only as a last resort.
    public static uint CurrentRegionId()
    {
        var byName = RegionIdOfDataCenter(CurrentDataCenter());
        if (byName != 0) return byName;

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

    /// <summary>
    /// The data centers of the CURRENT region, straight from the client.
    ///
    /// UIModule keeps this list itself — it's what the game populates its own travel UI from —
    /// so it is authoritative, costs no guesswork, and tracks whatever Square Enix does to the
    /// region layout without a plugin update. Empty if the client hasn't populated it yet
    /// (early login), in which case the callers below fall back to the sheet's own grouping.
    ///
    /// MAIN-THREAD ONLY: reads UIModule.
    /// </summary>
    public static unsafe List<string> ClientRegionDataCenters()
    {
        var list = new List<string>();
        try
        {
            var helper = DataCenterHelper.Instance();
            if (helper == null) return list;

            for (var i = 0; i < helper->DataCenters.Count; i++)
            {
                var name = helper->DataCenters[i].NameString;
                if (!string.IsNullOrWhiteSpace(name) && !list.Contains(name, StringComparer.OrdinalIgnoreCase))
                    list.Add(name);
            }
        }
        catch
        {
            // Struct drift or an unpopulated helper — the sheet grouping covers us.
            return new List<string>();
        }
        return list;
    }

    /// <summary>
    /// Every data center that shares a region with the given one, including it.
    ///
    /// Two sources, in descending order of trust:
    ///   1. The client's own DataCenterHelper list — authoritative, and it tracks Square Enix
    ///      layout changes without a plugin update. Trusted only if it actually contains the DC
    ///      we are standing on, so a stale or other-region list can never hijack the result. The
    ///      list is then narrowed to data centers the World sheet actually uses, which is what
    ///      folds Japan's four client-visible DC names down to the one its worlds belong to.
    ///   2. The sheet's own grouping: WorldDCGroupType.Region.RowId, reached by DC name. Checked
    ///      against the installed sheets this is 1=Japan, 2=North America, 3=Europe, 4=Oceania.
    ///
    /// A note on Japan, because it looks wrong and isn't: the client still lists four Japanese
    /// data centers (Elemental, Gaia, Mana, Meteor), but the free-travel beta assigns every JP
    /// world to a single one of them — Elemental — so "my whole region" in Japan correctly
    /// resolves to all 32 JP worlds under one data center rather than four. Universalis's own
    /// /api/v2/data-centers listing agrees (it has no Gaia/Mana/Meteor, only Elemental with 32
    /// worlds). The static name table this used to carry stood in for a failure that isn't real.
    /// </summary>
    public static List<string> SiblingDataCenters(string dcName)
    {
        if (string.IsNullOrWhiteSpace(dcName)) return new List<string>();

        var known = new HashSet<string>(AllDataCenters(), StringComparer.OrdinalIgnoreCase);

        // 1. Ask the client. Only trust it if it actually contains the DC we're standing on —
        //    otherwise it's stale or describes a region we're not in.
        var fromClient = ClientRegionDataCenters().Where(known.Contains).ToList();
        if (fromClient.Contains(dcName, StringComparer.OrdinalIgnoreCase))
            return fromClient.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

        // 2. The sheet's own grouping, reached by DC name. This is the same grouping the client
        //    uses, and it is what the region selector has always relied on.
        var bySheet = DataCentersInRegion(RegionIdOfDataCenter(dcName));
        if (bySheet.Count > 0) return bySheet;

        return new List<string> { dcName };
    }

    /// <summary>
    /// Diagnostic: what this client's sheet actually says about every data center. Region ids
    /// here are the sheet's own, which is exactly the value that turned out not to mean what we
    /// thought — this is how to check it rather than guess.
    /// </summary>
    public static unsafe List<string> DumpDataCenters()
    {
        var lines = new List<string>();
        var here = CurrentDataCenter();
        lines.Add($"current world={CurrentWorld()} dc={here} region(name)={CurrentRegion()} region(sheetId)={CurrentRegionId()}");

        // HomeWorld vs CurrentWorld: region grouping reads one of these and they can differ while
        // world-visiting. Print both, plus the sheet's DC row id and region row id for each, so a
        // "same DC" assumption can be checked rather than relied on.
        if (Player.Available)
        {
            var cur = Player.Object.CurrentWorld;
            var home = Player.Object.HomeWorld;
            var curDc = cur.Value.DataCenter;
            var homeDc = home.Value.DataCenter;
            lines.Add($"world row: current={cur.RowId} home={home.RowId} same={cur.RowId == home.RowId}");
            lines.Add($"dc row: current={curDc.RowId} home={homeDc.RowId} same={curDc.RowId == homeDc.RowId}");
            lines.Add($"dc region row: current={curDc.ValueNullable?.Region.RowId ?? 0} home={homeDc.ValueNullable?.Region.RowId ?? 0}");
        }
        else
        {
            lines.Add("player not available");
        }

        var fromClient = ClientRegionDataCenters();
        lines.Add($"client DataCenterHelper ({fromClient.Count}): {(fromClient.Count == 0 ? "(empty - falling back to the sheet grouping)" : string.Join(", ", fromClient))}");

        // Raw helper entries. The client's own Region byte for each DC is the value region
        // resolution trusts first — if these do not group Japan's four DCs together, the helper
        // is not the region source we assumed and the sheet grouping is carrying it instead.
        try
        {
            var helper = DataCenterHelper.Instance();
            if (helper == null)
            {
                lines.Add("client helper: <null>");
            }
            else
            {
                for (var i = 0; i < helper->DataCenters.Count; i++)
                {
                    var e = helper->DataCenters[i];
                    lines.Add($"client[{i}] name={e.NameString} region={e.Region} dcGroup={e.WorldDCGroupType} index={e.Index}");
                }
            }
        }
        catch (Exception ex)
        {
            lines.Add($"client helper read failed: {ex.Message}");
        }

        foreach (var dc in AllDataCenters())
        {
            var worlds = WorldsOnDataCenter(dc);
            lines.Add($"dc={dc} sheetRegionId={RegionIdOfDataCenter(dc)} regionByName={RegionOfDataCenter(dc)} worlds={worlds.Count}");
        }

        lines.Add($"siblings of {here}: {string.Join(", ", SiblingDataCenters(here))}");
        return lines;
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
