# Market Helper

An all-in-one FFXIV market board plugin for [Dalamud](https://github.com/goatcorp/Dalamud).

Open with `/markethelp`, `/market`, `/mh`, or `/undercut`.

## Features

### Undercut
Stand near a summoning bell and press **Run**. Market Helper walks every retainer on the bell and undercuts each listed item to the lowest price.
- HQ/NQ handling and own-retainer protection
- Per-item price overrides and price memory within a run
- Skips mannequin / display items (detected by the game's mannequin icon) so they're never touched or delisted

### Lister
Build a list of items — hold **CTRL** and hover an item to quick-add it, or search by name. Press **Go** and it walks the bell and lists them all.
- Prices each item at the most common market price (ignoring lone troll/outlier listings), minus your undercut
- Price against your world, data center, or whole region (via Universalis)
- Handles the 20-item-per-retainer cap by rolling extra items to the next retainer
- No-touch **dry run** previews exactly what it would list, and at what price, before touching the market

### Flipper
Search any marketable item and compare prices across Japan, America, Europe and Materia via [Universalis](https://universalis.app).
- Top 10 listings per region
- Best cross-region flip (cheapest buy vs dearest sell)
- Seller/buyer tax modeling

## Installation

Add this URL to Dalamud's custom plugin repositories (`/xlsettings` → Experimental → Custom Plugin Repositories):

```
https://raw.githubusercontent.com/dexcss/MarketHelper/main/repo.json
```

Then find **Market Helper** in the plugin installer.

## Credits
Author: Akari (dexcss). Built on [ECommons](https://github.com/NightmareXIV/ECommons). Market data from [Universalis](https://universalis.app).

## Disclaimer
Automating retainer and market board actions may carry risk. Use at your own discretion. Always run the Lister's **dry run** first to confirm items and prices before listing for real.
