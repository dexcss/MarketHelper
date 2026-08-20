using System;
using System.Linq;
using System.Numerics;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using static MarketHelper.UiScale;

namespace MarketHelper.Windows;

/// <summary>
/// The Buyer's scan-result popup: what's available, where, and what it would cost, with the SEND
/// button that hands the plan to BuyRunner.
///
/// Everything here is a SNAPSHOT of Universalis at scan time. The prices shown are what we expect,
/// not what will be paid — the runner re-checks every one against the live board and drops any
/// that have moved above your cap.
/// </summary>
public sealed class BuyPlanWindow : Window
{
    private readonly Plugin _plugin;
    private Configuration Cfg => _plugin.Config;

    public BuyPlanWindow(Plugin plugin)
        : base("Market Helper — Buy Plan##BuyPlan", ImGuiWindowFlags.None)
    {
        _plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = SV(520, 400),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        Size = SV(640, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        var buyer = _plugin.Buyer;
        var runner = _plugin.Buy;

        if (buyer.Scanning)
        {
            ImGui.TextColored(Gold, "Scanning Universalis...");
            return;
        }

        var plan = buyer.Plan;
        if (plan == null)
        {
            if (!string.IsNullOrEmpty(buyer.Error))
            {
                ImGui.TextColored(Red, "Scan failed.");
                Dummy(2f);
                WrapText(Red, buyer.Error!);
                Dummy(6f);
                if (ImGui.Button("Try again", new Vector2(SW(110), 0))) buyer.Scan();
                return;
            }
            ImGui.TextColored(Grey, "No scan yet. Press SCAN on the Buyer tab.");
            return;
        }

        WrapText(Grey, $"Scanned {plan.Scope} at {plan.ScannedAt:HH:mm:ss}");
        if (plan.HasWork)
            ImGui.TextColored(Green, $"{plan.GrandUnits} unit(s) across {plan.Stops.Count} world(s) — about {plan.GrandTotal:N0}g.");
        else
            ImGui.TextColored(Red, "Nothing available at or under your price caps.");

        Dummy(4f);
        if (ImGui.Button("Re-scan", new Vector2(SW(110), 0))) buyer.Scan();
        ImGui.SameLine(0, SW(8));
        using (Disabled(runner.Running || !plan.HasWork))
        {
            if (ImGui.Button("SEND", new Vector2(SW(160), 0)))
                runner.Start(plan, Cfg.BuyerDryRun);
        }
        if (runner.IsPaused)
        {
            ImGui.SameLine(0, SW(8));
            if (ImGui.Button("RESUME", new Vector2(SW(100), 0))) runner.Resume();
        }
        if (runner.Running)
        {
            ImGui.SameLine(0, SW(8));
            if (ImGui.Button("Stop", new Vector2(SW(80), 0))) runner.Stop();
        }
        ImGui.SameLine(0, SW(8));
        ImGui.TextColored(Cfg.BuyerDryRun ? Blue : Gold, Cfg.BuyerDryRun ? "dry run" : "LIVE — will spend gil");

        if (runner.IsPaused)
        {
            Dummy(2f);
            WrapText(Gold, $"PAUSED — {runner.PauseReason}");
        }
        else if (runner.Running || runner.State == BuyState.Error || runner.State == BuyState.Done)
        {
            Dummy(2f);
            WrapText(runner.State == BuyState.Error ? Red : runner.Running ? Gold : Green, runner.Status);
        }

        Divider();

        if (ImGui.BeginTabBar("##buyplantabs"))
        {
            if (ImGui.BeginTabItem("By item"))
            {
                DrawItems(plan);
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Route"))
            {
                DrawRoute(plan);
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem($"Log ({runner.Report.Count})"))
            {
                DrawLog();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem($"Audit ({runner.Audit.Count})"))
            {
                DrawAudit();
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }
    }

    private void DrawItems(BuyPlanResult plan)
    {
        if (plan.Warnings.Count > 0)
        {
            foreach (var w in plan.Warnings) WrapText(Gold, "• " + w);
            Dummy(4f);
        }

        var runner = _plugin.Buy;
        var boughtHeader = runner.DryRun ? "Bought (dry)" : "Bought";

        if (!ImGui.BeginTable("##planitems", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
            return;

        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 2.0f);
        ImGui.TableSetupColumn("Want", ImGuiTableColumnFlags.WidthFixed, SW(50));
        ImGui.TableSetupColumn("Found", ImGuiTableColumnFlags.WidthFixed, SW(55));
        ImGui.TableSetupColumn(boughtHeader, ImGuiTableColumnFlags.WidthFixed, SW(80));
        ImGui.TableSetupColumn("Cap", ImGuiTableColumnFlags.WidthFixed, SW(80));
        ImGui.TableSetupColumn("Cheapest", ImGuiTableColumnFlags.WidthStretch, 1.6f);
        ImGui.TableHeadersRow();

        foreach (var it in plan.Items)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn(); ImGui.TextUnformatted(it.ItemName);
            ImGui.TableNextColumn(); ImGui.TextUnformatted(it.Requested.ToString());

            ImGui.TableNextColumn();
            ImGui.TextColored(it.Satisfied ? Green : it.FoundUnits > 0 ? Gold : Red, it.FoundUnits.ToString());

            // Bought: what the last run actually moved. Blank until a run has produced numbers,
            // so an untouched plan doesn't read as "bought 0".
            ImGui.TableNextColumn();
            if (!runner.HasRunResults)
            {
                ImGui.TextColored(Grey, "-");
            }
            else
            {
                var got = runner.BoughtFor(it.ItemId);
                ImGui.TextColored(got >= it.Requested ? Green : got > 0 ? Gold : Red, got.ToString());
            }

            ImGui.TableNextColumn(); ImGui.TextUnformatted(it.CapText);

            ImGui.TableNextColumn();
            if (!it.AnyListingsAtAll)
                ImGui.TextColored(Red, "no listings");
            else
                ImGui.TextColored(!it.Capped || it.CheapestPrice <= it.MaxPrice ? Green : Red,
                    $"{it.CheapestPrice:N0}g on {it.CheapestWorld}"
                    + (string.IsNullOrWhiteSpace(it.CheapestDataCenter) ? "" : $" [{it.CheapestDataCenter}]"));
        }
        ImGui.EndTable();

        Dummy(6f);
        foreach (var it in plan.Items)
        {
            if (it.PerWorld.Count == 0 && it.Cheapest.Count == 0) continue;

            if (!ImGui.CollapsingHeader($"{it.ItemName}##detail{it.ItemId}", ImGuiTreeNodeFlags.DefaultOpen))
                continue;

            if (it.PerWorld.Count > 0)
            {
                ImGui.TextColored(Grey, it.Capped
                    ? $"Available at or under {it.MaxPrice:N0}g:"
                    : "Available (no price cap):");
                foreach (var kv in it.PerWorld)
                    ImGui.TextColored(Green, $"    {kv.Key} — {kv.Value.Units} available from {kv.Value.Cheapest:N0}g");
            }
            else if (it.AnyListingsAtAll)
            {
                ImGui.TextColored(Red, $"    None available at {it.MaxPrice:N0}g.");
            }

            if (it.Cheapest.Count > 0)
            {
                Dummy(2f);
                ImGui.TextColored(Grey, $"Cheapest {it.Cheapest.Count} in scope:");
                foreach (var c in it.Cheapest)
                {
                    var underCap = !it.Capped || c.UnitPrice <= it.MaxPrice;
                    var dcTag = string.IsNullOrWhiteSpace(c.DataCenter) ? "" : $" [{c.DataCenter}]";
                    ImGui.TextColored(underCap ? Green : Grey,
                        $"    {c.UnitPrice:N0}g  x{c.Quantity}{(c.Hq ? " HQ" : "")}  —  {c.World}{dcTag}{(underCap ? "" : "  (over cap)")}");
                }
            }
            Dummy(4f);
        }
    }

    private void DrawRoute(BuyPlanResult plan)
    {
        if (plan.Stops.Count == 0)
        {
            ImGui.TextColored(Grey, "No stops.");
            return;
        }

        foreach (var stop in plan.Stops)
        {
            var dcTag = string.IsNullOrWhiteSpace(stop.DataCenter) ? "" : $" [{stop.DataCenter}]";
            if (!ImGui.CollapsingHeader($"{stop.World}{dcTag} — {stop.TotalUnits} unit(s), {stop.TotalCost:N0}g##stop{stop.World}",
                    ImGuiTreeNodeFlags.DefaultOpen))
                continue;

            if (!ImGui.BeginTable($"##lines{stop.World}", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
                continue;

            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 2.0f);
            ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, SW(50));
            ImGui.TableSetupColumn("Each", ImGuiTableColumnFlags.WidthFixed, SW(90));
            ImGui.TableSetupColumn("Total", ImGuiTableColumnFlags.WidthFixed, SW(100));
            ImGui.TableHeadersRow();

            foreach (var line in stop.Lines)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.TextUnformatted(line.ItemName + (line.Hq ? " (HQ)" : ""));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(line.Quantity.ToString());
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"{line.UnitPrice:N0}g");
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"{line.Total:N0}g");
            }
            ImGui.EndTable();
            Dummy(4f);
        }
    }

    private void DrawLog()
    {
        var report = _plugin.Buy.Report;
        if (report.Count == 0)
        {
            ImGui.TextColored(Grey, "Nothing yet — the run log appears here.");
            return;
        }
        if (ImGui.BeginChild("##buylog", new Vector2(0, 0), true))
        {
            foreach (var line in report)
                WrapText(line.StartsWith("[dry run]", StringComparison.Ordinal) ? Blue : Grey, line);
        }
        ImGui.EndChild();
    }

    private bool _auditPurchasesOnly;
    private bool _auditProblemsOnly;

    /// <summary>
    /// The run's audit trail. Every purchase carries what we EXPECTED to pay next to what
    /// actually left the wallet, and the difference — which is the whole point: a purchase that
    /// cost more than planned is visible here instead of buried in a chat scrollback.
    /// </summary>
    private void DrawAudit()
    {
        var runner = _plugin.Buy;
        if (runner.Audit.Count == 0)
        {
            ImGui.TextColored(Grey, "Nothing yet — the audit trail is written as the run goes.");
            return;
        }

        var purchases = runner.Audit.Where(e => e.Kind == AuditKind.Purchase).ToList();
        var flagged = runner.Audit.Count(e => e.Flagged);
        var problems = runner.Audit.Count(e => e.Kind is AuditKind.Problem or AuditKind.Refused);

        var units = purchases.Sum(e => e.Quantity);
        var spent = purchases.Sum(e => e.Actual);
        var planned = purchases.Sum(e => e.Expected);

        ImGui.TextColored(Green, $"{purchases.Count} purchase(s), {units} unit(s), {spent:N0}g spent (planned {planned:N0}g).");
        if (flagged > 0) ImGui.TextColored(Red, $"{flagged} purchase(s) cost noticeably more than planned — shown in red.");
        if (problems > 0) ImGui.TextColored(Gold, $"{problems} problem(s) or refused purchase(s) recorded.");

        Dummy(2f);
        ImGui.Checkbox("Purchases only", ref _auditPurchasesOnly);
        ImGui.SameLine(0, SW(10));
        ImGui.Checkbox("Problems only", ref _auditProblemsOnly);
        ImGui.SameLine(0, SW(10));
        if (ImGui.Button("Save now##auditsave"))
        {
            var path = BuyAuditWriter.Save(runner.Audit, runner.DryRun);
            _plugin.Chat(path != null
                ? $"[Market Helper] Audit log saved: {path}"
                : "[Market Helper] Couldn't write the audit log.");
        }
        ImGui.SameLine(0, SW(6));
        if (ImGui.Button("Open folder##auditfolder"))
        {
            try
            {
                var dir = BuyAuditWriter.FolderPath();
                System.IO.Directory.CreateDirectory(dir);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
            }
            catch (Exception ex) { _plugin.Chat($"[Market Helper] Couldn't open the folder: {ex.Message}"); }
        }

        if (runner.LastAuditPath != null)
        {
            Dummy(2f);
            WrapText(Grey, runner.LastAuditPath);
        }

        Dummy(4f);
        if (ImGui.BeginChild("##auditrows", new Vector2(0, 0), true))
        {
            if (ImGui.BeginTable("##audittable", 7, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY))
            {
                ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthFixed, SW(60));
                ImGui.TableSetupColumn("What", ImGuiTableColumnFlags.WidthFixed, SW(70));
                ImGui.TableSetupColumn("World", ImGuiTableColumnFlags.WidthFixed, SW(90));
                ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 1.6f);
                ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, SW(40));
                ImGui.TableSetupColumn("Planned / Paid", ImGuiTableColumnFlags.WidthFixed, SW(150));
                ImGui.TableSetupColumn("Detail", ImGuiTableColumnFlags.WidthStretch, 1.8f);
                ImGui.TableHeadersRow();

                foreach (var e in runner.Audit)
                {
                    if (_auditPurchasesOnly && e.Kind is not (AuditKind.Purchase or AuditKind.DryBuy)) continue;
                    if (_auditProblemsOnly && e.Kind is not (AuditKind.Problem or AuditKind.Refused or AuditKind.Skip) && !e.Flagged) continue;

                    var colour = e.Flagged || e.Kind == AuditKind.Refused ? Red
                        : e.Kind == AuditKind.Problem ? Red
                        : e.Kind == AuditKind.Skip ? Gold
                        : e.Kind == AuditKind.Purchase ? Green
                        : Grey;

                    ImGui.TableNextRow();
                    ImGui.TableNextColumn(); ImGui.TextColored(Grey, e.Time.ToString("HH:mm:ss"));
                    ImGui.TableNextColumn(); ImGui.TextColored(colour, e.Kind.ToString());
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(e.World);
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(e.Item);
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(e.Quantity > 0 ? e.Quantity.ToString() : "");

                    ImGui.TableNextColumn();
                    if (e.Kind == AuditKind.Purchase)
                    {
                        var diff = e.Difference;
                        var sign = diff > 0 ? "+" : "";
                        ImGui.TextColored(e.Flagged ? Red : Green, $"{e.Expected:N0} / {e.Actual:N0} ({sign}{diff:N0})");
                    }
                    else if (e.Expected > 0) ImGui.TextColored(Grey, $"{e.Expected:N0}");
                    else ImGui.TextUnformatted("");

                    ImGui.TableNextColumn(); ImGui.TextColored(Grey, e.Detail);
                }
                ImGui.EndTable();
            }
        }
        ImGui.EndChild();
    }

    private static DisabledScope Disabled(bool on) => new(on);

    private readonly struct DisabledScope : IDisposable
    {
        private readonly bool _on;
        public DisabledScope(bool on) { _on = on; if (on) ImGui.BeginDisabled(); }
        public void Dispose() { if (_on) ImGui.EndDisabled(); }
    }

    private static void Divider()
    {
        Dummy(4f);
        ImGui.Separator();
        Dummy(4f);
    }
}
