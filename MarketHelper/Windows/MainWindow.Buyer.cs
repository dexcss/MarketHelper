using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ECommons.DalamudServices;
using Dalamud.Bindings.ImGui;
using static MarketHelper.UiScale;

namespace MarketHelper.Windows;

public partial class MainWindow
{
    private string _buyerSearch = string.Empty;
    private int _buyerAddQty = 1;
    private int _buyerAddMax = 1000;

    private void DrawBuyerTab()
    {
        WrapText("Build a shopping list, press SCAN to find the cheapest listings in your chosen scope, then press SEND to go and buy them. Nothing is ever bought above the max price you set for that item.");
        Dummy(6f);

        DrawBuyerAddRow();
        Divider();
        DrawMakePlaceImport();
        Divider();
        DrawBuyerList();
        Divider();
        DrawBuyerScope();
        Divider();
        DrawBuyerActions();
        Divider();
        DrawBuyerSettings();
    }

    // ---- shopping list ------------------------------------------------------------------------

    private void DrawBuyerAddRow()
    {
        var ctrl = ImGui.GetIO().KeyCtrl;
        ImGui.TextColored(ctrl ? Green : Grey, ctrl ? "CTRL held — hover an item to add it." : "Hold CTRL and hover an item to quick-add.");
        if (ctrl && Svc.GameGui.HoveredItem > 0)
        {
            var id = (uint)(Svc.GameGui.HoveredItem % 1000000);
            if (id > 0 && Cfg.BuyerItems.All(i => i.ItemId != id))
            {
                var nm = ItemSearch.FindById(id);
                if (!string.IsNullOrEmpty(nm)) AddBuyerItem(id);
            }
        }

        ImGui.SetNextItemWidth(SW(80));
        if (ImGui.InputInt("Qty##buyqty", ref _buyerAddQty, 1)) _buyerAddQty = Math.Max(1, _buyerAddQty);
        ImGui.SameLine(0, SW(10));

        var newCap = Cfg.BuyerNewItemUseMaxPrice;
        if (ImGui.Checkbox("Cap price##buynewcap", ref newCap)) { Cfg.BuyerNewItemUseMaxPrice = newCap; Cfg.Save(); }
        ImGui.SameLine(0, SW(6));
        using (ImRaiiDisabled(!Cfg.BuyerNewItemUseMaxPrice))
        {
            ImGui.SetNextItemWidth(SW(120));
            if (ImGui.InputInt("Max each##buymax", ref _buyerAddMax, 100)) _buyerAddMax = Math.Max(1, _buyerAddMax);
        }
        ImGui.SameLine(0, SW(6));
        HelpMarker("Off by default: the Buyer just takes the cheapest of whatever you asked for. Tick this to refuse anything above a price per unit — the cap can be set per row afterwards.");

        ImGui.SetNextItemWidth(SW(260));
        ImGui.InputTextWithHint("##buysearch", "search an item to add", ref _buyerSearch, 100);
        if (_buyerSearch.Trim().Length >= 2)
        {
            var hits = ItemSearch.Find(_buyerSearch);
            if (hits.Count > 0)
            {
                // EndChild is called unconditionally — BeginChild returning false still requires it.
                if (ImGui.BeginChild("##buyadd", new Vector2(SW(260), SW(120)), true))
                {
                    foreach (var h in hits)
                    {
                        if (ImGui.Selectable($"{h.Name}##badd{h.Id}"))
                        {
                            AddBuyerItem(h.Id);
                            _buyerSearch = string.Empty;
                        }
                    }
                }
                ImGui.EndChild();
            }
        }
    }

    private void AddBuyerItem(uint id)
    {
        if (Cfg.BuyerItems.Any(i => i.ItemId == id)) return;
        Cfg.BuyerItems.Add(new BuyerItem
        {
            ItemId = id,
            Quantity = Math.Max(1, _buyerAddQty),
            UseMaxPrice = Cfg.BuyerNewItemUseMaxPrice,
            MaxPrice = Math.Max(1, _buyerAddMax),
        });
        Cfg.Save();
    }

    private void DrawBuyerList()
    {
        ImGui.Text($"Shopping list ({Cfg.BuyerItems.Count}):");
        if (Cfg.BuyerItems.Count == 0)
        {
            ImGui.TextColored(Grey, "  (empty — add items above)");
            DrawUnbuyableReport();   // an import can resolve nothing and still have plenty to report
            return;
        }

        if (ImGui.BeginTable("##buyertable", 7, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("On", ImGuiTableColumnFlags.WidthFixed, SW(30));
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 2.0f);
            ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, SW(70));
            ImGui.TableSetupColumn("Cap", ImGuiTableColumnFlags.WidthFixed, SW(34));
            ImGui.TableSetupColumn("Max each", ImGuiTableColumnFlags.WidthFixed, SW(100));
            ImGui.TableSetupColumn("HQ", ImGuiTableColumnFlags.WidthFixed, SW(34));
            ImGui.TableSetupColumn("##del", ImGuiTableColumnFlags.WidthFixed, SW(28));
            ImGui.TableHeadersRow();

            uint? remove = null;
            foreach (var row in Cfg.BuyerItems)
            {
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                var on = row.Enabled;
                if (ImGui.Checkbox($"##on{row.ItemId}", ref on)) { row.Enabled = on; Cfg.Save(); }

                ImGui.TableNextColumn();
                var name = ItemSearch.FindById(row.ItemId);
                if (string.IsNullOrEmpty(name)) name = $"Item #{row.ItemId}";
                ImGui.TextUnformatted(name);

                ImGui.TableNextColumn();
                var qty = row.Quantity;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputInt($"##q{row.ItemId}", ref qty, 0)) { row.Quantity = Math.Max(1, qty); Cfg.Save(); }

                ImGui.TableNextColumn();
                var useCap = row.UseMaxPrice;
                if (ImGui.Checkbox($"##cap{row.ItemId}", ref useCap)) { row.UseMaxPrice = useCap; Cfg.Save(); }

                ImGui.TableNextColumn();
                using (ImRaiiDisabled(!row.UseMaxPrice))
                {
                    var max = (int)Math.Min(row.MaxPrice, int.MaxValue);
                    ImGui.SetNextItemWidth(-1);
                    if (ImGui.InputInt($"##m{row.ItemId}", ref max, 0)) { row.MaxPrice = Math.Max(1, max); Cfg.Save(); }
                }

                ImGui.TableNextColumn();
                var hq = row.HqOnly;
                if (ImGui.Checkbox($"##hq{row.ItemId}", ref hq)) { row.HqOnly = hq; Cfg.Save(); }

                ImGui.TableNextColumn();
                if (ImGui.SmallButton($"x##brm{row.ItemId}")) remove = row.ItemId;
            }
            ImGui.EndTable();

            if (remove.HasValue)
            {
                Cfg.BuyerItems.RemoveAll(i => i.ItemId == remove.Value);
                Cfg.Save();
            }
        }

        Dummy(2f);
        if (ImGui.Button("Clear list##buyclear", new Vector2(SW(120), 0)))
        {
            Cfg.BuyerItems.Clear();
            Cfg.BuyerUnbuyable.Clear();
            Cfg.Save();
            _plugin.Buyer.ClearPlan();
        }

        DrawUnbuyableReport();
        DrawUncappedWarning();
    }

    // ---- MakePlace import ----------------------------------------------------------------------

    private string _mpPaste = string.Empty;
    private string _mpLastDir = string.Empty;
    private string _mpLoadedName = string.Empty;
    private bool _mpReplace = true;
    private string _mpStatus = string.Empty;

    private void DrawMakePlaceImport()
    {
        if (!ImGui.CollapsingHeader("Import a MakePlace shopping list")) return;

        WrapText(Grey, "Reads the FURNITURE section only. Dyes are ignored, and so is \"Furniture (With Dye)\" — that section repeats the same furnishings split by colour, so importing it too would double every quantity.");
        Dummy(4f);

        if (ImGui.Button("Choose file...##mpbrowse", new Vector2(SW(140), 0)))
        {
            _fileDialog.OpenFileDialog(
                "Select a MakePlace shopping list",
                "Text files{.txt},All files{.*}",
                (ok, paths) =>
                {
                    if (!ok || paths.Count == 0) return;
                    LoadMakePlaceFile(paths[0]);
                },
                1,
                string.IsNullOrWhiteSpace(_mpLastDir) ? null : _mpLastDir);
        }
        ImGui.SameLine(0, SW(8));
        ImGui.TextColored(Grey, string.IsNullOrEmpty(_mpLoadedName) ? "no file loaded" : _mpLoadedName);

        Dummy(2f);
        ImGui.TextColored(Grey, "...or paste the list here:");
        ImGui.InputTextMultiline("##mppaste", ref _mpPaste, 200000, new Vector2(-1, SW(120)));

        Dummy(2f);
        if (ImGui.Checkbox("Replace the current shopping list", ref _mpReplace)) { }
        ImGui.SameLine(0, SW(6));
        HelpMarker("On: the list is wiped and rebuilt from the import. Off: imported quantities are added on top of what's already there.");

        Dummy(2f);
        using (ImRaiiDisabled(string.IsNullOrWhiteSpace(_mpPaste)))
        {
            if (ImGui.Button("Import furniture", new Vector2(SW(160), 0))) RunMakePlaceImport();
        }
        ImGui.SameLine(0, SW(6));
        if (ImGui.Button("Clear##mpclear", new Vector2(SW(70), 0)))
        { _mpPaste = string.Empty; _mpStatus = string.Empty; _mpLoadedName = string.Empty; }

        if (!string.IsNullOrEmpty(_mpStatus))
        {
            Dummy(2f);
            WrapText(Grey, _mpStatus);
        }
    }

    private void LoadMakePlaceFile(string path)
    {
        try
        {
            _mpPaste = System.IO.File.ReadAllText(path);
            _mpLoadedName = System.IO.Path.GetFileName(path);
            _mpLastDir = System.IO.Path.GetDirectoryName(path) ?? string.Empty;
            _mpStatus = $"Loaded {_mpLoadedName} ({_mpPaste.Length:N0} characters). Press Import furniture.";
        }
        catch (Exception ex)
        {
            _mpStatus = $"Couldn't read that file: {ex.Message}";
        }
    }

    private void RunMakePlaceImport()
    {
        var result = MakePlaceImport.ParseFurniture(_mpPaste);
        if (result.Error != null) { _mpStatus = result.Error; return; }

        if (_mpReplace) Cfg.BuyerItems.Clear();
        Cfg.BuyerUnbuyable.Clear();

        foreach (var entry in result.Items)
        {
            var existing = Cfg.BuyerItems.FirstOrDefault(i => i.ItemId == entry.ItemId);
            if (existing != null)
            {
                existing.Quantity += entry.Quantity;
                continue;
            }
            Cfg.BuyerItems.Add(new BuyerItem
            {
                ItemId = entry.ItemId,
                Quantity = entry.Quantity,
                UseMaxPrice = Cfg.BuyerNewItemUseMaxPrice,
                MaxPrice = Math.Max(1, _buyerAddMax),
            });
        }

        Cfg.BuyerUnbuyable.AddRange(result.Unbuyable);
        Cfg.Save();
        _plugin.Buyer.ClearPlan();

        _mpStatus = result.Unbuyable.Count == 0
            ? $"Imported {result.Items.Count} item type(s), {result.TotalUnits:N0} unit(s) in total."
            : $"Imported {result.Items.Count} item type(s), {result.TotalUnits:N0} unit(s). {result.Unbuyable.Count} line(s) can't be bought — listed below.";
    }

    private void DrawUnbuyableReport()
    {
        if (Cfg.BuyerUnbuyable.Count == 0) return;

        Dummy(4f);
        ImGui.TextColored(Red, $"Can't be bought ({Cfg.BuyerUnbuyable.Count}):");
        if (ImGui.BeginChild("##unbuyable", new Vector2(0, SW(90)), true))
        {
            foreach (var line in Cfg.BuyerUnbuyable)
                WrapText(Red, line);
        }
        ImGui.EndChild();
        if (ImGui.Button("Dismiss##unbuyclear", new Vector2(SW(100), 0))) { Cfg.BuyerUnbuyable.Clear(); Cfg.Save(); }
    }

    /// <summary>
    /// With caps off, "cheapest available" can still be an expensive listing. The wallet guards
    /// are the real protection, so point at them rather than silently relying on them.
    /// </summary>
    private void DrawUncappedWarning()
    {
        var uncapped = Cfg.BuyerItems.Count(i => i.Enabled && !i.UseMaxPrice);
        if (uncapped == 0 || Cfg.BuyerMaxSpendPerRun > 0) return;

        Dummy(4f);
        WrapText(Gold, $"{uncapped} item(s) have no price cap and there's no per-run spend limit set. The Buyer will take the cheapest listings it finds whatever they cost — consider setting \"Max spend per run\" in Buyer settings.");
    }

    // ---- scan scope ---------------------------------------------------------------------------

    private void DrawBuyerScope()
    {
        var chosen = Cfg.BuyerDataCenters;
        var here = WorldInfo.CurrentDataCenter();
        var hereWorld = WorldInfo.CurrentWorld();
        var hereRegion = WorldInfo.CurrentRegion();

        ImGui.Text("Scan scope:");
        ImGui.SameLine();
        ImGui.TextColored(Grey, string.IsNullOrEmpty(hereWorld)
            ? "(location unknown)"
            : $"{hereWorld} / {here} / {hereRegion}");

        var mode = Math.Clamp(Cfg.BuyerScopeMode, 0, 3);
        if (ImGui.RadioButton("My world", ref mode, 0)) { Cfg.BuyerScopeMode = 0; Cfg.Save(); }
        ImGui.SameLine();
        if (ImGui.RadioButton("My Data Center", ref mode, 1)) { Cfg.BuyerScopeMode = 1; Cfg.Save(); }
        ImGui.SameLine();
        if (ImGui.RadioButton("My whole Region", ref mode, 2)) { Cfg.BuyerScopeMode = 2; Cfg.Save(); }
        ImGui.SameLine();
        if (ImGui.RadioButton("Custom", ref mode, 3)) { Cfg.BuyerScopeMode = 3; Cfg.Save(); }
        ImGui.SameLine(0, SW(6));
        HelpMarker("Where to look for listings. My world = no travel at all. My Data Center = anywhere you can visit freely. My whole Region = every DC you can reach, which means data-center transfers. Custom = tick exactly the DCs you want.");

        if (mode == 3)
        {
            Dummy(4f);
            var showAll = Cfg.BuyerShowAllRegions;
            if (ImGui.Checkbox("Show data centers outside my region", ref showAll)) { Cfg.BuyerShowAllRegions = showAll; Cfg.Save(); }
            ImGui.SameLine(0, SW(6));
            HelpMarker("You can't travel outside your region, so these are for price-watching only. The Buyer will scan them but can't reach them to buy.");

            var dcs = showAll
                ? WorldInfo.AllDataCenters()
                : WorldInfo.DataCentersInRegion(WorldInfo.CurrentRegionId());
            if (dcs.Count == 0) dcs = WorldInfo.AllDataCenters();

            Dummy(2f);
            if (ImGui.Button("All##dcall", new Vector2(SW(70), 0)))
            {
                foreach (var dc in dcs) if (!chosen.Contains(dc)) chosen.Add(dc);
                Cfg.Save();
            }
            ImGui.SameLine(0, SW(6));
            if (ImGui.Button("None##dcnone", new Vector2(SW(70), 0))) { chosen.Clear(); Cfg.Save(); }
            ImGui.SameLine(0, SW(6));
            if (ImGui.Button("Just mine##dcmine", new Vector2(SW(90), 0)))
            {
                chosen.Clear();
                if (!string.IsNullOrWhiteSpace(here)) chosen.Add(here);
                Cfg.Save();
            }

            Dummy(2f);
            if (ImGui.BeginChild("##buyerscope", new Vector2(0, SW(150)), true))
            {
                foreach (var dc in dcs)
                {
                    var on = chosen.Contains(dc);
                    if (ImGui.Checkbox($"{dc}{(string.Equals(dc, here, StringComparison.OrdinalIgnoreCase) ? "  (you are here)" : "")}##dc{dc}", ref on))
                    {
                        if (on) { if (!chosen.Contains(dc)) chosen.Add(dc); }
                        else chosen.Remove(dc);
                        Cfg.Save();
                    }

                    if (!on) continue;
                    DrawWorldOptOuts(dc);
                }
            }
            ImGui.EndChild();
        }
        else
        {
            Dummy(4f);
            // Per-world opt-outs still apply outside Custom mode — collapsed so they stay out of
            // the way until you want them.
            if (ImGui.TreeNode("Skip individual worlds##buyerskip"))
            {
                var dcs = mode == 2
                    ? WorldInfo.DataCentersInRegion(WorldInfo.CurrentRegionId())
                    : new List<string> { here };
                foreach (var dc in dcs.Where(d => !string.IsNullOrWhiteSpace(d)))
                {
                    ImGui.TextColored(Grey, dc);
                    DrawWorldOptOuts(dc);
                }
                ImGui.TreePop();
            }
        }

        Dummy(4f);
        DrawDcPriority();

        var label = string.Join(", ", _plugin.Buyer.ResolveLocations());
        WrapText(Grey, $"Will scan: {(string.IsNullOrWhiteSpace(label) ? "(nothing)" : label)}");
        if (Cfg.BuyerExcludedWorlds.Count > 0)
            WrapText(Grey, $"Skipping {Cfg.BuyerExcludedWorlds.Count} world(s): {string.Join(", ", Cfg.BuyerExcludedWorlds)}");
    }

    /// <summary>
    /// Optional strict data-center visit order. Off by default because it changes what you end up
    /// with if a run stops early — see the warning text below.
    /// </summary>
    private void DrawDcPriority()
    {
        var on = Cfg.BuyerDcPriorityEnabled;
        if (ImGui.Checkbox("Visit data centers in a fixed order", ref on))
        {
            Cfg.BuyerDcPriorityEnabled = on;
            if (on && Cfg.BuyerDcPriority.Count == 0)
                Cfg.BuyerDcPriority = WorldInfo.DataCentersInRegion(WorldInfo.CurrentRegionId());
            Cfg.Save();
        }
        ImGui.SameLine(0, SW(6));
        HelpMarker("Off: the richest stop is visited first, so an interrupted run has banked the most value. On: whole data centers are cleared in the order below, which means one transfer per DC instead of bouncing between them.");

        if (!on) return;

        // Seed from the region if the list is empty or the character moved region.
        var regionDcs = WorldInfo.DataCentersInRegion(WorldInfo.CurrentRegionId());
        foreach (var dc in regionDcs)
            if (!Cfg.BuyerDcPriority.Contains(dc, StringComparer.OrdinalIgnoreCase))
            { Cfg.BuyerDcPriority.Add(dc); Cfg.Save(); }

        Dummy(2f);
        ImGui.TextColored(Grey, "Order (top first):");

        int? moveUp = null, moveDown = null;
        for (var i = 0; i < Cfg.BuyerDcPriority.Count; i++)
        {
            var dc = Cfg.BuyerDcPriority[i];
            using (ImRaiiDisabled(i == 0))
            {
                if (ImGui.SmallButton($"^##up{dc}")) moveUp = i;
            }
            ImGui.SameLine(0, SW(3));
            using (ImRaiiDisabled(i == Cfg.BuyerDcPriority.Count - 1))
            {
                if (ImGui.SmallButton($"v##down{dc}")) moveDown = i;
            }
            ImGui.SameLine(0, SW(8));
            ImGui.TextUnformatted($"{i + 1}. {dc}");
        }

        if (moveUp is int u && u > 0)
        {
            (Cfg.BuyerDcPriority[u - 1], Cfg.BuyerDcPriority[u]) = (Cfg.BuyerDcPriority[u], Cfg.BuyerDcPriority[u - 1]);
            Cfg.Save();
        }
        if (moveDown is int d && d < Cfg.BuyerDcPriority.Count - 1)
        {
            (Cfg.BuyerDcPriority[d + 1], Cfg.BuyerDcPriority[d]) = (Cfg.BuyerDcPriority[d], Cfg.BuyerDcPriority[d + 1]);
            Cfg.Save();
        }

        Dummy(2f);
        WrapText(Gold, "Prices don't change — the plan still picks the same cheapest listings. What changes is the order you collect them in, so if your bags fill or you stop early you'll have whatever the first data centers held rather than the most valuable listings.");
    }

    /// <summary>Per-world include/exclude checkboxes for one data center.</summary>
    private void DrawWorldOptOuts(string dc)
    {
        ImGui.Indent(SW(18));
        if (ImGui.TreeNode($"worlds##w{dc}"))
        {
            foreach (var world in WorldInfo.WorldsOnDataCenter(dc))
            {
                var included = !Cfg.BuyerExcludedWorlds.Contains(world, StringComparer.OrdinalIgnoreCase);
                if (ImGui.Checkbox($"{world}##world{world}", ref included))
                {
                    if (included) Cfg.BuyerExcludedWorlds.RemoveAll(w => string.Equals(w, world, StringComparison.OrdinalIgnoreCase));
                    else if (!Cfg.BuyerExcludedWorlds.Contains(world, StringComparer.OrdinalIgnoreCase)) Cfg.BuyerExcludedWorlds.Add(world);
                    Cfg.Save();
                }
            }
            ImGui.TreePop();
        }
        ImGui.Unindent(SW(18));
    }

    // ---- scan / send --------------------------------------------------------------------------

    private void DrawBuyerActions()
    {
        var buyer = _plugin.Buyer;
        var runner = _plugin.Buy;

        var dry = Cfg.BuyerDryRun;
        if (ImGui.Checkbox("Dry run (walk the route, buy nothing)", ref dry)) { Cfg.BuyerDryRun = dry; Cfg.Save(); }
        ImGui.SameLine(0, SW(6));
        HelpMarker("ON by default. The Buyer travels, opens boards, reads live prices and reports exactly what it WOULD buy — without spending a gil. Leave this on for your first run on a new setup.");

        Dummy(4f);

        using (ImRaiiDisabled(buyer.Scanning || runner.Running || Cfg.BuyerItems.Count == 0))
        {
            if (ImGui.Button("SCAN", new Vector2(SW(120), SW(30))))
            {
                buyer.Scan();
                _plugin.PlanWindow.IsOpen = true;
            }
        }
        ImGui.SameLine(0, SW(10));

        var plan = buyer.Plan;
        using (ImRaiiDisabled(runner.Running || plan == null || !plan.HasWork))
        {
            if (ImGui.Button("SEND", new Vector2(SW(160), SW(30))))
            {
                if (plan != null) runner.Start(plan, Cfg.BuyerDryRun);
            }
        }
        if (!Cfg.BuyerDryRun)
        {
            ImGui.SameLine(0, SW(10));
            ImGui.TextColored(Gold, "LIVE — will spend gil");
        }
        if (runner.IsPaused)
        {
            ImGui.SameLine(0, SW(10));
            if (ImGui.Button("RESUME##buyresume", new Vector2(SW(120), SW(30)))) runner.Resume();
        }
        else if (runner.Running)
        {
            ImGui.SameLine(0, SW(10));
            if (ImGui.Button("Pause##buypause", new Vector2(SW(80), SW(30)))) runner.PauseByUser();
        }
        if (runner.Running)
        {
            ImGui.SameLine(0, SW(10));
            if (ImGui.Button("Stop##buystop", new Vector2(SW(80), SW(30)))) runner.Stop();
        }

        Dummy(4f);
        if (plan != null)
        {
            if (ImGui.Button("Show plan##buyshow", new Vector2(SW(120), 0))) _plugin.PlanWindow.IsOpen = true;
            ImGui.SameLine(0, SW(8));
        }
        ImGui.TextColored(buyer.Scanning ? Gold : Grey, buyer.Status);
        if (!string.IsNullOrEmpty(buyer.Error) && !buyer.Scanning)
            WrapText(Red, buyer.Error!);

        if (runner.IsPaused)
        {
            Dummy(2f);
            WrapText(Gold, $"PAUSED — {runner.PauseReason}");
            var free = runner.FreeSlotsNow();
            if (free >= 0)
                WrapText(free >= Math.Max(1, Cfg.BuyerMinFreeSlots) ? Green : Grey,
                    $"Free bag slots: {free} (need {Math.Max(1, Cfg.BuyerMinFreeSlots)}).");
            WrapText(Grey, "Resume carries on from the same world and item — nothing is re-bought. Stop keeps what you've already bought and updates the list.");
        }
        else if (runner.Running || runner.State == BuyState.Error || runner.State == BuyState.Done)
        {
            var col = runner.State == BuyState.Error ? Red : runner.Running ? Gold : Green;
            WrapText(col, runner.Status);
        }

        Dummy(2f);
        if (!LifestreamBridge.Available)
            WrapText(Red, "Lifestream isn't loaded — world hops are unavailable, so only the world you're standing on can be bought from.");
        if (Cfg.BuyerUseNavmesh && !NavmeshBridge.Available)
            WrapText(Grey, "vnavmesh isn't loaded — you'll need to stand near a market board yourself.");
    }

    // ---- settings -----------------------------------------------------------------------------

    private void DrawBuyerSettings()
    {
        if (!ImGui.CollapsingHeader("Buyer settings")) return;

        var cheapest = Cfg.BuyerShowCheapestCount;
        ImGui.SetNextItemWidth(SW(180));
        if (ImGui.InputInt("Cheapest listings to show (0 = off)", ref cheapest, 1)) { Cfg.BuyerShowCheapestCount = Math.Clamp(cheapest, 0, 25); Cfg.Save(); }
        ImGui.SameLine(0, SW(6));
        HelpMarker("Per item, the plan lists this many of the cheapest listings in scope even when they're above your max price — so \"none at 1,000g\" also tells you what it actually costs and where.");

        var depth = Cfg.BuyerListingDepth;
        ImGui.SetNextItemWidth(SW(180));
        if (ImGui.InputInt("Listings to fetch per data center", ref depth, 50)) { Cfg.BuyerListingDepth = Math.Clamp(depth, 20, 500); Cfg.Save(); }
        ImGui.SameLine(0, SW(6));
        HelpMarker("How deep to read each data center's listings. Furnishing markets run to hundreds of rows, so raise this if \"Found\" comes back short for something you can see plenty of on Universalis.");

        var topUp = Cfg.BuyerTopUpShortfalls;
        if (ImGui.Checkbox("Keep going if the route finishes short", ref topUp)) { Cfg.BuyerTopUpShortfalls = topUp; Cfg.Save(); }
        ImGui.SameLine(0, SW(6));
        HelpMarker("When the planned stops are done and something is still short, the run extends itself down the price list — the next cheapest listings the scan found — and adds more stops. Always at the end, never mid-route: chasing a shortfall early means paying the current world's expensive tail while cheaper listings wait further along.");

        using (ImRaiiDisabled(!Cfg.BuyerTopUpShortfalls))
        {
            var passes = Cfg.BuyerMaxTopUpPasses;
            ImGui.SetNextItemWidth(SW(180));
            if (ImGui.InputInt("Max top-up passes", ref passes, 1)) { Cfg.BuyerMaxTopUpPasses = Math.Clamp(passes, 1, 10); Cfg.Save(); }
        }

        using (ImRaiiDisabled(!Cfg.BuyerTopUpShortfalls))
        {
            var overPlan = Cfg.BuyerTopUpMaxOverPlanPercent;
            ImGui.SetNextItemWidth(SW(180));
            if (ImGui.InputInt("Uncapped price rail (% over plan)", ref overPlan, 5)) { Cfg.BuyerTopUpMaxOverPlanPercent = Math.Clamp(overPlan, 0, 500); Cfg.Save(); }
            ImGui.SameLine(0, SW(6));
            HelpMarker("For items with NO price cap: the dearest price the plan budgeted is the most you were ever willing to pay, so it won't go past that plus this percentage. Keeps a run from taking a 1.8M listing while a 975k one waits two stops away. 0 removes the rail; items with their own cap ignore it.");
        }

        var audit = Cfg.BuyerWriteAuditLog;
        if (ImGui.Checkbox("Write an audit log for each run", ref audit)) { Cfg.BuyerWriteAuditLog = audit; Cfg.Save(); }
        ImGui.SameLine(0, SW(6));
        HelpMarker("Saves a CSV of the run — every purchase with planned vs actual cost, every skip and its reason — to the plugin config folder. The Audit tab in the Buy Plan window shows the same thing live, with an Open folder button.");

        var autoOff = Cfg.BuyerAutoDisableCompleted;
        if (ImGui.Checkbox("Update the shopping list after a run", ref autoOff)) { Cfg.BuyerAutoDisableCompleted = autoOff; Cfg.Save(); }
        ImGui.SameLine(0, SW(6));
        HelpMarker("After a REAL run the list is rewritten to match what's left: partly filled rows have what you bought deducted (wanted 4, got 3 -> now wants 1), and fully filled rows are unticked. Dry runs never change the list.");

        var delay = Cfg.BuyerScanDelayMs;
        ImGui.SetNextItemWidth(SW(180));
        if (ImGui.InputInt("Gap between Universalis calls (ms)", ref delay, 20)) { Cfg.BuyerScanDelayMs = Math.Clamp(delay, 0, 2000); Cfg.Save(); }
        ImGui.SameLine(0, SW(6));
        HelpMarker("Scanning several data centers means one call per item per DC. A small gap keeps Universalis happy; raise it if you see lookup failures.");

        var overshoot = Cfg.BuyerAllowOvershoot;
        if (ImGui.Checkbox("Buy oversized stacks", ref overshoot)) { Cfg.BuyerAllowOvershoot = overshoot; Cfg.Save(); }
        ImGui.SameLine(0, SW(6));
        HelpMarker("Market listings can only be bought whole. ON: a stack of 50 is bought even if you only asked for 10. OFF: oversized stacks are skipped, which may leave your order unfilled.");

        var reserve = (int)Math.Min(Cfg.BuyerGilReserve, int.MaxValue);
        ImGui.SetNextItemWidth(SW(180));
        if (ImGui.InputInt("Gil reserve (never spend below)", ref reserve, 10_000)) { Cfg.BuyerGilReserve = Math.Max(0, reserve); Cfg.Save(); }

        var cap = (int)Math.Min(Cfg.BuyerMaxSpendPerRun, int.MaxValue);
        ImGui.SetNextItemWidth(SW(180));
        if (ImGui.InputInt("Max spend per run (0 = no limit)", ref cap, 10_000)) { Cfg.BuyerMaxSpendPerRun = Math.Max(0, cap); Cfg.Save(); }

        var slots = Cfg.BuyerMinFreeSlots;
        ImGui.SetNextItemWidth(SW(180));
        if (ImGui.InputInt("Stop with this many bag slots left", ref slots, 1)) { Cfg.BuyerMinFreeSlots = Math.Clamp(slots, 0, 30); Cfg.Save(); }

        var home = Cfg.BuyerReturnHome;
        if (ImGui.Checkbox("Return to my starting world when done", ref home)) { Cfg.BuyerReturnHome = home; Cfg.Save(); }

        var homeWorld = Cfg.BuyerHomeWorld;
        ImGui.SetNextItemWidth(SW(180));
        if (ImGui.InputTextWithHint("Home world", "blank = where the run started", ref homeWorld, 40)) { Cfg.BuyerHomeWorld = homeWorld; Cfg.Save(); }

        var nav = Cfg.BuyerUseNavmesh;
        if (ImGui.Checkbox("Let vnavmesh walk me to the market board", ref nav)) { Cfg.BuyerUseNavmesh = nav; Cfg.Save(); }

        var typeDelay = Cfg.BuyerSearchTypeDelayMs;
        ImGui.SetNextItemWidth(SW(180));
        if (ImGui.InputInt("Delay after typing before searching (ms)", ref typeDelay, 100)) { Cfg.BuyerSearchTypeDelayMs = Math.Clamp(typeDelay, 0, 5000); Cfg.Save(); }
        ImGui.SameLine(0, SW(6));
        HelpMarker("The name is typed, then the search fires a moment later. Raise this if searches come back empty for items you know are listed.");

        var partial = Cfg.BuyerPartialMatch;
        if (ImGui.Checkbox("Use the board's Partial Match when searching", ref partial)) { Cfg.BuyerPartialMatch = partial; Cfg.Save(); }
        ImGui.SameLine(0, SW(6));
        HelpMarker("On by default. Exact-name matching is fussier about punctuation and localisation than it looks; partial match finds the item and we still pick the row by item ID, so it can't grab the wrong thing.");

        var manual = Cfg.BuyerManualSearchFallback;
        if (ImGui.Checkbox("If the search won't fire, let me do it by hand", ref manual)) { Cfg.BuyerManualSearchFallback = manual; Cfg.Save(); }
        ImGui.SameLine(0, SW(6));
        HelpMarker("Rather than skipping the item, the run pauses and waits for you to search it on the board yourself. As soon as the listings are up it carries on automatically.");

        var manualWait = Cfg.BuyerManualSearchTimeoutSec;
        ImGui.SetNextItemWidth(SW(180));
        if (ImGui.InputInt("Seconds to wait for a manual search", ref manualWait, 10)) { Cfg.BuyerManualSearchTimeoutSec = Math.Clamp(manualWait, 5, 600); Cfg.Save(); }

        var boardName = Cfg.BuyerBoardNameOverride;
        ImGui.SetNextItemWidth(SW(180));
        if (ImGui.InputTextWithHint("Board object name", "blank = auto (EN/JP/DE/FR)", ref boardName, 60)) { Cfg.BuyerBoardNameOverride = boardName; Cfg.Save(); }

        Dummy(4f);
        if (ImGui.TreeNode("Advanced — board click opcodes"))
        {
            WrapText(Grey, "Only change these if a game patch breaks the buy path. Run \"/undercut buydump\" at an open board and read the output first.");
            var forceReq = Cfg.BuyerForceListingRequest;
            if (ImGui.Checkbox("Also request listings directly from the proxy", ref forceReq)) { Cfg.BuyerForceListingRequest = forceReq; Cfg.Save(); }
            ImGui.SameLine(0, SW(6));
            HelpMarker("Off by default. Clicking the result row already loads the listings; forcing a second request on top can leave the game reporting that it's still waiting for data.");

            var learn = Cfg.BuyerLearnEvents;
            if (ImGui.Checkbox("Learn mode — log board events", ref learn)) { Cfg.BuyerLearnEvents = learn; Cfg.Save(); _plugin.Buy.ApplyLearnMode(); }
            ImGui.SameLine(0, SW(6));
            HelpMarker("Turn on, open a market board, and click a search result and a listing BY HAND. Each click prints its event type and parameter to chat — put those numbers in the fields below and the Buyer will reproduce them exactly.");

            var rowType = Cfg.BuyerResultRowEventType;
            ImGui.SetNextItemWidth(SW(120));
            if (ImGui.InputInt("Result row event", ref rowType, 1)) { Cfg.BuyerResultRowEventType = rowType; Cfg.Save(); }
            var rowOff = Cfg.BuyerResultRowEventParam;
            ImGui.SetNextItemWidth(SW(120));
            if (ImGui.InputInt("Result row param", ref rowOff, 1)) { Cfg.BuyerResultRowEventParam = rowOff; Cfg.Save(); }

            var listType = Cfg.BuyerListingRowEventType;
            ImGui.SetNextItemWidth(SW(120));
            if (ImGui.InputInt("Listing row event", ref listType, 1)) { Cfg.BuyerListingRowEventType = listType; Cfg.Save(); }
            var listOff = Cfg.BuyerListingRowEventParam;
            ImGui.SetNextItemWidth(SW(120));
            if (ImGui.InputInt("Listing row param", ref listOff, 1)) { Cfg.BuyerListingRowEventParam = listOff; Cfg.Save(); }
            var searchBtn = Cfg.BuyerSearchButtonOpcode;
            ImGui.SetNextItemWidth(SW(120));
            if (ImGui.InputInt("Search button (fallback)", ref searchBtn, 1)) { Cfg.BuyerSearchButtonOpcode = searchBtn; Cfg.Save(); }
            ImGui.TreePop();
        }
    }
}
