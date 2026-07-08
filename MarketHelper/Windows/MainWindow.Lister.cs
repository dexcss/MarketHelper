using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using ECommons.DalamudServices;
using MarketHelper;
using static MarketHelper.UiScale;

namespace MarketHelper.Windows;

public partial class MainWindow
{
    private string _listerSearch = string.Empty;
    private bool _addToSession = true;   // CTRL-add / search-add target: session (true) or permanent

    private List<uint> AddTarget => _addToSession ? _plugin.SessionListerItems : Cfg.ListerItems;
    private void AddListerItem(uint id)
    {
        // Don't add if it's already in EITHER list — an item should live in only one.
        if (_plugin.SessionListerItems.Contains(id) || Cfg.ListerItems.Contains(id)) return;
        AddTarget.Add(id);
        if (!_addToSession) Cfg.Save();   // session list isn't persisted
    }

    private void DrawListerTab()
    {
        WrapText("Build a list of items to auto-list on the market. Hold CTRL and hover an item to quick-add it, or search below. Two lists: a permanent preset and a session list that clears when you reload. Press Go to walk the bell and list all of them.");
        Dummy(4f);

        // Which list do adds go to?
        ImGui.Text("Add to:");
        ImGui.SameLine();
        var toSession = _addToSession;
        if (ImGui.RadioButton("Session", toSession)) _addToSession = true;
        ImGui.SameLine();
        if (ImGui.RadioButton("Permanent", !toSession)) _addToSession = false;
        ImGui.SameLine(0, SW(6));
        HelpMarker("Session items clear when the plugin reloads or the game closes (or via 'Clear session'). Permanent items are saved and persist. Both are listed when you press Go.");

        // --- CTRL quick-add (AutoRetainer's HoveredItem pattern) ---
        var ctrl = ImGui.GetIO().KeyCtrl;
        ImGui.TextColored(ctrl ? Green : Grey, ctrl
            ? $"CTRL held — hover an item to add it to the {(_addToSession ? "session" : "permanent")} list."
            : "Hold CTRL and hover an item to quick-add.");
        if (ctrl && Svc.GameGui.HoveredItem > 0)
        {
            var id = (uint)(Svc.GameGui.HoveredItem % 1000000);
            if (id > 0)
            {
                // Only add marketable items; AddListerItem rejects duplicates across both lists.
                var hits = ItemSearch.FindById(id);
                if (!string.IsNullOrEmpty(hits))
                    AddListerItem(id);
            }
        }

        // --- Manual search-add ---
        ImGui.SetNextItemWidth(SW(260));
        ImGui.InputTextWithHint("##listersearch", "...or search to add an item", ref _listerSearch, 100);
        if (_listerSearch.Trim().Length >= 2)
        {
            var hits = ItemSearch.Find(_listerSearch);
            if (hits.Count > 0)
            {
                if (ImGui.BeginChild("##listeradd", new Vector2(SW(260), SW(120)), true))
                {
                    foreach (var h in hits)
                    {
                        if (ImGui.Selectable($"{h.Name}##add{h.Id}"))
                        {
                            AddListerItem(h.Id);
                            _listerSearch = string.Empty;
                        }
                    }
                }
                ImGui.EndChild();
            }
        }

        Dummy(4f);
        ImGui.Separator();

        // --- Pricing options ---
        var detectedWorld = WorldInfo.CurrentWorld();
        var detectedDc = WorldInfo.CurrentDataCenter();
        var detectedRegion = WorldInfo.CurrentRegion();
        ImGui.Text("Pricing target:");
        ImGui.SameLine();
        ImGui.TextColored(Grey, string.IsNullOrEmpty(detectedWorld)
            ? "(location unknown — set below)"
            : $"{detectedWorld} / {detectedDc} / {detectedRegion}");

        ImGui.Text("Undercut the lowest on:");
        var scope = Cfg.ListerPriceScope;
        if (ImGui.RadioButton("My world", ref scope, 0)) { Cfg.ListerPriceScope = 0; Cfg.Save(); }
        ImGui.SameLine();
        if (ImGui.RadioButton("My Data Center", ref scope, 1)) { Cfg.ListerPriceScope = 1; Cfg.Save(); }
        ImGui.SameLine();
        if (ImGui.RadioButton("My whole Region", ref scope, 2)) { Cfg.ListerPriceScope = 2; Cfg.Save(); }
        ImGui.SameLine(0, SW(6));
        HelpMarker("Price to beat the cheapest listing at this scope (via Universalis). Region = e.g. all of North-America. Wider scope means a lower target price.");

        ImGui.SetNextItemWidth(SW(120));
        var undercut = Cfg.ListerUndercutBy;
        if (ImGui.InputInt("Undercut by (gil)", ref undercut, 1)) { Cfg.ListerUndercutBy = Math.Max(0, undercut); Cfg.Save(); }
        ImGui.SameLine(0, SW(6));
        HelpMarker("Prices at the most common listing price (the real 'wall'), then subtracts this many gil. Lone cheap outliers (trolls / stale listings) are ignored automatically.");

        // Manual overrides if auto-detect failed.
        if (string.IsNullOrEmpty(detectedWorld))
        {
            ImGui.TextColored(Gold, "Couldn't auto-detect your world. Pick manually:");
            DrawDcWorldSelectors();
        }

        Dummy(4f);
        ImGui.Separator();

        // --- Session list ---
        DrawItemList("Session list", _plugin.SessionListerItems, "sess", isSession: true);
        // --- Permanent list ---
        DrawItemList("Permanent list", Cfg.ListerItems, "perm", isSession: false);

        Dummy(6f);
        var lr = _plugin.Lister;
        var totalItems = _plugin.AllListerItems().Count;
        if (lr.Running)
        {
            if (ImGui.Button("Stop", new Vector2(SW(120), 0))) lr.Stop();
            ImGui.SameLine(0, SW(8));
            ImGui.TextColored(Gold, lr.Status);
        }
        else
        {
            using (ImRaiiDisabled(totalItems == 0))
            {
                if (ImGui.Button("Dry run (preview only)", new Vector2(SW(200), 0)))
                    lr.Start(dryRun: true);
                ImGui.SameLine(0, SW(8));
                if (ImGui.Button("Go — list for real", new Vector2(SW(160), 0)))
                    lr.Start(dryRun: false);
            }
            if (!string.IsNullOrEmpty(lr.Status) && lr.Status != "Idle.")
            {
                ImGui.SameLine(0, SW(8));
                ImGui.TextColored(Grey, lr.Status);
            }
        }
        ImGui.TextColored(Grey, "Dry run walks the bell and logs what it WOULD list (no market changes). Run it first to confirm items and prices look right.");

        // Report
        if (lr.Report.Count > 0)
        {
            Dummy(4f);
            if (ImGui.BeginChild("##listreport", new Vector2(0, SW(140)), true))
            {
                foreach (var line in lr.Report)
                    ImGui.TextWrapped(line);
            }
            ImGui.EndChild();
        }
    }

    private void DrawDcWorldSelectors()
    {
        var dcs = WorldInfo.AllDataCenters();
        ImGui.SetNextItemWidth(SW(160));
        if (ImGui.BeginCombo("Data Center", string.IsNullOrEmpty(Cfg.ListerDcOverride) ? "Select..." : Cfg.ListerDcOverride))
        {
            foreach (var dc in dcs)
                if (ImGui.Selectable(dc, dc == Cfg.ListerDcOverride)) { Cfg.ListerDcOverride = dc; Cfg.ListerWorldOverride = ""; Cfg.Save(); }
            ImGui.EndCombo();
        }
        if (!string.IsNullOrEmpty(Cfg.ListerDcOverride))
        {
            var worlds = WorldInfo.WorldsOnDataCenter(Cfg.ListerDcOverride);
            ImGui.SetNextItemWidth(SW(160));
            if (ImGui.BeginCombo("World", string.IsNullOrEmpty(Cfg.ListerWorldOverride) ? "Select..." : Cfg.ListerWorldOverride))
            {
                foreach (var w in worlds)
                    if (ImGui.Selectable(w, w == Cfg.ListerWorldOverride)) { Cfg.ListerWorldOverride = w; Cfg.Save(); }
                ImGui.EndCombo();
            }
        }
    }

    private void DrawItemList(string title, List<uint> list, string id, bool isSession)
    {
        ImGui.Text($"{title} ({list.Count}):");
        if (list.Count == 0)
        {
            ImGui.TextColored(Grey, "  (empty)");
        }
        else
        {
            if (ImGui.BeginChild($"##items{id}", new Vector2(0, SW(130)), true))
            {
                uint? remove = null;
                foreach (var itemId in list)
                {
                    var name = ItemSearch.FindById(itemId);
                    if (string.IsNullOrEmpty(name)) name = $"Item #{itemId}";
                    if (ImGui.SmallButton($"x##{id}rm{itemId}")) remove = itemId;
                    ImGui.SameLine();
                    ImGui.Text(name);
                }
                if (remove.HasValue) { list.Remove(remove.Value); if (!isSession) Cfg.Save(); }
            }
            ImGui.EndChild();

            var clearLabel = isSession ? "Clear session" : "Clear permanent";
            if (ImGui.Button($"{clearLabel}##{id}", new Vector2(SW(140), 0)))
            { list.Clear(); if (!isSession) Cfg.Save(); }
        }
        Dummy(4f);
    }
}
