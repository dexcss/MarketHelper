using System.Numerics;
using Dalamud.Bindings.ImGui;
using static MarketHelper.UiScale;

namespace MarketHelper.Windows;

public partial class MainWindow
{
    // override editor scratch fields
    private string _ovName = string.Empty;
    private int _ovMin, _ovMax, _ovDefault;
    private bool _useMin, _useMax, _useDefault;

    private void DrawOverridesTab()
    {
        WrapText("Per-item price overrides. Item name is matched loosely (case/space/symbol insensitive).");
        Dummy(2f);

        ImGui.SetNextItemWidth(SW(280));
        ImGui.InputText("Item name", ref _ovName, 128);

        ImGui.Checkbox("Min", ref _useMin); ImGui.SameLine(0, SW(6));
        ImGui.SetNextItemWidth(SW(120)); ImGui.InputInt("##min", ref _ovMin);
        ImGui.Checkbox("Max", ref _useMax); ImGui.SameLine(0, SW(6));
        ImGui.SetNextItemWidth(SW(120)); ImGui.InputInt("##max", ref _ovMax);
        ImGui.Checkbox("Default", ref _useDefault); ImGui.SameLine(0, SW(6));
        ImGui.SetNextItemWidth(SW(120)); ImGui.InputInt("##def", ref _ovDefault);
        ImGui.SameLine(0, SW(6));
        HelpMarker("Default is used only when there are no active listings.");

        if (ImGui.Button("Add / Update override", new Vector2(SW(180), 0)) && !string.IsNullOrWhiteSpace(_ovName))
        {
            var key = PricingLogic.Normalize(_ovName);
            Cfg.ItemOverrides[key] = new ItemOverride
            {
                Minimum = _useMin ? _ovMin : (int?)null,
                Maximum = _useMax ? _ovMax : (int?)null,
                Default = _useDefault ? _ovDefault : (int?)null,
            };
            Cfg.Save();
            _ovName = string.Empty; _ovMin = _ovMax = _ovDefault = 0;
            _useMin = _useMax = _useDefault = false;
        }

        Divider();

        if (ImGui.BeginTable("ovtable", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 2.0f);
            ImGui.TableSetupColumn("Min", ImGuiTableColumnFlags.WidthFixed, SW(70));
            ImGui.TableSetupColumn("Max", ImGuiTableColumnFlags.WidthFixed, SW(70));
            ImGui.TableSetupColumn("Default", ImGuiTableColumnFlags.WidthFixed, SW(70));
            ImGui.TableSetupColumn("##del", ImGuiTableColumnFlags.WidthFixed, SW(28));
            ImGui.TableHeadersRow();

            string? toRemove = null;
            foreach (var kv in Cfg.ItemOverrides)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.TextUnformatted(kv.Key);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(kv.Value.Minimum?.ToString() ?? "-");
                ImGui.TableNextColumn(); ImGui.TextUnformatted(kv.Value.Maximum?.ToString() ?? "-");
                ImGui.TableNextColumn(); ImGui.TextUnformatted(kv.Value.Default?.ToString() ?? "-");
                ImGui.TableNextColumn();
                if (ImGui.SmallButton($"X##{kv.Key}")) toRemove = kv.Key;
            }
            if (toRemove != null) { Cfg.ItemOverrides.Remove(toRemove); Cfg.Save(); }
            ImGui.EndTable();
        }
    }
}
