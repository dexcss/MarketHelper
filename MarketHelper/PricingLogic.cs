using System;
using System.Collections.Generic;
using System.Text;

namespace MarketHelper;

public struct PriceResult
{
    public int Price;
    public bool OverrideApplied;
    public bool SanityTriggered;
    public bool FloorApplied;      // computed <= 1, floor used
    public bool MatchedOwnRetainer;
    public int LowestSeen;
    public string Notes;
}

/// <summary>
/// Port of MarketBotty's pricing decision logic. Given the current item's HQ state,
/// the sorted market listings and history mean, it returns the price to set.
/// </summary>
public static class PricingLogic
{
    public static string Normalize(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    /// <param name="listings">Ascending by price.</param>
    /// <param name="historyTrimmedMean">Trimmed mean of historical sales for sanity checking; 0 if unknown.</param>
    public static PriceResult Compute(
        Configuration cfg,
        string itemName,
        bool itemIsHq,
        List<Listing> listings,
        long historyTrimmedMean)
    {
        var res = new PriceResult { Notes = string.Empty };
        var uc = cfg.Undercut;

        if (listings.Count == 0)
        {
            // No competition: fall back to history mean if we have it, else a safe default.
            var basePrice = historyTrimmedMean > 0 ? (int)historyTrimmedMean : cfg.MinPriceFloor;
            res.Price = basePrice;
            ApplyOverride(cfg, itemName, ref res, isDefaultMode: true);
            if (res.Price <= 1) { res.Price = cfg.MinPriceFloor; res.FloorApplied = true; }
            res.Notes = "No active listings; used history/default.";
            return res;
        }

        res.LowestSeen = (int)listings[0].Price;

        var target = 0;             // index into listings (0-based; SND used 1-based)
        var depth = Math.Min(cfg.PriceSanityCheckDepth, listings.Count);
        var maxChecks = depth * 2;
        var checks = 0;

        // --- Sanity checking: skip listings priced below half the trimmed mean (likely misprices),
        //     respecting HQ/NQ matching. ---
        if (cfg.PriceSanityChecking && historyTrimmedMean > 0)
        {
            while (target < depth && checks < maxChecks)
            {
                checks++;
                var l = listings[target];

                if (l.Price <= (historyTrimmedMean / 2))
                {
                    // Below sanity threshold.
                    if (cfg.CheckForHq && (l.IsHq != itemIsHq))
                    {
                        target++;
                        if (target >= depth) { target = 0; break; }
                        continue;
                    }
                    res.SanityTriggered = true;
                    target++;
                    if (target >= depth) { target = 0; break; }
                    continue;
                }
                break;
            }
        }

        // --- HQ matching: if our item is HQ, walk forward to the first HQ listing. ---
        if (cfg.CheckForHq && itemIsHq)
        {
            while (target < listings.Count && !listings[target].IsHq)
                target++;
            if (target >= listings.Count) target = 0; // no HQ competition; treat first as base
        }

        var chosen = listings[Math.Min(target, listings.Count - 1)];

        // --- Don't undercut my own retainers: match instead of undercut. ---
        if (cfg.DontUndercutMyRetainers)
        {
            var rn = Normalize(chosen.Retainer);
            if (chosen.IsHq == itemIsHq && cfg.MyRetainers.Contains(rn))
            {
                uc = 0;
                res.MatchedOwnRetainer = true;
            }
        }

        int price;
        if (cfg.CheckForHq && !itemIsHq && chosen.IsHq)
        {
            // Our item is NQ but the chosen listing is HQ: drop below by multiplier.
            price = (int)Math.Floor(chosen.Price * cfg.NqPriceDropMultiplier + 0.5);
        }
        else
        {
            price = (int)chosen.Price - uc;
        }

        res.Price = price;

        // --- Overrides (min/max clamp). ---
        ApplyOverride(cfg, itemName, ref res, isDefaultMode: false);

        // --- Floor: never set to <= 1. ---
        if (res.Price <= 1)
        {
            res.Price = cfg.MinPriceFloor;
            res.FloorApplied = true;
        }

        return res;
    }

    private static void ApplyOverride(Configuration cfg, string itemName, ref PriceResult res, bool isDefaultMode)
    {
        if (!cfg.UsingOverrides) return;
        var key = Normalize(itemName);
        if (!cfg.ItemOverrides.TryGetValue(key, out var ov) || ov == null) return;

        if (isDefaultMode && ov.Default.HasValue)
        {
            res.Price = ov.Default.Value;
            res.OverrideApplied = true;
        }
        if (ov.Minimum.HasValue && res.Price < ov.Minimum.Value)
        {
            res.Price = ov.Minimum.Value;
            res.OverrideApplied = true;
        }
        if (ov.Maximum.HasValue && res.Price > ov.Maximum.Value)
        {
            res.Price = ov.Maximum.Value;
            res.OverrideApplied = true;
        }
    }
}
