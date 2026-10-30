using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using MarketHelper.FancyUI;
using static MarketHelper.UiScale;

namespace MarketHelper.Windows;

public partial class MainWindow
{
    private string _retainerPullStatus = string.Empty;

    private void DrawRetainersTab()
    {
        var auto = Cfg.AutoDetectMyRetainers;
        if (ImGui.Checkbox("Auto-detect my retainers on run", ref auto))
        { Cfg.AutoDetectMyRetainers = auto; Cfg.Save(); }
        ImGui.SameLine(0, SW(6));
        HelpMarker("Pulls your retainer names from the game's market info proxy when you press Run.");

        WrapText("Listings from these retainers are matched, not undercut:");
        Dummy(2f);

        if (Cfg.MyRetainers.Count == 0)
            ImGui.TextColored(Grey, "  (none yet — they appear here after a run, or pull them below)");

        string? remove = null;
        foreach (var n in Cfg.MyRetainers)
        {
            ImGui.TextUnformatted($"• {n}");
            ImGui.SameLine(0, SW(6));
            if (ImGui.SmallButton($"remove##{n}")) remove = n;
        }
        if (remove != null) { Cfg.MyRetainers.Remove(remove); Cfg.Save(); }

        Dummy(4f);

        // Manual pull: the auto-detect only runs during a run/reactive pricing, so before the
        // first run this list starts empty even though the game already knows the retainer names
        // (they're read from the market info proxy, which populates after any market board use).
        if (UiKit.ChipButton("##fancy-retpull", "PULL MY RETAINERS NOW", false, ImGui.GetContentRegionAvail().X))
        {
            var names = MarketData.GetMyRetainerNames();
            var added = 0;
            foreach (var n in names)
            {
                var norm = PricingLogic.Normalize(n);
                if (norm.Length == 0 || Cfg.MyRetainers.Contains(norm)) continue;
                Cfg.MyRetainers.Add(norm);
                added++;
            }
            if (added > 0) Cfg.Save();
            ImGui.OpenPopup("##retpullstatus");
            _retainerPullStatus = names.Count == 0
                ? "The market proxy has no names yet — open a retainer's sell window or a market board, then try again."
                : $"{added} retainer(s) added ({names.Count} seen).";
        }
        if (ImGui.BeginPopup("##retpullstatus"))
        {
            ImGui.TextWrapped(_retainerPullStatus);
            ImGui.EndPopup();
        }
        ImGui.SameLine(0, SW(6));
        HelpMarker("Reads your retainer names from the game's market info proxy. The proxy fills once you've opened a retainer's sell window or a market board this session — do that first if nothing is added.");

        Dummy(4f);
        if (ImGui.Button("Clear list", new Vector2(SW(120), 0))) { Cfg.MyRetainers.Clear(); Cfg.Save(); }
    }
}
