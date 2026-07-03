using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace MarketHelper;

[Serializable]
public class ItemOverride
{
    public int? Minimum { get; set; }
    public int? Maximum { get; set; }
    public int? Default { get; set; }
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // --- Core undercut behaviour ---
    public int Undercut { get; set; } = 1;                     // gil to undercut by
    public bool DontUndercutMyRetainers { get; set; } = true;  // match (don't undercut) own retainers
    public bool CheckForHq { get; set; } = true;               // treat HQ / NQ separately
    public float NqPriceDropMultiplier { get; set; } = 0.6f;   // NQ price when only HQ listings exist

    // --- Price sanity checking ---
    public bool PriceSanityChecking { get; set; } = true;
    public int PriceSanityCheckDepth { get; set; } = 10;       // 0-13 top listings to scan

    // --- Overrides ---
    public bool UsingOverrides { get; set; } = true;
    // keyed by normalised item name (letters/digits only, lowercased)
    public Dictionary<string, ItemOverride> ItemOverrides { get; set; } = new();

    // --- Own retainers to protect (normalised lowercase names). Auto-populated from InfoProxy. ---
    public List<string> MyRetainers { get; set; } = new();
    public bool AutoDetectMyRetainers { get; set; } = true;

    // Retainers to skip entirely during the walk (by name).
    public List<string> BlacklistRetainers { get; set; } = new();

    // --- Behaviour / safety ---
    public bool Verbose { get; set; } = true;
    public bool Debug { get; set; } = false;
    public int MinPriceFloor { get; set; } = 69;               // price applied when computed <= 1
    public bool SkipItemsAlreadyLowest { get; set; } = true;   // don't re-set if already cheapest & ours

    // --- Reactive mode ---
    // Off by default: when on, prices any item whose sell window opens even outside a Run.
    // Most users want undercutting only during an explicit Run, so leave this off.
    public bool MarketHelperOnOpen { get; set; } = false;      // undercut each item as its RetainerSell window opens
    public bool AutoConfirm { get; set; } = true;              // auto-confirm after setting price (fully hands-off)

    // --- Speed ---
    // Multiplies all inter-step waits. 1.0 = default (fast). Raise toward 2.0 if steps
    // occasionally miss on high latency; lower toward 0.5 to go faster on a fast connection.
    public float SpeedMultiplier { get; set; } = 1.0f;

    // Reuse a scanned item's price for later identical items in the same run (skips re-searching
    // duplicate stacks). Cleared at the start of each Run.
    public bool UsePriceMemory { get; set; } = true;

    // Skip mannequin / display items so they're never opened or undercut. Primary detection is
    // the game's mannequin icon on the sell-list row; the price threshold is a safety-net fallback
    // (ON by default, because opening a mannequin item can delist it — we must never open one).
    public bool SkipMannequinItems { get; set; } = true;
    public bool MannequinUsePriceFallback { get; set; } = false;
    public long MannequinPriceThreshold { get; set; } = 5_000_000;

    // Hard safety: never click a context menu (which could delist) on an item priced at/above this.
    // Set high so it only ever catches mannequin displays (conventionally 30M+), never real items.
    public long MannequinSafetyPrice { get; set; } = 20_000_000;

    // --- Flipper tax settings ---
    public bool ApplySellerTax { get; set; } = true;
    public bool ApplyBuyerTax { get; set; } = true;
    public float SellerTaxPercent { get; set; } = 5.0f;   // 0–5; reduced in expansion hubs
    public float BuyerTaxPercent { get; set; } = 5.0f;    // 5% when buying cross-city

    // --- Lister settings ---
    public List<uint> ListerItems { get; set; } = new();   // item IDs queued to auto-list
    public bool ListerPriceByDc { get; set; } = false;     // legacy; superseded by ListerPriceScope
    public int ListerPriceScope { get; set; } = 0;         // 0 = home world, 1 = data center, 2 = region
    public int ListerUndercutBy { get; set; } = 1;         // gil below the lowest
    public string ListerWorldOverride { get; set; } = "";  // manual world if auto-detect fails
    public string ListerDcOverride { get; set; } = "";     // manual DC if auto-detect fails

    // Outlier protection: if the cheapest listing is more than this % below the NEXT listing,
    // treat it as a lone undercut/troll and price against the next one instead. 0 = disabled.
    public float ListerOutlierGapPercent { get; set; } = 15.0f;

    [NonSerialized] private IDalamudPluginInterface? pluginInterface;

    public void Initialize(IDalamudPluginInterface pi) => pluginInterface = pi;

    public void Save() => pluginInterface?.SavePluginConfig(this);
}
