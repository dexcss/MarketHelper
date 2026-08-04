using System;
using System.Collections.Generic;
using System.Numerics;
using ECommons.DalamudServices;
using Dalamud.Bindings.ImGui;
using static MarketHelper.UiScale;

namespace MarketHelper.Windows;

public partial class MainWindow
{
    private string _gathererSearch = string.Empty;

    private void DrawGathererTab()
    {
        WrapText("Pulls designated items off your retainers into your main inventory. Walks the bell, retrieves the items you list below, and stops when your bags are full. Empty your bags and run again to continue.");
        Dummy(6f);

        var fromInv = Cfg.GatherFromInventory;
        if (ImGui.Checkbox("Pull from retainer inventory", ref fromInv)) { Cfg.GatherFromInventory = fromInv; Cfg.Save(); }
        var fromMkt = Cfg.GatherFromMarket;
        if (ImGui.Checkbox("Pull from market listings (returns them first)", ref fromMkt)) { Cfg.GatherFromMarket = fromMkt; Cfg.Save(); }
        ImGui.SameLine(0, SW(6));
        HelpMarker("When on, designated items currently listed on the market are returned to the retainer's inventory, then retrieved into your bags. When off, only items already in the retainer's inventory are pulled.");

        Dummy(4f);
        // Quick-add + search-add, both target the gather list.
        var ctrl = ImGui.GetIO().KeyCtrl;
        ImGui.TextColored(ctrl ? Green : Grey, ctrl ? "CTRL held — hover an item to add it." : "Hold CTRL and hover an item to quick-add.");
        if (ctrl && Svc.GameGui.HoveredItem > 0)
        {
            var id = (uint)(Svc.GameGui.HoveredItem % 1000000);
            if (id > 0 && !Cfg.GathererItems.Contains(id))
            {
                var name = ItemSearch.FindByIdAny(id);
                if (!string.IsNullOrEmpty(name)) { Cfg.GathererItems.Add(id); Cfg.Save(); }
            }
        }

        ImGui.SetNextItemWidth(SW(260));
        ImGui.InputTextWithHint("##gathersearch", "...or search to add an item", ref _gathererSearch, 100);
        if (_gathererSearch.Trim().Length >= 2)
        {
            var hits = ItemSearch.FindAny(_gathererSearch);
            if (hits.Count > 0 && ImGui.BeginChild("##gatheradd", new Vector2(SW(260), SW(120)), true))
            {
                foreach (var h in hits)
                {
                    if (ImGui.Selectable($"{h.Name}##gadd{h.Id}"))
                    {
                        if (!Cfg.GathererItems.Contains(h.Id)) { Cfg.GathererItems.Add(h.Id); Cfg.Save(); }
                        _gathererSearch = string.Empty;
                    }
                }
                ImGui.EndChild();
            }
            else if (hits.Count == 0) { /* no matches */ }
        }

        Dummy(4f);
        ImGui.Separator();
        ImGui.Text($"Items to gather ({Cfg.GathererItems.Count}):");
        if (Cfg.GathererItems.Count == 0)
        {
            ImGui.TextColored(Grey, "  (empty — add items above)");
        }
        else
        {
            if (ImGui.BeginChild("##gatheritems", new Vector2(0, SW(160)), true))
            {
                uint? remove = null;
                foreach (var id in Cfg.GathererItems)
                {
                    var name = ItemSearch.FindByIdAny(id);
                    if (string.IsNullOrEmpty(name)) name = $"Item #{id}";
                    if (ImGui.SmallButton($"x##grm{id}")) remove = id;
                    ImGui.SameLine();
                    ImGui.Text(name);
                }
                if (remove.HasValue) { Cfg.GathererItems.Remove(remove.Value); Cfg.Save(); }
            }
            ImGui.EndChild();
            if (ImGui.Button("Clear list", new Vector2(SW(120), 0))) { Cfg.GathererItems.Clear(); Cfg.Save(); }
        }

        Dummy(6f);
        var free = RetainerReader.FreePlayerBagSlots();
        ImGui.TextColored(free > 0 || free < 0 ? Grey : Red, free < 0 ? "Free inventory slots: (open to check)" : $"Free inventory slots: {free}");

        Dummy(4f);
        var g = _plugin.Gatherer;
        if (g.Running)
        {
            if (ImGui.Button("Stop", new Vector2(SW(120), 0))) g.Stop();
            ImGui.SameLine(0, SW(8));
            ImGui.TextColored(Gold, g.Status);
        }
        else
        {
            using (ImRaiiDisabled(Cfg.GathererItems.Count == 0))
            {
                if (ImGui.Button("Run — gather items", new Vector2(SW(200), 0)))
                    g.Start();
            }
            ImGui.SameLine(0, SW(8));
            ImGui.TextColored(Grey, g.Status);
        }

        Dummy(6f);
        ImGui.Separator();
        ImGui.TextColored(Grey, "Manual actions (operate on the window you already have open):");
        Dummy(2f);
        var m = _plugin.Manual;
        using (ImRaiiDisabled(m.Running || Cfg.GathererItems.Count == 0))
        {
            if (ImGui.Button("Withdraw all (from open retainer)", new Vector2(SW(280), 0)))
                m.Start(ManualMode.WithdrawAll);
            HelpMarkerLine("Open a retainer's inventory yourself, then click to pull all your listed items from it into your bags.");

            if (ImGui.Button("Deposit all → open retainer", new Vector2(SW(280), 0)))
                m.Start(ManualMode.DepositRetainer);
            HelpMarkerLine("Open a retainer's inventory, then click to entrust all your listed items from your bags into it.");

            if (ImGui.Button("Deposit all → FC chest", new Vector2(SW(280), 0)))
                m.Start(ManualMode.DepositFc);
            HelpMarkerLine("Open your Free Company chest, then click to deposit all your listed items from your bags into it.");
        }
        if (m.Running)
        {
            ImGui.SameLine(0, SW(8));
            if (ImGui.Button("Stop##manual", new Vector2(SW(80), 0))) m.Stop();
        }
        if (!string.IsNullOrEmpty(m.Status) && m.Status != "Idle.")
            ImGui.TextColored(m.Running ? Gold : Grey, m.Status);
    }

    private static void HelpMarkerLine(string text)
    {
        ImGui.SameLine(0, SW(6));
        HelpMarker(text);
    }
}
