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

/// <summary>One row of the Buyer's shopping list.</summary>
[Serializable]
public class BuyerItem
{
    public uint ItemId { get; set; }
    public int Quantity { get; set; } = 1;
    public long MaxPrice { get; set; } = 1000;   // never pay more than this PER UNIT
    public bool HqOnly { get; set; }
    public bool Enabled { get; set; } = true;
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
    // A cheapest listing is treated as an obvious misprice and skipped only if it's this % or more
    // below the NEXT listing up (a clear price gap). Conservative default: 25%.
    public float UndercutOutlierGapPercent { get; set; } = 25.0f;

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
    public int MinPriceFloor { get; set; } = 100_000_000;      // fallback price; set very high so an
                                                               // accidental fallback lists out of reach
                                                               // (nobody buys it) rather than dirt cheap
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

    // Minimum delay (ms) before each market search, to avoid the game's "Please wait and try your
    // search again" rate limit. Higher = safer on high-latency connections. Default 600ms.
    public int SearchPacingMs { get; set; } = 600;

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

    // Undercut retainers automatically during AutoRetainer's multi-mode (via AR's postprocess API).
    // When AR opens each retainer to run ventures, Market Helper undercuts that retainer's listings
    // first, then hands control back so AR can send ventures. Off by default.
    public bool AutoRetainerIntegration { get; set; } = false;

    // Optional allow-lists to scope which retainers/characters the integration acts on. Empty list
    // = no filter (act on all). If both are set, BOTH must pass. Opt-out model.
    public List<string> ArOnlyCharacters { get; set; } = new();   // character names (e.g. "First Last")
    public List<string> ArOnlyRetainers { get; set; } = new();    // retainer names

    // Also auto-list preset items from inventory during AR postprocess (uses the Lister's item
    // list and pricing). Off by default — this WRITES new market listings during AR's cycle.
    public bool ArAutoList { get; set; } = false;

    // --- Flipper tax settings ---
    public bool ApplySellerTax { get; set; } = true;
    public bool ApplyBuyerTax { get; set; } = true;
    public float SellerTaxPercent { get; set; } = 5.0f;   // 0–5; reduced in expansion hubs
    public float BuyerTaxPercent { get; set; } = 5.0f;    // 5% when buying cross-city

    // --- Lister settings ---
    // Permanent preset list (saved to config, persists across restarts).
    public List<uint> ListerItems { get; set; } = new();

    // --- Item Gatherer settings ---
    // Designated items to pull off retainers into your main inventory. Own list, saved.
    public List<uint> GathererItems { get; set; } = new();
    public bool GatherFromInventory { get; set; } = true;   // pull from retainer inventory
    public bool GatherFromMarket { get; set; } = true;      // pull back from active market listings
    public bool ListerPriceByDc { get; set; } = false;     // legacy; superseded by ListerPriceScope
    public int ListerPriceScope { get; set; } = 0;         // 0 = home world, 1 = data center, 2 = region

    // Undercut pricing scope: 0 = home world (live board), 1 = data center, 2 = region (Universalis).
    public int UndercutPriceScope { get; set; } = 0;
    public int ListerUndercutBy { get; set; } = 1;         // gil below the lowest
    public string ListerWorldOverride { get; set; } = "";  // manual world if auto-detect fails
    public string ListerDcOverride { get; set; } = "";     // manual DC if auto-detect fails

    // Outlier protection: if the cheapest listing is more than this % below the NEXT listing,
    // treat it as a lone undercut/troll and price against the next one instead. 0 = disabled.
    public float ListerOutlierGapPercent { get; set; } = 15.0f;

    // --- Buyer settings ---
    // Shopping list: what to buy, how many, and the hard per-unit price ceiling for each.
    public List<BuyerItem> BuyerItems { get; set; } = new();

    // Dry run is ON by default and deliberately so: the Buyer spends real gil, and the first run
    // on any new setup should prove the route and the prices without touching the wallet.
    public bool BuyerDryRun { get; set; } = true;

    // Scan scope. Mirrors the Lister's selector so both tabs read the same way:
    //   0 = my world, 1 = my data center, 2 = my whole region, 3 = custom DC picker.
    // Custom is the default because it's the only mode that lets you pick several DCs by hand.
    public int BuyerScopeMode { get; set; } = 3;

    // Which data centers get scanned in Custom mode. Empty falls back to the DC you're on.
    // Any DC in your own region is fair game since you can travel freely within it.
    public List<string> BuyerDataCenters { get; set; } = new();

    // Worlds to never scan, travel to, or buy from, even when their DC is selected.
    public List<string> BuyerExcludedWorlds { get; set; } = new();

    // Show data centers outside your region in the picker. Off by default because you can't
    // travel to them — useful only for looking at prices.
    public bool BuyerShowAllRegions { get; set; } = false;

    // How many of the cheapest listings to show per item in the plan, regardless of your cap.
    // This is what answers "nothing at 1,000g — so what DOES it cost?". 0 hides the section.
    public int BuyerShowCheapestCount { get; set; } = 5;

    // Politeness gap between Universalis calls when scanning several DCs at once.
    public int BuyerScanDelayMs { get; set; } = 120;

    // A market listing must be bought whole. When on, a stack larger than what you still need is
    // still bought; when off, oversized stacks are skipped.
    public bool BuyerAllowOvershoot { get; set; } = true;

    // Hard wallet guards. The reserve is never spent below; the per-run ceiling caps one SEND.
    public long BuyerGilReserve { get; set; } = 100_000;
    public long BuyerMaxSpendPerRun { get; set; } = 0;       // 0 = no per-run limit

    // Stop buying before the bags are actually full, so nothing is bought that can't be received.
    public int BuyerMinFreeSlots { get; set; } = 3;

    // Hop back to where you started once the run finishes.
    public bool BuyerReturnHome { get; set; } = true;
    public string BuyerHomeWorld { get; set; } = "";         // blank = world you started the run on

    // Let vnavmesh walk the last stretch to a market board after a world hop.
    public bool BuyerUseNavmesh { get; set; } = true;

    // Extra market-board object name, if your client language isn't one of the built-in four.
    public string BuyerBoardNameOverride { get; set; } = "";

    // Gap between typing the item name into the board and firing the search. Firing in the same
    // frame as the text write can search a half-set field and come back "No matching items".
    public int BuyerSearchTypeDelayMs { get; set; } = 500;

    // Tick the board's "Partial Match" box when searching. On by default: exact-name matching is
    // fussier about punctuation and localisation than it looks.
    public bool BuyerPartialMatch { get; set; } = true;

    // If the automated search won't fire, pause and let you type it yourself rather than skipping
    // the item. The runner watches the board and picks straight back up once listings appear.
    public bool BuyerManualSearchFallback { get; set; } = true;
    public int BuyerManualSearchTimeoutSec { get; set; } = 90;

    // Fallback callback opcode for the board's Search button, used only by the retry ladder.
    public int BuyerSearchButtonOpcode { get; set; } = 0;

    // Atk event codes for the two list-row clicks. 35 = AtkEventType.ListItemClick, which is what
    // the game sends when you click a row in an AtkComponentList; the parameter is the row index.
    // Exposed (with a learn mode to capture the real values) because these are the only numbers in
    // the buy path that a game patch could move.
    public int BuyerResultRowEventType { get; set; } = 35;
    public int BuyerResultRowParamOffset { get; set; } = 0;
    public int BuyerListingRowEventType { get; set; } = 35;
    public int BuyerListingRowParamOffset { get; set; } = 0;

    // Log every event the board addons receive, so clicking a row by hand reveals the exact
    // event type and parameter to use. Off by default — it is very chatty.
    public bool BuyerLearnEvents { get; set; } = false;

    [NonSerialized] private IDalamudPluginInterface? pluginInterface;

    public void Initialize(IDalamudPluginInterface pi) => pluginInterface = pi;

    public void Save() => pluginInterface?.SavePluginConfig(this);
}
