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

    // Price ceiling per unit, applied ONLY when UseMaxPrice is on. Off by default: the normal
    // case is "buy me the N cheapest of these", and a cap is the exception you opt into.
    public bool UseMaxPrice { get; set; }
    public long MaxPrice { get; set; } = 1000;

    public bool HqOnly { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>The cap to actually enforce — no ceiling at all when the cap is switched off.</summary>
    [Newtonsoft.Json.JsonIgnore]
    public long EffectiveCap => UseMaxPrice ? MaxPrice : long.MaxValue;
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
    public bool SkipItemsAlreadyLowest { get; set; } = true;   // don't re-set if already cheapest & ours    // --- Reactive mode ---
    // Off by default: when on, prices any item whose sell window opens even outside a Run.
    // Most users want undercutting only during an explicit Run, so leave this off.
    public bool MarketHelperOnOpen { get; set; } = false;      // undercut each item as its RetainerSell window opens
    public bool AutoConfirm { get; set; } = true;              // auto-confirm after setting price (fully hands-off)

    // --- Speed ---
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
    public int ListerPriceScope { get; set; } = 0;         // 0 = home world, 1 = data center, 2 = region

    // Units per listing, PER ITEM, keyed by item id. Absent or 0 means the whole stack in one
    // listing (the game's default). Set 1 to split a stack of 18 dye into 18 separate listings —
    // remember a retainer only has 20 slots.
    public Dictionary<uint, int> ListerStackSizes { get; set; } = new();

    // Total units to list for an item in one run, keyed by item id. Absent or 0 means "everything
    // you're holding". Set it to hold some back — list 12 of the 20 dye and keep 8.
    public Dictionary<uint, int> ListerListAmounts { get; set; } = new();

    // Callback case for the sell window's quantity field. The value is written directly too, and
    // read back before confirming, so this is belt-and-braces rather than load-bearing.
    public int ListerQuantityOpcode { get; set; } = 3;

    // Undercut pricing scope: 0 = home world (live board), 1 = data center, 2 = region (Universalis).
    public int UndercutPriceScope { get; set; } = 0;
    public int ListerUndercutBy { get; set; } = 1;         // gil below the lowest
    public string ListerWorldOverride { get; set; } = "";  // manual world if auto-detect fails
    public string ListerDcOverride { get; set; } = "";     // manual DC if auto-detect fails

    // --- Buyer settings ---
    // Shopping list: what to buy, how many, and (optionally) a hard per-unit price ceiling.
    public List<BuyerItem> BuyerItems { get; set; } = new();

    // Lines from an imported MakePlace list that can't be bought on the market board, kept so the
    // Buyer tab can show you what it couldn't take.
    public List<string> BuyerUnbuyable { get; set; } = new();

    // Whether newly added items get a price cap by default, and what it is.
    public bool BuyerNewItemUseMaxPrice { get; set; } = false;

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

    // How many listings to pull per WORLD per item. 0 means "everything Universalis has", which
    // is the default and almost always what you want.
    //
    // This used to be per DATA CENTER, and that was the bug behind "it only found 9,144 of the
    // 119,880 I asked for". The cap applies to the whole response, so one request for a whole DC
    // returned the cheapest N rows across all its worlds and nothing deeper — for an item sold in
    // x99 stacks, 200 rows is a hard ceiling of ~19,800 units no matter how much is really listed.
    // The scan now asks each world separately, so every world gets its own budget.
    public int BuyerListingDepth { get; set; }

    // After a real run, rewrite the shopping list to match what's actually left: deduct what was
    // bought from each row's quantity, and untick rows whose order is complete. Stops a second
    // SEND from re-buying everything.
    public bool BuyerAutoDisableCompleted { get; set; } = true;

    // Visit whole data centers in a fixed order instead of chasing the most valuable stop first.
    // Off by default — it trades "bank the biggest wins early" for "fewer DC transfers".
    public bool BuyerDcPriorityEnabled { get; set; } = false;
    public List<string> BuyerDcPriority { get; set; } = new();

    // When the planned route finishes short, extend it: take the next cheapest listings the scan
    // found and add more stops. Happens at the END of the run, never mid-route.
    public bool BuyerTopUpShortfalls { get; set; } = true;

    // How many extension passes a single run may make before giving up.
    public int BuyerMaxTopUpPasses { get; set; } = 3;

    // Price rail for an UNCAPPED item: never pay more than the dearest unit price the plan
    // budgeted for it, plus this percentage. That planned maximum is the most we were ever willing
    // to pay; anything beyond it belongs to a re-scan, not to this run. 0 disables the rail.
    // Items with their own price cap ignore this — the cap governs.
    public int BuyerTopUpMaxOverPlanPercent { get; set; } = 10;

    // Built-in sanity net that applies EVEN WHEN an item has no price cap: never pay more than this
    // many times the cheapest price the scan found for the item, wherever that listing was. 10 =
    // never pay more than 10x the cheapest. 0 disables it. This is the hard backstop that stops a
    // stale plan from paying 1M for something the market was selling at 100g.
    public int BuyerSanityCeilingMultiple { get; set; } = 10;

    // Write a CSV audit trail of every run to the plugin config folder (buyer-logs), so a run can
    // be checked over afterwards instead of scrolled back through in chat.
    public bool BuyerWriteAuditLog { get; set; } = true;

    // Politeness gap between Universalis calls when scanning several DCs at once.
    public int BuyerScanDelayMs { get; set; } = 120;

    // A market listing must be bought whole. When on, a listing bigger than what you still need is
    // bought anyway (you take the entire listing). OFF BY DEFAULT: the Buyer is a shopping list —
    // "I want 5" must never turn into "I bought 99". Turn on only if you'd rather take a whole
    // listing than skip it.
    public bool BuyerAllowOvershoot { get; set; } = false;

    // Hard wallet guards. The reserve is never spent below; the per-run ceiling caps one SEND.
    public long BuyerGilReserve { get; set; } = 100_000;
    public long BuyerMaxSpendPerRun { get; set; } = 0;       // 0 = no per-run limit

    // Stop buying before the bags are actually full, so nothing is bought that can't be received.
    public int BuyerMinFreeSlots { get; set; } = 3;

    // Hop back to where you started once the run finishes.
    public bool BuyerReturnHome { get; set; } = true;
    public string BuyerHomeWorld { get; set; } = "";         // override target (used only when the
                                                             // "somewhere else" toggle is on)

    // Send the character somewhere OTHER than home when the run finishes. Off by default: left on
    // its own, the run returns to the character's home world automatically.
    public bool BuyerReturnElsewhere { get; set; } = false;

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

    // Also ask InfoProxyItemSearch for the listings directly (proxy->SearchItemId + RequestData).
    // OFF by default: the row click loads them on its own, and forcing a second request can leave
    // the proxy stuck reporting that it's still waiting.
    public bool BuyerForceListingRequest { get; set; } = false;

    // Atk event codes for the two list-row clicks, captured live with learn mode:
    //   ItemSearch results      -> ListItemClick(35), component param 3
    //   ItemSearchResult listing -> ListItemClick(35), component param 0
    // The param identifies the LIST COMPONENT, not the row; the row is set via the list's own
    // SelectedItemIndex. Kept in config so a game patch can be corrected without a rebuild.
    public int BuyerResultRowEventType { get; set; } = 35;
    public int BuyerResultRowEventParam { get; set; } = 3;
    public int BuyerListingRowEventType { get; set; } = 35;
    public int BuyerListingRowEventParam { get; set; } = 0;

    // Log every event the board addons receive, so clicking a row by hand reveals the exact
    // event type and parameter to use. Off by default — it is very chatty.
    public bool BuyerLearnEvents { get; set; } = false;

    [NonSerialized] private IDalamudPluginInterface? pluginInterface;

    public void Initialize(IDalamudPluginInterface pi)
    {
        pluginInterface = pi;

        // v1 -> v2: BuyerListingDepth changed meaning from "rows per data center" to "rows per
        // world", and gained 0 = unlimited. Anyone still carrying the old 200 default is carrying
        // a cap that silently truncated bulk orders, so clear it once.
        if (Version < 2)
        {
            if (BuyerListingDepth == 200) BuyerListingDepth = 0;
            Version = 2;
            Save();
        }

        // v2 -> v3: BuyerAllowOvershoot used to default to ON, which meant "I want 5" could buy an
        // entire 99-stack (a real 99m-gil accident waiting to happen). Reset everyone to the safe
        // default; anyone who genuinely wants whole-listing buys can re-tick it, now knowingly.
        if (Version < 3)
        {
            BuyerAllowOvershoot = false;
            Version = 3;
            Save();
        }
    }

    public void Save() => pluginInterface?.SavePluginConfig(this);
}
