using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons;
using ECommons.DalamudServices;
using ECommons.GameFunctions;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using Lumina.Excel.Sheets;

namespace MarketHelper;

/// <summary>
/// Summoning-bell discovery and interaction. Ported from AutoRetainer's verified helpers
/// (Utils.GetNearestRetainerBell / IsRetainerBell / TargetSystem InteractWithObject).
///
/// A bell is an EventObj / HousingEventObject named after EObjName row 2000401. One bell
/// exposes all of the character's retainers.
/// </summary>
public static unsafe class Bell
{
    // EObjName row 2000401 = "Summoning Bell". Cached English + JP fallback like AR does.
    private static string[]? _names;
    private static string[] Names => _names ??= new[]
    {
        Svc.Data.GetExcelSheet<EObjName>()?.GetRow(2000401).Singular.GetText() ?? "Summoning Bell",
        "リテイナーベル",
    };

    public static bool IsBell(IGameObject? o) =>
        o != null
        && (o.ObjectKind == ObjectKind.EventObj || o.ObjectKind == ObjectKind.HousingEventObject)
        && Names.Any(n => string.Equals(o.Name.ToString(), n, System.StringComparison.OrdinalIgnoreCase));

    public static IGameObject? GetNearest(out float distance)
    {
        distance = float.MaxValue;
        IGameObject? nearest = null;
        if (!Player.Available) return null;
        var mePos = Player.Object.Position;

        foreach (var o in Svc.Objects)
        {
            if (!o.IsTargetable) continue;
            if (!IsBell(o)) continue;
            var d = Vector3.Distance(mePos, o.Position);
            if (d < distance) { distance = d; nearest = o; }
        }
        return nearest;
    }

    /// <summary>Interact with the given bell object. Returns false if the object is gone.</summary>
    public static bool Interact(IGameObject bell)
    {
        if (bell == null) return false;
        TargetSystem.Instance()->InteractWithObject(bell.Struct(), false);
        return true;
    }
}
