using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Interface;
using ECommons.DalamudServices;
using Dalamud.Bindings.ImGui;
using MarketHelper.FancyUI;
using static MarketHelper.UiScale;

namespace MarketHelper.Windows;

public partial class MainWindow
{
    private string _gathererSearch = string.Empty;

    private void DrawGathererTab()
    {
        WrapText("Pulls designated items off your retainers into your main inventory. Walks the bell, retrieves the items you list below, and stops when your bags are full. Empty your bags and run again to continue.");
        Dummy(6f);

        UiKit.Section(FontAwesomeIcon.Download, "what to pull");
        var fromInv = UiKit.Toggle("##fancy-ginv", Cfg.GatherFromInventory);
        ImGui.SameLine(0, Theme.Gap);
        ImGui.TextUnformatted("Pull from retainer inventory");
        if (fromInv != Cfg.GatherFromInventory) { Cfg.GatherFromInventory = fromInv; Cfg.Save(); }

        var fromMkt = UiKit.Toggle("##fancy-gmkt", Cfg.GatherFromMarket);
        ImGui.SameLine(0, Theme.Gap);
        ImGui.TextUnformatted("Pull from market listings (returns them first)");
        ImGui.SameLine(0, SW(6));
        HelpMarker("When on, designated items currently listed on the market are returned to the retainer's inventory, then retrieved into your bags. When off, only items already in the retainer's inventory are pulled.");
        if (fromMkt != Cfg.GatherFromMarket) { Cfg.GatherFromMarket = fromMkt; Cfg.Save(); }

        UiKit.Section(FontAwesomeIcon.BoxOpen, "items to gather");
        // Quick-add + search-add, both target the gather list.
        var ctrl = ImGui.GetIO().KeyCtrl;
        ImGui.TextColored(ctrl ? Green : Grey, ctrl ? "CTRL held — hover an item to add it." : "Hold CTRL and hover an item to quick-add.");
        if (ctrl && Svc.GameGui.HoveredItem > 0)
        {
            var id = Dalamud.Utility.ItemUtil.GetBaseId((uint)Svc.GameGui.HoveredItem).ItemId;
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
            if (hits.Count > 0)
            {
                // EndChild is called unconditionally: BeginChild returning false (the child is fully
                // clipped) still requires it, or ImGui's window stack is left unbalanced.
                if (ImGui.BeginChild("##gatheradd", new Vector2(SW(260), SW(120)), true))
                {
                    foreach (var h in hits)
                    {
                        if (ImGui.Selectable($"{h.Name}##gadd{h.Id}"))
                        {
                            if (!Cfg.GathererItems.Contains(h.Id)) { Cfg.GathererItems.Add(h.Id); Cfg.Save(); }
                            _gathererSearch = string.Empty;
                        }
                    }
                }
                ImGui.EndChild();
            }
        }

        UiKit.Section(FontAwesomeIcon.BoxOpen, $"items to gather ({Cfg.GathererItems.Count})");
        if (Cfg.GathererItems.Count == 0)
        {
            ImGui.TextColored(Grey, "  (empty — add items above)");
        }
        else
        {
            if (ImGui.BeginChild("##gatheritems", new Vector2(0, SW(160)), true))
            {
                uint? remove = null;

                // Same table treatment as the Lister's lists: icon, name, remove — so both tabs
                // read the same way.
                var tflags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingFixedFit;
                if (ImGui.BeginTable("##gathertable", 3, tflags))
                {
                    ImGui.TableSetupColumn("##gicon", ImGuiTableColumnFlags.WidthFixed, SW(26));
                    ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 1f);
                    ImGui.TableSetupColumn("##gdel", ImGuiTableColumnFlags.WidthFixed, SW(24));
                    ImGui.TableHeadersRow();

                    foreach (var id in Cfg.GathererItems)
                    {
                        var name = ItemSearch.FindByIdAny(id);
                        if (string.IsNullOrEmpty(name)) name = $"Item #{id}";

                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        MainWindow.DrawItemIconStatic(id, SW(22));
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(name);
                        ImGui.TableNextColumn();
                        if (ImGui.SmallButton($"x##grm{id}")) remove = id;
                    }
                    ImGui.EndTable();
                }

                if (remove.HasValue) { Cfg.GathererItems.Remove(remove.Value); Cfg.Save(); }
            }
            ImGui.EndChild();
            if (ImGui.Button("Clear list", new Vector2(SW(120), 0))) { Cfg.GathererItems.Clear(); Cfg.Save(); }
        }

        UiKit.Section(FontAwesomeIcon.Play, "run");
        var free = RetainerReader.FreePlayerBagSlots();
        ImGui.TextColored(free > 0 || free < 0 ? Grey : Red, free < 0 ? "Free inventory slots: (open to check)" : $"Free inventory slots: {free}");

        Dummy(4f);
        var g = _plugin.Gatherer;
        if (g.Running)
        {
            if (UiKit.GradientButton("##fancy-gstop", "STOP", Theme.Danger, Theme.Danger, 36f)) g.Stop();
            ImGui.SameLine(0, Theme.Gap);
            ImGui.TextColored(Gold, g.Status);
        }
        else
        {
            using (ImRaiiDisabled(Cfg.GathererItems.Count == 0))
            {
                if (UiKit.GradientButton("##fancy-grun", "RUN — GATHER ITEMS", Theme.Accent, Theme.Good, 40f))
                    g.Start();
            }
            ImGui.SameLine(0, Theme.Gap);
            ImGui.TextColored(Grey, g.Status);
        }

        UiKit.Section(FontAwesomeIcon.HandHolding, "manual actions");
        WrapText(Grey, "Operate on the window you already have open:");
        Dummy(2);
        var m = _plugin.Manual;
        using (ImRaiiDisabled(m.Running || Cfg.GathererItems.Count == 0))
        {
            if (UiKit.ChipButton("##fancy-gwd", "WITHDRAW ALL (FROM OPEN RETAINER)", false, ImGui.GetContentRegionAvail().X))
                m.Start(ManualMode.WithdrawAll);
            HelpMarkerLine("Open a retainer's inventory yourself, then click to pull all your listed items from it into your bags.");

            if (UiKit.ChipButton("##fancy-gdp", "DEPOSIT ALL → OPEN RETAINER", false, ImGui.GetContentRegionAvail().X))
                m.Start(ManualMode.DepositRetainer);
            HelpMarkerLine("Open a retainer's inventory, then click to entrust all your listed items from your bags into it.");
        }
        if (m.Running)
        {
            ImGui.SameLine(0, Theme.Gap);
            if (UiKit.ChipButton("##fancy-gmstop", "STOP", false)) m.Stop();
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
