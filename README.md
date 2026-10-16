# Market Helper

An all-in-one FFXIV market board plugin for [Dalamud](https://github.com/goatcorp/Dalamud).

Five tabs: **Undercut**, **Lister**, **Flipper**, **Item Gatherer** and **Buyer**.

Open with `/markethelp`, `/market`, `/mh`, or `/undercut`.

## Undercut

Stand near a summoning bell and press **Run**. Market Helper walks every retainer on the bell and
undercuts each listed item to the lowest price.

- **HQ/NQ handled separately** — an HQ item is priced against HQ listings, not NQ ones
- **Own-retainer protection** — a listing that belongs to one of your retainers is matched instead of
  undercut (matched by retainer ID, because the game hides seller names on the board)
- Per-item price overrides (min / max / default) and price memory within a run
- **Price sanity checking** — a cheapest listing far below the next one up is treated as a troll/stale
  price and skipped, so the walk doesn't chase a lowball down
- **Never opens mannequin / display items** — detected from the game's own mannequin icon on each sell
  row; if a row can't be verified the walk skips it rather than risk delisting it
- Closes the bell window when it finishes
- **Reactive mode** (off by default): undercut any item as its sell window opens, even outside a run

## Lister

Build a list of items — hold **CTRL** and hover an item to quick-add it, or search by name. Press
**Go** and it walks the bell and lists them all.

- Prices each item at the **most common** market price (ignoring lone troll/outlier listings), minus
  your undercut
- Price against your world (live board), data center, or whole region (Universalis)
- Per-item **stack size** (e.g. split a stack of 18 dye into listings of 1) and per-item **list
  amount** (hold some back)
- Handles the 20-item-per-retainer cap by rolling extra items to the next retainer
- No-touch **dry run** previews exactly what it would list, and at what price, before anything is
  written to the market

## Flipper

Search any marketable item and compare prices across Japan, America, Europe and Materia via
[Universalis](https://universalis.app).

- Top 10 listings per region, with cheapest price and world
- Best cross-region flip (cheapest buy vs dearest sell)
- Seller and buyer tax modelling, with only the rates you've enabled applied

## Item Gatherer

Pull designated items off your retainers into your main inventory.

- Optionally returns your market listings to the retainer first, then retrieves them
- Stops when your bags fill, and continues on the next run
- Manual "withdraw all" / "deposit all" actions for the retainer window you already have open

## Buyer

Build a shopping list, sweep the market with Universalis, then travel and buy.

- Add items by hand (quantity, optional per-item price cap, HQ-only) or **import a MakePlace
  furniture list**
- Scope: my world · my data center · my whole region · or a custom set of data centers, with
  per-world opt-outs and an optional fixed data-center visit order
- **SCAN** queries Universalis *per world* (so deep listings aren't hidden behind a per-request row
  cap) and builds a plan you can inspect: per item, the cheapest listings, how many are available
  under your cap, the route, a live log and an audit trail
- **SEND** hops world to world with [Lifestream](https://github.com/NightmareXIV/Lifestream), opens a
  market board and buys — optionally walking the last stretch with vnavmesh
- **Dry run is on by default** — prove the route without spending a gil

### How the Buyer buys safely

- **Every purchase is verified against the game's own confirmation dialog** — the item name and the
  total must match what was intended, or it answers No and moves on
- **Listings are bought whole** (FFXIV has no partial purchases), so by default the Buyer **never
  buys a bigger stack than you still need**. "Buy oversized stacks" is opt-in for people who'd rather
  take a whole stack than skip it
- **Hard sanity ceiling**: even with no price cap set, it will not pay more than 10× the cheapest
  price the scan found for that item (configurable)
- **Chases the cheapest**: if a planned listing has been bought by someone else, it skips it, skips
  anything too big, and hunts the next-cheapest listings — including on other worlds — until your
  order is filled
- Own-retainer listings are skipped; gil reserve and an optional per-run spend cap are enforced; bags
  are checked before each purchase and the run pauses rather than buying something it can't receive
- Returns to your **home world** when finished (a different world can be set explicitly)
- Every skip and refusal is explained in plain language in the log, and each run writes an optional
  CSV audit log

## Commands

| Command | Does |
|---|---|
| `/markethelp`, `/market`, `/mh`, `/undercut` | Open the window |
| `/undercut run` / `stop` | Start / stop the undercut walk |
| `/undercut now` | Undercut the item whose sell window is open |
| `/undercut dump` / `dcdump` / `buydump` | Diagnostics: sell-list rows, world/DC resolution, market board state |
| `/undercut buytry <param> [row]` / `buylearn` | Board diagnostic helpers |

## Requirements

- [Lifestream](https://github.com/NightmareXIV/Lifestream) — needed by the Buyer to travel between
  worlds. Without it, the Buyer can only buy on the world you're standing on.
- [vnavmesh](https://github.com/awgil/ffxiv_navmesh) — optional; walks you to the market board after a
  world hop.
- [AutoRetainer](https://github.com/PunishXIV/AutoRetainer) — optional; can undercut during its
  retainer cycle.

## Installation

Add this URL to Dalamud's custom plugin repositories (`/xlsettings` → Experimental → Custom Plugin
Repositories):

```
https://raw.githubusercontent.com/dexcss/DalamudPlugins/main/repo.json
```

Then find **Market Helper** in the plugin installer.

## Credits

Author: Akari (dexcss). Built on [ECommons](https://github.com/NightmareXIV/ECommons). Market data
from [Universalis](https://universalis.app).

## Disclaimer

Automating retainer and market board actions may carry risk. Use at your own discretion. Run the
Lister's **dry run** and the Buyer's **dry run** first to confirm items and prices before letting
either one touch the market for real.
