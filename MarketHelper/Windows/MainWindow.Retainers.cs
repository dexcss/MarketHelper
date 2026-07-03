using System.Numerics;
using Dalamud.Bindings.ImGui;
using static MarketHelper.UiScale;

namespace MarketHelper.Windows;

public partial class MainWindow
{
    private void DrawRetainersTab()
    {
        var auto = Cfg.AutoDetectMyRetainers;
        if (ImGui.Checkbox("Auto-detect my retainers on run", ref auto))
        { Cfg.AutoDetectMyRetainers = auto; Cfg.Save(); }
        ImGui.SameLine(0, SW(6));
        HelpMarker("Pulls your retainer names from the game's market info proxy when you press Run.");

        WrapText("Listings from these retainers are matched, not undercut:");
        Dummy(2f);

        string? remove = null;
        foreach (var n in Cfg.MyRetainers)
        {
            ImGui.TextUnformatted($"• {n}");
            ImGui.SameLine(0, SW(6));
            if (ImGui.SmallButton($"remove##{n}")) remove = n;
        }
        if (remove != null) { Cfg.MyRetainers.Remove(remove); Cfg.Save(); }

        Dummy(4f);
        if (ImGui.Button("Clear list", new Vector2(SW(120), 0))) { Cfg.MyRetainers.Clear(); Cfg.Save(); }
    }
}
