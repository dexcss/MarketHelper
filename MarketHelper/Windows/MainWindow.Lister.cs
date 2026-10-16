using System;
using Dalamud.Interface.Textures;
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

    /// <summary>
    /// Item icon for a row, falling back to blank space of the same size so the columns stay
    /// aligned when an icon can't be loaded.
    /// </summary>
    internal static void DrawItemIconStatic(uint itemId, float size) => DrawItemIcon(itemId, size);

    private static void DrawItemIcon(uint itemId, float size)
    {
        var icon = ItemSearch.IconId(itemId);
        if (icon != 0)
        {
            try
            {
                var tex = Svc.Texture.GetFromGameIcon(new GameIconLookup(icon)).GetWrapOrEmpty();
                ImGui.Image(tex.Handle, new Vector2(size, size));
                return;
            }
            catch { /* fall through to the spacer */ }
        }
        ImGui.Dummy(new Vector2(size, size));
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
            if (ImGui.BeginChild($"##items{id}", new Vector2(0, SW(170)), true))
            {
                uint? remove = null;

                var tflags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingFixedFit;
                if (ImGui.BeginTable($"##listtable{id}", 5, tflags))
                {
                    ImGui.TableSetupColumn("##icon", ImGuiTableColumnFlags.WidthFixed, SW(26));
                    ImGui.TableSetupColumn("Listing", ImGuiTableColumnFlags.WidthFixed, SW(118));
                    ImGui.TableSetupColumn("Stack size", ImGuiTableColumnFlags.WidthFixed, SW(76));
                    ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 1f);
                    ImGui.TableSetupColumn("##del", ImGuiTableColumnFlags.WidthFixed, SW(24));
                    ImGui.TableHeadersRow();

                    foreach (var itemId in list)
                    {
                        var name = ItemSearch.FindById(itemId);
                        if (string.IsNullOrEmpty(name)) name = $"Item #{itemId}";

                        // Units held right now. Retainer pages only count while a retainer is open.
                        var held = RetainerReader.CountItem(itemId, includeRetainer: true);

                        ImGui.TableNextRow();

                        ImGui.TableNextColumn();
                        DrawItemIcon(itemId, SW(22));

                        // How many to list, then "/ held" so you can see what you have.
                        ImGui.TableNextColumn();
                        var amount = Cfg.ListerListAmounts.TryGetValue(itemId, out var a2) && a2 > 0 ? a2 : held;
                        ImGui.SetNextItemWidth(SW(58));
                        // Step 0 drops ImGui's +/- buttons — typing is quicker and the row is tighter.
                        if (ImGui.InputInt($"##{id}amt{itemId}", ref amount, 0))
                        {
                            amount = Math.Clamp(amount, 0, 99999);
                            // "All of them" is stored as absent so it stays right when the count changes.
                            if (amount <= 0 || amount >= held) Cfg.ListerListAmounts.Remove(itemId);
                            else Cfg.ListerListAmounts[itemId] = amount;
                            Cfg.Save();
                        }
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip("How many units to list in total.\nDefaults to everything you hold; lower it to keep some back.");
                        ImGui.SameLine(0, SW(4));
                        ImGui.TextColored(held > 0 ? Grey : Gold, $"/ {held:N0}");

                        // Units per listing. 0 = the whole stack as one listing (game default).
                        ImGui.TableNextColumn();
                        var per = Cfg.ListerStackSizes.TryGetValue(itemId, out var v) ? v : 0;
                        ImGui.SetNextItemWidth(SW(58));
                        if (ImGui.InputInt($"##{id}qty{itemId}", ref per, 0))
                        {
                            per = Math.Clamp(per, 0, 9999);
                            if (per <= 0) Cfg.ListerStackSizes.Remove(itemId);
                            else Cfg.ListerStackSizes[itemId] = per;
                            Cfg.Save();
                        }
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip("Units in each listing.\n0 = the whole stack as one listing (default).\n1 = one listing per unit, so 18 dye becomes 18 listings.\nEach listing takes one of the retainer's 20 slots.");

                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(name);

                        ImGui.TableNextColumn();
                        if (ImGui.SmallButton($"x##{id}rm{itemId}")) remove = itemId;
                    }
                    ImGui.EndTable();
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
