using System;
using Dalamud.Bindings.ImGui;
using static MarketHelper.UiScale;

namespace MarketHelper.Windows;

public partial class MainWindow
{
    private void DrawSettingsTab()
    {
        var changed = false;

        ImGui.SetNextItemWidth(SW(160));
        var undercut = Cfg.Undercut;
        if (ImGui.InputInt("Undercut amount (gil)", ref undercut))
        { Cfg.Undercut = Math.Max(0, undercut); changed = true; }

        var dontUc = Cfg.DontUndercutMyRetainers;
        if (ImGui.Checkbox("Don't undercut my own retainers (match instead)", ref dontUc))
        { Cfg.DontUndercutMyRetainers = dontUc; changed = true; }

        var hq = Cfg.CheckForHq;
        if (ImGui.Checkbox("Handle HQ / NQ separately", ref hq))
        { Cfg.CheckForHq = hq; changed = true; }

        ImGui.SetNextItemWidth(SW(220));
        var nqMult = Cfg.NqPriceDropMultiplier;
        if (ImGui.SliderFloat("NQ price drop multiplier", ref nqMult, 0.1f, 1.0f, "%.2f"))
        { Cfg.NqPriceDropMultiplier = nqMult; changed = true; }

        Divider();

        var sanity = Cfg.PriceSanityChecking;
        if (ImGui.Checkbox("Price sanity checking", ref sanity))
        { Cfg.PriceSanityChecking = sanity; changed = true; }
        ImGui.SameLine(0, SW(6));
        HelpMarker("Ignores listings priced below half the trimmed mean of historical prices (likely misprices).");

        ImGui.SetNextItemWidth(SW(220));
        var depth = Cfg.PriceSanityCheckDepth;
        if (ImGui.SliderInt("Sanity check depth", ref depth, 0, 13))
        { Cfg.PriceSanityCheckDepth = depth; changed = true; }

        Divider();

        ImGui.SetNextItemWidth(SW(160));
        var floor = Cfg.MinPriceFloor;
        if (ImGui.InputInt("Min price floor", ref floor))
        { Cfg.MinPriceFloor = Math.Max(1, floor); changed = true; }
        ImGui.SameLine(0, SW(6));
        HelpMarker("Applied when the computed price would be 1 gil or less.");

        var skip = Cfg.SkipItemsAlreadyLowest;
        if (ImGui.Checkbox("Skip items already at target price", ref skip))
        { Cfg.SkipItemsAlreadyLowest = skip; changed = true; }

        var autoConfirm = Cfg.AutoConfirm;
        if (ImGui.Checkbox("Auto-confirm after setting price", ref autoConfirm))
        { Cfg.AutoConfirm = autoConfirm; changed = true; }
        ImGui.SameLine(0, SW(6));
        HelpMarker("On: fully hands-off — sets and confirms. Off: sets the price but you click Confirm yourself.");

        ImGui.SetNextItemWidth(SW(220));
        var speed = Cfg.SpeedMultiplier;
        if (ImGui.SliderFloat("Step speed", ref speed, 0.5f, 2.0f, "%.2fx"))
        { Cfg.SpeedMultiplier = speed; changed = true; }
        ImGui.SameLine(0, SW(6));
        HelpMarker("Lower = faster (shorter waits between steps). Raise toward 2x if items occasionally get skipped on high latency. Doesn't affect the market search, which is server-limited.");

        var mem = Cfg.UsePriceMemory;
        if (ImGui.Checkbox("Remember prices within a run", ref mem))
        { Cfg.UsePriceMemory = mem; changed = true; }
        ImGui.SameLine(0, SW(6));
        HelpMarker("After scanning an item once, later identical stacks (same name + HQ) are priced instantly without re-searching. Reset each time you press Run.");

        var skipMann = Cfg.SkipMannequinItems;
        if (ImGui.Checkbox("Skip mannequin / display items", ref skipMann))
        { Cfg.SkipMannequinItems = skipMann; changed = true; }
        ImGui.SameLine(0, SW(6));
        HelpMarker("Detects the game's mannequin icon on each sell-list row and never opens those items, so they can't be undercut or accidentally delisted.");

        if (Cfg.SkipMannequinItems)
        {
            var fallback = Cfg.MannequinUsePriceFallback;
            if (ImGui.Checkbox("Also skip by price threshold", ref fallback))
            { Cfg.MannequinUsePriceFallback = fallback; changed = true; }
            ImGui.SameLine(0, SW(6));
            HelpMarker("Optional fallback: also skip items priced at/above the threshold, in case the icon isn't detected. May catch genuine high-value items, so off by default.");

            if (Cfg.MannequinUsePriceFallback)
            {
                ImGui.SetNextItemWidth(SW(160));
                var thresh = (int)Cfg.MannequinPriceThreshold;
                if (ImGui.InputInt("Price threshold (gil)", ref thresh, 0))
                { Cfg.MannequinPriceThreshold = Math.Max(1000, thresh); changed = true; }
            }
        }

        Divider();

        var verbose = Cfg.Verbose;
        if (ImGui.Checkbox("Verbose chat output", ref verbose))
        { Cfg.Verbose = verbose; changed = true; }

        var debug = Cfg.Debug;
        if (ImGui.Checkbox("Debug chat output", ref debug))
        { Cfg.Debug = debug; changed = true; }

        if (changed) Cfg.Save();
    }
}
