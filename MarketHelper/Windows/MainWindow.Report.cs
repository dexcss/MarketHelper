using System.Numerics;
using Dalamud.Bindings.ImGui;
using static MarketHelper.UiScale;

namespace MarketHelper.Windows;

public partial class MainWindow
{
    private void DrawReportTab()
    {
        var report = _plugin.Listener.Report;
        if (report.Count == 0)
        {
            ImGui.TextDisabled("No run yet.");
            return;
        }
        ImGui.Text($"{report.Count} line(s):");
        ImGui.BeginChild("report", new Vector2(0, 0), true);
        foreach (var line in report)
            WrapText(line);
        ImGui.EndChild();
    }
}
