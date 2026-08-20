using System;
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
        WrapText("Build a shopping list, press SCAN to find the cheapest listings across your data center, then press SEND to go and buy them. Nothing is ever bought above the max price you set for that item.");
        Dummy(6f);

        DrawBuyerAddRow();
        Divider();
        DrawBuyerList();
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
        ImGui.SetNextItemWidth(SW(120));
        if (ImGui.InputInt("Max price each##buymax", ref _buyerAddMax, 100)) _buyerAddMax = Math.Max(1, _buyerAddMax);
        ImGui.SameLine(0, SW(6));
        HelpMarker("Applied to items you add from here. Each row's cap can be edited afterwards. The Buyer NEVER pays more than this per unit.");

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
            return;
        }

        if (ImGui.BeginTable("##buyertable", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("On", ImGuiTableColumnFlags.WidthFixed, SW(30));
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 2.0f);
            ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, SW(70));
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
                var max = (int)Math.Min(row.MaxPrice, int.MaxValue);
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputInt($"##m{row.ItemId}", ref max, 0)) { row.MaxPrice = Math.Max(1, max); Cfg.Save(); }

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
            Cfg.Save();
            _plugin.Buyer.ClearPlan();
        }
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
            if (ImGui.Button(Cfg.BuyerDryRun ? "SEND (dry run)" : "SEND", new Vector2(SW(160), SW(30))))
            {
                if (plan != null) runner.Start(plan, Cfg.BuyerDryRun);
            }
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

        if (runner.Running || runner.State == BuyState.Error || runner.State == BuyState.Done)
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

        var region = Cfg.BuyerScanRegion;
        if (ImGui.Checkbox("Scan the whole region instead of my data center", ref region)) { Cfg.BuyerScanRegion = region; Cfg.Save(); }
        ImGui.SameLine(0, SW(6));
        HelpMarker("Off = your own DC (e.g. Aether). On = the whole region, which finds cheaper listings but means cross-DC travel and much longer runs.");

        var scope = Cfg.BuyerScopeOverride;
        ImGui.SetNextItemWidth(SW(180));
        if (ImGui.InputTextWithHint("Scope override", "blank = auto", ref scope, 40)) { Cfg.BuyerScopeOverride = scope; Cfg.Save(); }

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

        var boardName = Cfg.BuyerBoardNameOverride;
        ImGui.SetNextItemWidth(SW(180));
        if (ImGui.InputTextWithHint("Board object name", "blank = auto (EN/JP/DE/FR)", ref boardName, 60)) { Cfg.BuyerBoardNameOverride = boardName; Cfg.Save(); }

        Dummy(4f);
        if (ImGui.TreeNode("Advanced — board click opcodes"))
        {
            WrapText(Grey, "Only change these if a game patch breaks the buy path. Run \"/undercut buydump\" at an open board and read the output first.");
            var sel = Cfg.BuyerSelectResultOpcode;
            ImGui.SetNextItemWidth(SW(120));
            if (ImGui.InputInt("Open search result", ref sel, 1)) { Cfg.BuyerSelectResultOpcode = sel; Cfg.Save(); }
            var buy = Cfg.BuyerBuyOpcode;
            ImGui.SetNextItemWidth(SW(120));
            if (ImGui.InputInt("Click a listing", ref buy, 1)) { Cfg.BuyerBuyOpcode = buy; Cfg.Save(); }
            ImGui.TreePop();
        }
    }
}
