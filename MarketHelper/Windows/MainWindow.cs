using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Interface;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Bindings.ImGui;
using MarketHelper.FancyUI;
using static MarketHelper.UiScale;

namespace MarketHelper.Windows;

/// <summary>
/// Main plugin window: the Fancy UI shell (custom title bar, drag, resize grip) around the tab
/// body. Each tab lives in its own MainWindow.*.cs partial so new tabs can be dropped in without
/// touching this file. The reskin is presentation-only — behaviour code is unchanged.
/// </summary>
public partial class MainWindow : ShellWindow
{
    private readonly Plugin _plugin;
    private Configuration Cfg => _plugin.Config;

    /// <summary>Shared file-picker host. Must be drawn every frame, outside the tab bar, or the
    /// dialog vanishes the moment you switch tabs while it's open.</summary>
    private readonly FileDialogManager _fileDialog = new();

    public MainWindow(Plugin plugin)
        : base("MarketHelper##Main", "Market Helper",
               new Vector2(480, 560), new Vector2(1600, 1400))
    {
        _plugin = plugin;
    }

    // Blocky geometry for the stock widgets that remain (text fields, sliders, combos, tables):
    // square-ish corners and roomier padding, on top of the colour theme the shell already pushes.
    // FrameBg is lifted one step above Surface so slider tracks and input fields are visible
    // (live screenshot 2026-10-30: the troll-floor grab floated on an invisible track).
    public override void PreDraw()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, Theme.RadiusControl);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, Theme.RadiusCard);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, Theme.RadiusControl);
        ImGui.PushStyleVar(ImGuiStyleVar.GrabRounding, Theme.RadiusControl);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(Theme.Gap * 0.8f, Theme.Gap * 0.5f));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(Theme.Gap * 0.9f, Theme.Gap * 0.7f));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemInnerSpacing, new Vector2(Theme.Gap * 0.6f, Theme.Gap * 0.4f));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.Surface);
        base.PreDraw();
        ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(0.196f, 0.161f, 0.310f, 1f));
    }

    public override void PostDraw()
    {
        ImGui.PopStyleColor(1);
        base.PostDraw();
        ImGui.PopStyleColor(1);
        ImGui.PopStyleVar(7);
    }

    /// <summary>Version chip + one status dot per runner, drawn into the free title-bar span.</summary>
    protected override void DrawHeaderExtras(Vector2 freeMin, Vector2 freeMax)
    {
        var dl = ImGui.GetWindowDrawList();
        var y = (freeMin.Y + freeMax.Y) * 0.5f;
        var x = freeMin.X + Theme.Gap;

        var ver = typeof(MainWindow).Assembly.GetName().Version;
        var verText = ver == null ? string.Empty : "v" + ver.ToString(3);
        var ts = ImGui.CalcTextSize(verText);
        dl.AddText(new Vector2(x, y - ts.Y * 0.5f), Theme.U32(Theme.Muted), verText);
        x += ts.X + Theme.Gap * 2f;

        (bool Running, string Name)[] runners =
        {
            (_plugin.Nav.Running, "Undercut"),
            (_plugin.Lister.Running, "Lister"),
            (_plugin.Gatherer.Running, "Gatherer"),
            (_plugin.Buy.Running, "Buyer"),
        };
        foreach (var (running, _) in runners)
        {
            dl.AddCircleFilled(new Vector2(x, y), 4f * Theme.UiScale,
                Theme.U32(running ? Theme.Good : Theme.Muted));
            x += 12f * Theme.UiScale;
        }
    }

    protected override void DrawBody()
    {
        if (ImGui.BeginTabBar("##markethelper_tabs"))
        {
            if (BeginIconTab(FontAwesomeIcon.Tags, "Undercut"))
            {
                DrawUndercutTab();
                ImGui.EndTabItem();
            }
            if (BeginIconTab(FontAwesomeIcon.MoneyBillTrendUp, "Flipper"))
            {
                DrawFlipperTab();
                ImGui.EndTabItem();
            }
            if (BeginIconTab(FontAwesomeIcon.ListUl, "Lister"))
            {
                DrawListerTab();
                ImGui.EndTabItem();
            }
            if (BeginIconTab(FontAwesomeIcon.BoxOpen, "Item Gatherer"))
            {
                DrawGathererTab();
                ImGui.EndTabItem();
            }
            if (BeginIconTab(FontAwesomeIcon.CartArrowDown, "Buyer"))
            {
                DrawBuyerTab();
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }

        _fileDialog.Draw();
    }

    /// <summary>BeginTabItem with a FontAwesome glyph prefix. The helper is verified against the
    /// dev-hooks Dalamud.dll (Dalamud.Interface.FontAwesomeExtensions.ToIconString).</summary>
    private static bool BeginIconTab(FontAwesomeIcon icon, string label)
        => ImGui.BeginTabItem($"{icon.ToIconString()} {label}");

    /// <summary>The Undercut tab: the run section plus its sub-tabs (Settings/Overrides/etc).</summary>
    private void DrawUndercutTab()
    {
        UiKit.Section(FontAwesomeIcon.Tags, "undercut run");
        DrawRunSection();

        if (ImGui.CollapsingHeader("Undercut settings, overrides and report"))
        {
            if (ImGui.BeginTabBar("##undercut_subtabs"))
            {
                if (ImGui.BeginTabItem("Settings"))
                {
                    DrawSettingsTab();
                    ImGui.EndTabItem();
                }
                if (ImGui.BeginTabItem("Overrides"))
                {
                    DrawOverridesTab();
                    ImGui.EndTabItem();
                }
                if (ImGui.BeginTabItem("My Retainers"))
                {
                    DrawRetainersTab();
                    ImGui.EndTabItem();
                }
                if (ImGui.BeginTabItem("Report"))
                {
                    DrawReportTab();
                    ImGui.EndTabItem();
                }
                ImGui.EndTabBar();
            }
        }
    }

    private void DrawRunSection()
    {
        var listener = _plugin.Listener;
        var nav = _plugin.Nav;

        // Hero status card (the Fancy UI pattern): status label, live status, one-line hint.
        var s = Theme.UiScale;
        var p = ImGui.GetCursorScreenPos();
        var size = new Vector2(ImGui.GetContentRegionAvail().X, 92f * s);
        var dl = ImGui.GetWindowDrawList();
        UiKit.Card(dl, p, p + size, null, glow: nav.Running ? 0.10f : 0f);

        ImGui.SetCursorScreenPos(p + new Vector2(Theme.Pad, Theme.Pad));
        ImGui.TextColored(nav.Running ? Theme.Good : Theme.Muted, nav.Running ? "RUNNING" : "READY");
        ImGui.SetCursorScreenPos(p + new Vector2(Theme.Pad, Theme.Pad + 20f * s));
        ImGui.TextColored(Theme.Text, nav.Status);
        ImGui.SetCursorScreenPos(p + new Vector2(Theme.Pad, Theme.Pad + 48f * s));
        ImGui.PushTextWrapPos(p.X + size.X - Theme.Pad);
        ImGui.TextColored(Theme.Muted, "Stand near a summoning bell, then run. Opens the bell and undercuts every item on every retainer.");
        ImGui.PopTextWrapPos();
        ImGui.SetCursorScreenPos(new Vector2(p.X, p.Y + size.Y + Theme.Gap));

        if (nav.Running)
        {
            if (UiKit.GradientButton("##fancy-nav-stop", "STOP", Theme.Danger, Theme.Danger, 36f))
                nav.Stop();
        }
        else
        {
            if (UiKit.GradientButton("##fancy-nav-run", "RUN — WALK ALL RETAINERS", Theme.Accent, Theme.Good, 40f))
                nav.Start();
        }

        // --- Reactive / on-demand ---
        UiKit.Section(FontAwesomeIcon.Bolt, "reactive");
        var auto = Cfg.MarketHelperOnOpen;
        var autoNew = UiKit.Toggle("##fancy-autoopen", auto);
        ImGui.SameLine(0, Theme.Gap);
        ImGui.TextUnformatted("Auto-undercut on open (even when NOT running)");
        ImGui.SameLine(0, SW(6));
        HelpMarker("Off by default. When ON, ANY item whose sell window opens gets undercut — even if you're just managing a retainer by hand. Leave off unless you want that.");
        if (autoNew != auto)
        { Cfg.MarketHelperOnOpen = autoNew; Cfg.Save(); }

        using (ImRaiiDisabled(!Addons.Exists("RetainerSell") || listener.Busy))
        {
            if (UiKit.ChipButton("##fancy-price-now", "UNDERCUT OPEN ITEM", false, 220f * Theme.UiScale))
                listener.PriceOpenItemNow();
        }
        ImGui.SameLine(0, Theme.Gap);
        ImGui.TextColored(listener.Busy ? Theme.Warn : Theme.Muted, listener.Status);
    }

    // Minimal BeginDisabled/EndDisabled scope helper (avoids a hard ImRaii dependency).
    private static DisabledScope ImRaiiDisabled(bool disabled) => new(disabled);

    private readonly struct DisabledScope : IDisposable
    {
        private readonly bool _on;
        public DisabledScope(bool on) { _on = on; if (on) ImGui.BeginDisabled(); }
        public void Dispose() { if (_on) ImGui.EndDisabled(); }
    }

    // ---- shared helpers available to every tab partial ----
    // The Fancy UI kit (UiKit, Theme, ShellWindow) lives in MarketHelper.FancyUI; section
    // headings are UiKit.Section(...). This file keeps only the HelpMarker pattern.

    /// <summary>Inline "(?)" tooltip marker using the proven SetTooltip pattern.</summary>
    private static void HelpMarker(string text)
    {
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(text);
    }

    // Per-list transient input buffers for DrawStringListEditor, keyed by id.
    private readonly Dictionary<string, string> _listInputs = new();

    /// <summary>
    /// Editable list of strings: shows each entry with a remove button, a text field to add one,
    /// and optionally an "add current" button that pulls a value from a supplier.
    /// </summary>
    private void DrawStringListEditor(List<string> list, string id, string? addCurrentLabel, Func<string>? addCurrent)
    {
        string? remove = null;
        foreach (var entry in list)
        {
            if (ImGui.SmallButton($"x##{id}rm{entry}")) remove = entry;
            ImGui.SameLine();
            ImGui.Text(entry);
        }
        if (remove != null) { list.Remove(remove); Cfg.Save(); }

        var buf = _listInputs.TryGetValue(id, out var b) ? b : string.Empty;
        ImGui.SetNextItemWidth(SW(180));
        if (ImGui.InputTextWithHint($"##{id}add", "type a name, Enter to add", ref buf, 64,
                ImGuiInputTextFlags.EnterReturnsTrue))
        {
            var v = buf.Trim();
            if (v.Length > 0 && !list.Contains(v)) { list.Add(v); Cfg.Save(); }
            buf = string.Empty;
        }
        _listInputs[id] = buf;

        if (addCurrentLabel != null && addCurrent != null)
        {
            ImGui.SameLine();
            if (ImGui.Button($"{addCurrentLabel}##{id}cur"))
            {
                var v = addCurrent();
                if (!string.IsNullOrEmpty(v) && !list.Contains(v)) { list.Add(v); Cfg.Save(); }
            }
        }
        Dummy(2f);
    }
}
