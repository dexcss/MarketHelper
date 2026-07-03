using System;
using System.Numerics;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using MarketHelper;
using static MarketHelper.UiScale;

namespace MarketHelper.Windows;

/// <summary>
/// Main plugin window. This file is the shell: construction, the tab bar, the always-visible
/// run section, and shared UI helpers. Each tab lives in its own MainWindow.*.cs partial so
/// new tabs can be dropped in without touching this file (VenueHelper layout convention).
/// </summary>
public partial class MainWindow : Window
{
    private readonly Plugin _plugin;
    private Configuration Cfg => _plugin.Config;

    public MainWindow(Plugin plugin)
        : base("Market Helper##Main", ImGuiWindowFlags.None)
    {
        _plugin = plugin;
        // Scaled constraints so the window stays usable at 4K / high global scale.
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = SV(480, 560),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        Size = SV(520, 600);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        if (ImGui.BeginTabBar("##markethelper_tabs"))
        {
            if (ImGui.BeginTabItem("Undercut"))
            {
                DrawUndercutTab();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Flipper"))
            {
                DrawFlipperTab();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Lister"))
            {
                DrawListerTab();
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }
    }

    /// <summary>The Undercut tab: the run section plus its sub-tabs (Settings/Overrides/etc).</summary>
    private void DrawUndercutTab()
    {
        DrawRunSection();
        ImGui.Separator();

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

    private void DrawRunSection()
    {
        var listener = _plugin.Listener;
        var nav = _plugin.Nav;

        // --- Full standalone walk ---
        WrapText("Stand near a summoning bell, then Run. Opens the bell and undercuts every item on every retainer.");
        Dummy(2f);

        if (nav.Running)
        {
            if (ImGui.Button("Stop", new Vector2(SW(120), 0))) nav.Stop();
        }
        else
        {
            if (ImGui.Button("Run (walk all retainers)", new Vector2(SW(220), 0))) nav.Start();
        }
        ImGui.SameLine(0, SW(8));
        ImGui.TextColored(nav.Running ? Gold : Grey, nav.Status);

        Divider();

        // --- Reactive / on-demand ---
        var auto = Cfg.MarketHelperOnOpen;
        if (ImGui.Checkbox("Auto-undercut on open (even when NOT running)", ref auto))
        { Cfg.MarketHelperOnOpen = auto; Cfg.Save(); }
        ImGui.SameLine(0, SW(6));
        HelpMarker("Off by default. When ON, ANY item whose sell window opens gets undercut — even if you're just managing a retainer by hand. Leave off unless you want that.");

        using (ImRaiiDisabled(!Addons.Exists("RetainerSell") || listener.Busy))
        {
            if (ImGui.Button("Undercut open item", new Vector2(SW(200), 0)))
                listener.PriceOpenItemNow();
        }
        ImGui.SameLine(0, SW(8));
        ImGui.TextColored(listener.Busy ? Gold : Grey, listener.Status);
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

    /// <summary>Inline "(?)" tooltip marker using the proven SetTooltip pattern.</summary>
    private static void HelpMarker(string text)
    {
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(text);
    }

    /// <summary>A scaled separator with padding above and below.</summary>
    private static void Divider()
    {
        Dummy(4f);
        ImGui.Separator();
        Dummy(4f);
    }
}
