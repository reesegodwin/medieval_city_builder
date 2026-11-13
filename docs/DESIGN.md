# Medieval Map Game — Design & Development Plan

## Overview
A persistent, multiplayer medieval RTS on a dedicated server. It looks like classic isometric castle-builders (walls, towers, farms, townsfolk) but plays out over weeks: a procedurally generated map that expands as players explore, NPC factions that do diplomacy, trade and shipping, sieges that last days, squad-based battles, subjugation instead of elimination, and alerts sent outside the game. The colony simulation takes its depth from games like Dwarf Fortress and Songs of Syx.

## Verdict: Is it possible and reasonable?
**It's possible. As one finished game it's very large, but a lot of your design choices make it easier than it looks.**

Design choices that make it **easier** than a normal RTS:
- **Battles are calculated, not simulated unit by unit.** You don't need per-soldier pathfinding, collision or lockstep networking. That removes the hardest technical problem in RTS games.
- **Slow pace (weeks and days).** The server can tick the economy every few seconds or minutes instead of 30 times a second, so one server can run a big world cheaply.
- **Formations move as one block.** On the server an army is one object with numbers attached. Individual soldier models only exist on the client, for show.
- **The server simulates, the client only draws.** This is the standard "authoritative server" model. The client only renders what's in view, which is exactly what you described.

The **hard parts**, ranked by risk:
1. **Scope.** Each bullet in your list is weeks to months of work. You'll need a staged roadmap (below), or it won't ship.
2. **Players in different time zones over days-long sieges.** You'll need defender AI, siege windows or "attack notice" timers, and protection for new players. Otherwise people log in to find they've already lost.
3. **NPC faction AI** (economy, diplomacy, war) that feels smart, runs cheaply, and doesn't snowball.
4. **Balance and stability:** empire instability, civil war and subjugation tax all feed back into each other.
5. **Art.** "Looks like Stronghold" means a lot of buildings and animated townsfolk. Low-poly helps a lot. Don't copy Stronghold's actual assets or look 1:1. Aim for your own style in the same genre.
6. **Running it:** hosting, backups, save migrations when you patch, and cheating. Cheating is mostly solved by the server being authoritative.

## Recommended Platform
**Client: Godot 4 with C#. Server: a standalone .NET (C#) program, not running the engine.** Both share one plain C# library that holds the game rules.

Why:
- **Shared simulation code.** The same C# rules (economy formulas, battle odds, tech tree) run on the server and in the client for previews and tooltips.
- **The server needs no engine.** A plain .NET console or service is lighter, easier to test, and easy to run on a Linux VPS. The engine is just the visual layer.
- **Godot is free and open-source**, with no runtime fees or licensing surprises for a long-lived online game. It handles low-poly 3D with a fixed isometric camera well, and has built-in MultiMesh for drawing thousands of small soldiers cheaply.
- C# is a mainstream language with good tooling. Both an AI assistant and you can be productive in it quickly.

When to choose **Unity** instead: if you want the large asset store (e.g. ready-made low-poly medieval packs) and are fine with Unity's licensing. The architecture below stays the same; only the client changes.

Not recommended: Unreal (too heavy for stylized low-poly, and C++ slows iteration), or building your own engine.

## Architecture

```
[Godot client] <--WebSocket/ENet--> [Game Server (.NET)] <--> [PostgreSQL]
                                         |
                                         +--> [Notifier service] --> Discord / Email
```

**Shared library `Sim.Core` (plain C#, no engine references)**
- Data model: World > Regions/Chunks > Settlements > Buildings, Population, Stockpiles; Factions; Armies; Fleets; Trade routes.
- **Two-speed tick:**
  - *Economy tick* (about every 10–60 seconds): production chains, food, growth, taxes, building progress.
  - *Event tick* (about every 1 second): army movement, battles in progress, siege progress.
- Deterministic and seedable, so it can be unit-tested and replayed.

**Game server**
- Runs `Sim.Core` continuously, keeps the world in memory, and saves snapshots plus an event log to PostgreSQL (e.g. a snapshot every 5 minutes plus a log of every command). This gives crash recovery.
- **Interest management:** each client subscribes to the chunks its camera covers and gets only those updates.
- Players send *commands* ("build X here", "set rule: keep 200 grain in reserve", "march army to Y"). The server checks them and applies them. The client never decides what happens.

**Godot client**
- Fixed-angle isometric 3D camera, low-poly models, and MultiMesh crowds.
- **Real citizens, cosmetic walking:** every townsperson is a real server record (see Colony Simulation below). The client animates them walking, hauling and working, based on their current job and workplace. The same goes for armies: the server sends squad state, and the client places and animates the soldiers.

**Procedural world:** a seed generates the heightmap (noise), rivers and coasts, biomes and resource deposits (iron, stone, fertile land), and starting spots for NPC factions. Store the seed plus any changes, not every tile.

## Mapping your features to systems

| Feature | Approach |
|---|---|
| Tasks & rules for settlements | A per-settlement rule set (reserves, production priorities, recruit quotas, trade permissions) checked every economy tick |
| Resources & refining | Data-driven production chains (grain→flour→bread, iron ore→iron→weapons) defined in JSON/YAML |
| Build times (minutes→days) | Each building type has a build duration; big facilities need deliveries of materials over time |
| Trade routes & ports | Graph pathfinding between settlements (land roads plus sea lanes); caravans and ships are moving objects that can be intercepted |
| Blockades | A fleet sitting on a sea-lane node cuts or taxes routes through it |
| NPC factions | **Utility AI** at strategic level (evaluated every few minutes): expand, build, trade, ally, war. Personality traits per faction |
| Diplomacy with delays | Proposals are stored with an expiry time; auto-declined when time runs out; a notification is sent |
| Instability / civil war | Stability = f(size, distance from capital, culture, tax, recent wars, general loyalty). Below a threshold, rebel factions form from your own provinces |
| Subjugation | Defeated faction becomes a vassal: tribute %, limits on diplomacy, can rebel. Small factions get an efficiency bonus and no corruption |
| Armies from townsfolk | Recruiting moves people from the workforce into the army, which costs the economy. Dead stay dead, so population is the real currency |
| Formation battles | Battle resolver: front/archers/flanks as blocks; rounds of combat with odds based on troop counts, equipment tech, general skill, terrain, morale, fortifications |
| Generals gain skill | XP from battles; traits (siege expert, cavalry commander) |
| Hidden enemy details | Fog of war plus scouting: far = rough size estimate, near = unit breakdown, adjacent = exact numbers |
| Sieges | A siege is a long-running state: encirclement → supplies drained → engines built (ram, trebuchet take hours or days) → assault resolves with the battle resolver plus wall/tower modifiers |
| Tech tree | Data-driven nodes that unlock buildings, gear tiers and bonuses; research uses resources and time |
| Notifications | **Discord** (bot DM or webhook: easiest and best) and **email** (Postmark/SES). *Steam* can't send arbitrary push messages to players who are offline; you'd need a mobile app for true push. Start with Discord plus email |

## Phased Roadmap
Each phase ends with something you can play.

1. **Phase 0, Prototype the simulation (no graphics):** `Sim.Core` with one settlement, 4 resources, 5 buildings, the economy tick, and a console printout. Unit tests for production chains. *(Done.)*
2. **Phase 1, Visual settlement:** Godot client that renders a procedural map chunk, lets you place buildings and shows animated workers. *(In progress: client done; roads, demolition, services and food variety added in Phase 1b.)* Decision: the simulation runs in-process inside the client through `SimSession` until Phase 2, which swaps it for a connection to the dedicated server without changing the renderers.
3. **Phase 2, Persistence & multiplayer:** PostgreSQL saves, accounts, several players, interest management, running on a cheap Linux VPS.
4. **Phase 3, War:** armies from population, the formation battle resolver, generals, fog of war and scouting. *(In progress: military economy, squads, generals, the aggregate resolver, raids and the call to arms are in; see "War (Phase 3a)".)*
5. **Phase 4, Sieges & walls:** walls and towers, siege engines, multi-day sieges, Discord/email alerts. *(In progress: walls, towers, gatehouses and bandit sieges with rams, catapults and trebuchets are in; see "Sieges".)*
6. **Phase 5, NPC factions & diplomacy:** utility AI, proposals with expiry, vassals and tribute.
7. **Phase 6, Trade & sea:** roads, caravans, ports, ships, blockades.
8. **Phase 7, Depth:** tech tree, instability and civil war, balance passes, content and art.

Rough guess for one experienced developer: a playable Phase 3 in 6–12 months, and the full list in 2–4+ years. A small team or bought art speeds this up a lot.

## Design decisions (settled)

### World length: lasts forever, with an optional soft reset ("New Age")
- The world never wipes. The server must support **versioned save migrations** from day one, because every patch has to load old saves.
- **Soft reset = per-house "New Age".** A player can choose to end their current house's era. Their holdings become ruins or independent NPC towns. Their lineage starts again in a freshly generated frontier region, "centuries later", and keeps **Legacy perks** earned from its achievements (e.g. +5% farm yield, a starting general trait, a unique banner).
- **The map grows.** New procedural regions are generated at the edges as players arrive or rebirth. This keeps space for new players without a global wipe.
- Legacy perks must be small and capped, or veterans snowball. Prefer sideways perks (variety) over raw power.

### Time scale: one balance, a per-world clock
- **Every world has the same balance; only its clock differs.** Real seconds per game day is a world setting chosen at creation (`WorldSettings`, stored in the save): *persistent* 1 day per hour (24/7 servers), *weekend* 1 per 10 min, *evening* 1 per minute (village to castle in an evening or two), *sprint* 1 per 20 s.
- At the persistent speed: 24 game days per real day, and a 360-day game year is 15 real days. The examples below use that speed.
- **Speed control** (a world setting): *Fixed* (dedicated servers), *Host decides* (host-and-play), or *Everyone agrees* (the world goes faster only if every connected player asks; the slowest request wins, like Total War battles). Players can't speed-run a shared world on their own. Single-player worlds can pause and go up to 64×.
- **When nobody plays** (a world setting): *Pause* (Minecraft-style), *Catch up* (time stops, then the world simulates the missed time, capped at 7 game days by default, when someone returns), or *Run* (24/7; a restarted server also catches up on its downtime). Catch-up uses UTC wall-clock time saved with the world. A clock that went backwards owes nothing, and on a server only the server's clock counts.
- Future war features need a minimum warning time in *real* minutes on top of their game-day durations, so fast worlds can't overrun a player who stepped away.
- Write all durations in **game days** (a farm cycle, building times, aging, siege engines), so tuning the time scale rescales everything consistently.
- Examples: a starter hut takes 0.1 day (6 min). A refinery takes 48 days (2 real days). A battering ram takes 12 days (12 h). A full siege takes 50–100 days (2–4 real days).
- Aging: a general aged 25→60 lives about 1.5 real years, so generals and lineage succession actually matter.
- Optional seasons (spring planting, autumn harvest, winter supply strain) give rhythm without a day/night cycle. One season is about 4 real days.

### Offline defense: automatic call to arms
- **Threat detection.** Each economy/event tick, a settlement checks for hostile armies within its watch radius (watchtowers extend it).
- **Graduated alert levels.** This stops enemies from parking an army nearby to freeze your economy forever.
  1. *Watch:* hostile army spotted at range. Send a notification. No economic cost.
  2. *Muster:* hostile army close, or heading this way. Garrison plus a % of townsfolk (set by player rule) arm themselves.
  3. *Call to arms:* army adjacent or siege begun. Everyone eligible is mobilized.
  Mobilized townsfolk stop working, so a long siege also starves the economy. Food reserves matter.
- **Placement.** Archers/crossbows go to walls and towers. Melee troops go behind gates and wall breaches. Militia go to the keep. These map to battle-resolver modifiers (height, cover, choke points).
- **Player rules hook into this** (the existing settlement rule system): muster %, which units to keep working, whether to sally out, and surrender terms.
- **Stand down:** when the threat leaves the radius for N hours, townsfolk return to work.
- Siege engines take game-days to build, and a Discord/email alert goes out at the *Watch* level. Together these give an offline player real hours to react.

## Hosting and multiplayer (Phase 2 started)
Grows the way Minecraft does, from single-player up to a 24/7 server, with the same world file throughout:
1. **Single-player:** the simulation runs inside the game (`SimSession` local mode).
2. **Host and play:** the game runs a `WorldHost` and TCP listener in the background and joins it. Friends connect with the host's address and port. The world is saved to the host's user folder and can be continued later. Planned: Steam's free relay networking so nobody needs to forward ports.
3. **Dedicated server:** `src/Sim.Server` (`medieval-server`) is a headless Linux/Windows program that loads or creates a world file, autosaves, and saves on SIGINT/SIGTERM. A single-player or hosted world file can be moved onto it.
4. *Later, maybe:* official or "Realms"-style hosted worlds, paid for by subscription or through a hosting partner.

**Protocol (`src/Sim.Net`, engine-free):** JSON lines over TCP. The server is the authority. A joining player gets a snapshot (the save format), then a stream of "advance to tick T" and "apply command C at tick T" messages. Every client runs the same deterministic simulation, so they stay identical with very little traffic; a daily state hash detects drift and triggers a fresh snapshot. Commands carry a settlement id, and the server only accepts them from that settlement's owner (`Settlement.OwnerId`, the player name). New players get a settlement founded on a ring of spots 260 tiles apart (`FoundSettlementCommand`); the first player into a single-player world takes over its town.

**Accounts (`Sim.Net/Accounts.cs`, protocol v2):**
- Dedicated servers keep player accounts in a file beside the world, so they never reach clients in snapshots.
- The server stores PBKDF2-SHA256(password, salt) at 100,000 iterations.
- **Joining:** after `hello` the server sends the salt and a random nonce (`auth`). The client derives the key and answers with HMAC-SHA256(key, nonce). On a name's first join it sends the key itself to register.
- The password never leaves the player's machine.
- Five failures lock a name for a minute.
- `--accounts off` restores name trust. Host-and-play sessions don't use accounts (friends only).
- **Still to do:** TLS for the connection (the first, registering join can be observed).

**Not yet:** Steam relay, interaction between towns, and PostgreSQL persistence. For public servers, run on a separate rented machine, never a home network.

## Trade (Phase 2)
One mechanism covers barter, coin and routes. Tuning is in `trade.json`; fair values are `value` in `resources.json`.
- **Coin** is a normal resource that caravans carry. It's kept in the treasury, so it doesn't fill stockpiles. Towns start with 50.
- **Trading Post** (`trade: true` in `buildings.json`, one trader job): needed to post or accept offers and routes. Caravans leave from its door.
- **Offers:** "give X, want Y". Selling is goods for coin, buying is coin for goods, barter is goods for goods. The *give* side is set aside when posted and returns to stock on cancel or after 10 game days. A town has at most 8 open offers.
- **Fair price:** sellers get `value × amount`; buyers pay that × 1.25 (the merchant markup). The trade panel's **Fair price** button fills these in, and a price factor raises them for goods you'd rather keep.
- **Accepting is instant in the UI, but goods travel, and only by road:** both sides' goods leave at once as caravans along the road network between the two Trading Posts, arriving after path length ÷ 40 tiles per game day (at least 1 day). With no road connecting them, offers can't be accepted and route rounds are skipped. Caravans follow any finished road, whoever built it, and are simulation state that can later be intercepted in war.
- **Trade routes:** A proposes "every N days I send X, you send Y". Once B accepts, each round sends caravans both ways, or is skipped and logged if either side is short. Either side can end it.
- **Merchants:** every 10 game days a merchant caravan visits each town with a staffed Trading Post on a road. It travels cross-country from about 120 tiles beyond the town's land to the nearest point of the road network joined to the Trading Post, then along those roads. It buys the town's sale offers priced at or below the fair price (purse 300 coin) and fills its buy offers for goods it carries (tools, cloth, ore, stone, wood, grain, wool) at or above the fair price plus markup (cargo 200). This makes trade work in single-player and gives towns without iron a way to get tools.
- **AI towns** (Auto on): build a Trading Post at 80 people. Daily they accept one other town's fair offer for something they're short of, accept routes that trade surpluses for shortages, list surpluses (90% of the stock limit or more) at the fair price, and post buy offers for shortages they can afford. They keep one offer per good, so they never buy what they're selling.
- **AI roads to partners:** a governed town with a Trading Post plans a road towards the nearest town with a Trading Post that its roads don't reach yet, ending at that town's roads or its territory edge. The other town does the same, so they meet; two seed-42 towns are connected by about day 150. Retried every 10 days.

## Housing tiers and classes
- **Ladder** (`home` in `buildings.json`): Hovel (5 people, peasants), then Cottage (6, craftsmen), then Townhouse (8, burghers), same 2×2 footprint, upgraded in place. Only hovels can be placed.
- **Up:** when every resident has had the next tier's needs for 5 days in a row, the home pays the upgrade cost and changes tier. Cottage needs water, market and faith; costs wood 10, stone 4. Townhouse adds clothing, furniture and drink; costs wood 10, stone 12, furniture 2, coin 20. Upgrades leave at least 40 wood and 20 stone, and at most 4 happen per day. A rule (`AutoUpgradeHomes`, on by default) turns them off.
- **Down:** after 15 days of a tier's own needs going unmet, the home slips back a tier, and residents beyond the smaller capacity move out.
- **Classes** (`classes.json`): taxes per adult per day are peasants 0.05, craftsmen 0.07, burghers 0.10. Better homes also give mood (+2, +4).
- Result (720 days): most homes become townhouses and most people burghers; populations 742/806/738.

## Road surfaces
- `roads.json`: dirt (free; walk cost 0.4; caravans 1×; 0.05 roadworker-days), gravel (1 stone per tile; 0.3; 1.4×; 0.1), stone (2 stone; 0.22; 1.8×; 0.2). Bridges stay plain.
- Resurfacing is planned like a road (materials up front, refunded if the plan is dropped) and built by roadworkers. Better surfaces mean cheaper walking, which raises hauling efficiency. Caravans prefer and travel faster on them.
- Governed towns with stone to spare (75% of the limit or more, keeping half) resurface roads within 12 tiles of stockpiles, 25 tiles at a time: gravel, or stone from 300 people. By day 720 that's 450–570 stone tiles.

## Treasury and coin sinks
(Taxes are now income tax on wages; see "Household economy".)
- **Income:** class taxes (see Housing tiers and classes), plus trade.
- **Upkeep** (`upkeep` in `buildings.json`, coin per day): markets 0.4, taverns and chapels 0.3, workshops 0.3–0.5, mine 0.5, Trading Post 1, Town Hall 2, Castle 4. If the treasury can't pay, buildings with upkeep run at `unpaidOutput` (half) until it can. Debt never exceeds a day's upkeep.
- **Coin costs:** Town Hall 150, Castle 600, Smithy and Carpenter 15 each (taken like any construction material). Basic services cost no coin, so early towns aren't starved of them.
- Result over 720 days (three seeds, with housing classes): treasuries end around 5,000–11,000 coin, taxes roughly match upkeep, and upkeep goes unpaid at most once, briefly, in the early game.

## Town ledger
- Each settlement keeps books (`Economy/Ledger.cs`): every change to stock or coin is recorded by good under a category key. The keys are `made`, `inputs`, `eaten`, `used`, `building`, `trade-in`, `trade-out`, `offers`, `refunds`, `taxes:<class>`, `upkeep:<building>` and `disaster:<what>`.
- **Books:** today plus the last 30 days. They are saved (save v10) and hashed, so a joining player sees the same history.
- **The books never change stock themselves.** A test checks that they exactly explain every stock change over 30 days. Forecasts read them (see Shortage warnings).
- **Ledger screen (L or the HUD button):**
  - **Overview:** people by class, homes, adults working, on standby, hauling or idle, needs met, and the top complaints (negative mood factors added up).
  - **Goods:** stock, limit, made, used, traded and net per day (7-day average), plus "runs out in N days". Click a good for 30-day charts of its stock and of what was made, used and traded each day, plus its full breakdown by key.
  - **Treasury:** 30-day charts of the treasury and of daily income and spending, then income and spending by source, and the daily balance.
  - **Work:** workers and slots for each job, standby workers, and problems.
- The console summary prints the same goods and coin tables, plus a count of disasters and shortage warnings.
- The charts are the client's first custom drawing (`Ui/LineChart.cs`). Stock history is worked back from today's stock using each day's net flow (`Ledger.StockSeries`).

## Shortage warnings (`Economy/Forecast.cs`)
- `Forecast.DaysLeft` is stock divided by the average net loss over the last 7 days (14 for coin, which arrives in lumps when merchants visit). It's infinite when stock isn't falling, and needs at least 3 days of books.
- **Watched:** food (all foods together, weighted by nutrition), wood, stone, tools and coin.
- **Warnings:** under 10 days left logs a `Shortage` event ("Wood runs out in 8 days at the current rate") that shows in the feed. A good isn't warned about again until it recovers past 20 days. The warned goods are saved and hashed (`Settlement.ShortageWarnings`).
- **Governor:** it also wants a woodcutter, quarry or farm when that good would run out within 15 days, alongside the fixed stock checks.

## Fish
- **Fishing Hut:** 2×2 and 10 wood. It must have water within 3 tiles (the same `nearTerrain` rule as woodcutters near forest). Two fishers, 3 fish per cycle.
- **Fish:** nutrition 0.8 and meal mood +3. That's better than grain (−6) or vegetables (+2) and below bread (+6). A third or fourth food adds to the existing variety bonus.
- **Governor:** builds fishing huts from population 25, one per 80 people, and counts them in its food estimate.

## Seasons (`Systems/SeasonSystem.cs`, `seasons.json`)
- A year has four 90-day seasons. The look is lean winter: hard enough to plan for, not a starvation test.
- **Output by season** (farms and gardens / fishing huts):

  | | Spring | Summer | Autumn | Winter |
  |---|---|---|---|---|
  | Farms and gardens | 90% | 115% | 135% | 60% |
  | Fishing huts | 100% | 115% | 105% | 80% |

  Over a year farms make what they always did, so the governor sizes farms on the yearly average (`SeasonRules.AverageOutputOf`).
- **Storing for winter**
  - Per-citizen food limits went up so a reserve fits: 10 grain, 10 bread, 6 vegetables and 5 fish per person.
  - In summer and autumn the governor makes room for a 15-day winter reserve. It builds **granaries** (storage 1,500, 30 wood and 15 stone) from population 60, about one per 60 people, and stockpiles before that.
  - It doesn't add farms just because food falls in winter.
- **Spoilage**
  - Food beyond what granaries hold rots each day, booked as `spoiled`. Rates: grain 0.3%, vegetables 1.5%, bread 2%, fish 3%.
  - The season scales the rate: 1× in spring and autumn, 1.5× in summer, and none in winter, when stores freeze.
  - Granaries (`keepsFood`) protect up to their full storage. The simplification is that the whole granary counts as food store.
  - The governor adds a granary whenever more food is stored than the granaries hold. Governed towns therefore lose almost nothing; a player who skips granaries loses food all summer.
- **Firewood (its own good)**
  - Heating burns firewood, not timber, so winter no longer competes with builders and carpenters. (Furniture coverage had fallen to 7% one winter when heating used wood.)
  - **Firewood Hut:** 5 wood, near forest. One worker gathers 5 firewood a day.
  - **Limits:** a flat 200, or 6 per person (a winter's heating).
  - **Governor:** builds firewood huts from population 15, one per 50 people, when firewood is under half its limit in summer and autumn or running out.
  - **Sales:** firewood is only for sale in spring.
  - New towns start with 30 firewood.
- **Heating**
  - In winter each occupied home burns 0.3 firewood a day, booked as `heating`.
  - Homes are heated in list order until the firewood runs out. The rest are `Cold`, and their people get −12 mood ("Cold"). People without a home are cold all winter.
- **Autumn food sales:** in autumn a town may sell food above 1.25× its winter reserve (`SeasonSystem.SellableFood`), split across foods by stock. This is on top of the usual sale of anything near its stock limit.
- **Announcements:** each season start is logged as a `Season` event, e.g. "Winter has come: farms yield 60%, fishing 80%, and every home burns firewood".
- **Saves:** save v12 stores the cold homes and the part-unit of firewood owed; v13 adds the part-units of food rotting. Older saves get the new per-citizen limits.
- **Look (client, `SeasonLook.cs`):** one shader for terrain, trees and buildings. Snow settles on flat ground over the first days of winter and melts in early spring. Greens turn golden through autumn. The time of year is read from the game clock, so it's purely cosmetic.
- **Result over 720 days (seeds 42, 7 and 1234):**
  - Food falls slowly through each winter (from about 23 to 18 days' worth), and nobody starves.
  - No home went cold, because the governor stocks firewood.
  - With seasons alone, populations ended at 796/724/662 and treasuries at 2,970 / 9,163 / 2,550, because towns kept food for winter instead of selling it.
  - With firewood, spoilage and autumn sales, populations end at 803/732/717 and treasuries at 5,401 / 6,364 / 6,245. Nothing rots in governed towns, and no home goes cold.

## Mutton
- **Sheep Pasture** now has two shepherds and makes wool 2 and mutton 3 per cycle. Mutton is food: nutrition 1.0, meal mood +5 (bread is +6), rotting at 2.5% a day outside granaries.
- **Seasons:** pastures yield 110% in spring (lambs), 105% in summer and autumn, and 80% in winter.
- **Several products:** a workplace that makes more than one good only makes those still below their stock limit. A full wool store therefore doesn't stop the mutton, and doesn't overflow.
- **Governor**
  - Keeps one pasture per 70 people from population 40, while mutton is under half its limit.
  - Counts pastures in its food estimate.
  - The shepherd job priority is now 4, like other food jobs.
- **Limits:** flat 150, or 4 per person.
- **Result over 720 days:**
  - Populations end at 778/822/735 and treasuries at 5,465 / 8,204 / 9,694.
  - Mutton is eaten as fast as it's made (80–97 a day).
  - Food now takes fewer workers, so towns build fewer farms (seed 42: 61 instead of 79) and bakeries. Late in the run about 55–70 adults are idle on two seeds; before, that spare labour sat "on standby" at farms that had hit their stock limit. Mood is about the same (87–91).

## Odd jobs (`Systems/OddJobs.cs`, `oddjobs.json`)
- Adults who can work but have no job do odd jobs instead of sitting idle.
- **Allocation:** worked out from the citizens and buildings as they are, in list order, so nothing is saved and every client agrees:
  1. **Building sites:** helpers up to each site's free builder slots.
  2. **Roads:** one helper per 10 planned tiles, up to 6, while the town centre stands.
  3. **Gathering:** everyone else.
- **Helpers** add half an average worker's labour each, with no skill gain.
- **Gatherers** are dealt round-robin over the goods still below their limit, booked as `gathered`:
  - berries: 2 a day, not in winter;
  - firewood: 2.5 a day;
  - stones: 2 a day.
- **Berries** are a new food: nutrition 0.5, meal mood +1, rotting at 4% a day.
- **Free storage:** gatherers always leave a fifth of storage free, between 60 and 500. A first version filled a small town's only stockpile with stones; the farms couldn't store grain and the town starved.
- **Mood:** odd jobs are neutral (0), between "Idle" (−3, only when there is truly nothing to do) and "Has work" (+3).
- **Treasury fixes found while testing:**
  - Home upgrades that cost coin (townhouses, 20 each) now leave 20 days of upkeep, at least 100 coin, in the treasury.
  - The governor builds no more services with upkeep while upkeep is unpaid. Halved service capacity had looked like missing coverage, so it kept adding taverns, markets and breweries, which added more upkeep. Odd-jobbers made building faster and exposed this spiral: one seed ended with 13 coin.
- **Result over 720 days:**
  - Populations end at 796/830/771 and treasuries at 8,770 / 8,822 / 6,303.
  - Idle adults are near 0. When people are free (e.g. 41 on seed 7 at day 500) they gather berries, firewood and stones.
  - Late in the game, storage is often full, which limits what can be gathered.

## Walls and gates (`walls.json`, `Commands/Walls.cs`, `Systems/WallSystem.cs`)
- **Three kinds, drawn like roads** (click start and end, L-shaped) and built by the town-centre crew after its road work:

  | Kind | Cost per tile | Work days | Hit points | Safety mood | Gate cost |
  |---|---|---|---|---|---|
  | Palisade | 4 wood | 0.4 | 100 | +2 | 10 wood |
  | Stone wall | 6 stone | 1.2 | 400 | +4 | 20 stone, 5 wood |
  | Large stone wall | 12 stone, 2 wood | 2.5 | 1,000 | +6 | 40 stone, 10 wood |

  Odd-jobbers help. Walls are paid when planned. Drawing a better kind over a wall upgrades it in place.
- **Where they go:** inside your own territory, on any land but water, not on buildings.
- **Blocking:** a finished wall (`Tile.BlocksPassage`) stops walking, hauling, road planning and caravans in `Pathfinder.StepCost`, the one place every path goes through.
  - Gates let everyone through.
  - A wall drawn across a road becomes a gate by itself, so roads and caravans keep working.
  - Buildings can't go on walls. Roads only cross at gates.
  - Planned walls don't block until built.
- **Gates:** the gate tool turns a wall (finished or planned) into a gate for the gate's cost. On a finished wall the gate opens once built.
- **Demolishing:** a planned wall is fully refunded; a finished one gives a quarter back.
- **Safety:** one flood fill from outside the territory's bounding box. Walls (gates count as closed, being guarded), buildings and water stop it, and open ground it can't reach is enclosed.
  - A home whose doorstep is enclosed gets "Safe behind walls", the mood of the weakest wall facing outside.
  - The result is cached by wall, territory and building versions, so it's derived and identical after a load.
- **Hit points** are stored and saved per tile for the future siege phase; nothing uses them yet.
- **Towers and gatehouses (`WallPiece`: wall, gate, tower, gatehouse):** placed on a wall (towers) or a gate (gatehouses), paid up front and built by the crew. Each kind of wall has its own, set in `walls.json`:

  | Wall | Tower | Cost | Hit points | Defenders | Gatehouse | Cost | Hit points | Defenders |
  |---|---|---|---|---|---|---|---|---|
  | Palisade | Wooden watchtower | 25 wood | 300 | 3 | Timber gatehouse | 30 wood | 300 | 2 |
  | Stone wall | Stone tower | 60 stone, 10 wood | 1,500 | 6 | Stone gatehouse | 80 stone, 15 wood | 1,800 | 6 |
  | Large stone wall | Great tower | 120 stone, 20 wood | 3,500 | 10 | Great gatehouse | 160 stone, 30 wood | 4,000 | 10 |

  - **Passage:** towers block passage. Gatehouses let traffic through, caravans included.
  - **Wall-walk:** both are walkway tiles (timber platforms on a palisade) and access points to it.
  - **Defenders:** both add their defenders to `DefenderCapacity`. `WallSystem.Towers` lists tower tiles for the archers to come.
  - **Upgrades:** upgrading a wall upgrades its pieces at the new kind's price.
  - **Governor:** once a ring is fully built at its current kind, it adds a tower every 16 ring tiles and a gatehouse on every gate a road runs through, one at a time. When upgrading, it does plain walls and gates first, so the dearer pieces don't stall the batches.
  - **Saves:** v15. The piece is stored where v14 kept its gate flag, so old saves read the same.
- **Wall-walk (stone and large stone walls):** these walls are built to hold soldiers. `walkway` and `defendersPerTile` (2 and 3) are set in `walls.json`.
  - **Sizes:** the walk is about 0.8–0.95 of a tile wide, two soldiers abreast. A soldier is about 0.4 tall. Stone walls stand 1.4 high and large walls 2.2.
  - **Parapets:** crenellated on the outside, chest-high on the inside.
  - **Getting up:** gates and gatehouses carry the walk over the passage, and towers have their own stairs. Stairs also lead up from inside the town every 12 tiles.
  - **`WallSystem.WalkPath`:** finds the way along the top between wall tiles, including diagonal corners. Gates continue the walk and gaps break it.
  - **`DefenderCapacity`:** totals how many soldiers the walls hold; seed 7 ends with 212 tiles of wall-walk for 429 defenders.
  - Palisades have no walk.
- **Governor:**
  - At population 150 it lays out a ring once: the edge of the town's territory cut to its buildings' extent plus 4 tiles. Being the edge of a region it closes on itself.
  - The ring is ordered around the centre with whole-number maths, so every machine agrees. Gates go where it crosses roads, plus one every 24 tiles.
  - It's built in batches of 30 while materials are at 75% of their limit (keeping half). Upgrades go to stone at 400 people and to a large stone wall at 700.
- **Saves:** v14.
- **Result over 720 days:**
  - Each town has a closed palisade by about day 260, mostly stone by day 720, and the first large wall sections.
  - 40–47 homes are safe behind the walls (homes built later sit outside the ring).
  - No building is cut off; caravans still trade.
  - Populations end at 727/749/799.
- **Bug found while testing:** buildings stop the flood, so their own tiles always looked walled in and every home counted as safe. A home now counts by its doorstep.

## Winter life (`seasons.json`)
- **Short days:** outdoor work speed (`daylight`) is 100% in spring, 105% in summer, 95% in autumn and 80% in winter.
  - It slows building sites, road work, and workplaces marked `"outdoor": true`: woodcutters, quarries, mines and firewood huts.
  - Farms, gardens, fishing and pastures already have their own seasonal yields, so they aren't slowed twice.
- **Winter evenings**
  - In winter, people covered by a tavern get +3 mood ("Tavern on winter evenings") and by a chapel +2 (`serviceMood`).
  - Adults with neither get −3 ("Long winter nights", `longNightsMood`).
  - Taverns pour 1.5× the ale per person (`serviceUse`).
- **Look (client):** the sun eases lower, dimmer and cooler through early winter (long shadows) and back with the thaw, with a hint in late autumn (`SeasonLook.LightSun`).
- **Result over 720 days:**
  - Winter is the happiest season in governed towns (mood 92–95, against 84–93 in summer), because taverns and chapels cover nearly everyone.
  - Taverns pour 200–235 ale a day in winter.
  - No home went cold.
  - Populations end at 710/769/780 and treasuries at 5,086 / 11,790 / 3,726.

## Smokehouse
- **Smokehouse:** 3×3, 20 wood and 10 stone, two smokers. It turns 3 mutton **or** 3 fish, plus 1 firewood, into 3 smoked meat.
- **Smoked meat:** nutrition 1.1, meal mood +7 (the tastiest food), rotting at only 0.2% a day, so it keeps for months.
- **Input choices:** recipes can now list `inputChoices`, alternative input sets.
  - Production uses whichever available option's main good is most in stock.
  - When none is available, the problem reads "Needs mutton or fish".
  - Once inputs are loaded the output is the same, so nothing new is saved.
  - The governor, trade governor, data validation and recipe text all understand choices.
- **No fresh-meat reserve:** a first version kept 30 mutton and fish back for meals. Both are eaten as fast as they're made, so smokehouses never had anything to smoke. Smoking adds food value and mood, so it may compete with meals.
- **Governor:** builds one smokehouse per 150 people from population 100 (data hint), while smoked meat is under 150.
- **Limits:** a flat 300, or 6 per person.
- **Result over 720 days:**
  - Smokehouses make 50–60 smoked meat a day, all eaten.
  - They burn about 17 extra firewood a day, but no home ever went cold.
  - Populations end at 730/669/805 and treasuries at 4,962 / 11,246 / 5,796.
  - Seed 7 grows steadily (mood 92, no homelessness), just slower in this run.

## Workplace stores
- **Problem:** late in the game, storage filled completely. At day 500 on seed 42 it was 18,499 / 18,500. Farms, bakeries, fishing huts and pastures stalled with "Storage full", and gatherers had no room. Per-person stock limits (winter food, firewood, mutton) had outgrown the stockpiles and granaries.
- **Fix:** each workplace keeps some of its own products on site, outside the stockpiles.
  - **Amount:** `BuildingDef.OwnStorageCapacity` is 30 of each output by default, with `ownStorage` overrides in `buildings.json`: farm 50, garden 40, woodcutter 40, quarry 40, mine 20.
  - **How it's counted:** stock is still one town-wide inventory. Each good first fills the stores of the workplaces that make it (`Settlement.OwnStorage`), and only the rest takes general space. `FreeStorage` is general space only. `RoomFor(good)` adds that good's free workplace space.
  - **Production:** a cycle's output fits if the overflow beyond its products' own stores fits in general space.
  - **No new save data:** everything is derived from buildings and stock, so saves and multiplayer are unchanged.
  - **Rot:** food kept at farms is outside granaries, so it can rot like any other food outside them.
- **Governor:** builds storage while less than a tenth of general storage is free (it used to wait for under 80 places), and allows one granary per 40 people (was 60).
- **Result over 720 days:**
  - No workplace ends with "Storage full".
  - Stockpiles end 83–86% used, with 5,400–5,800 goods kept at workplaces.
  - Populations 760/714/777, treasuries 4,521 / 12,708 / 3,155.
  - No rot, cold homes or starvation. Seed 7 just grew a little slower in this run.

## Disasters (`Systems/DisasterSystem.cs`, `disasters.json`)
- **Mild:** nothing is lost for good except some goods in a burning stockpile. Each settlement can switch disasters off in the Rules panel (`SetDisastersCommand`). The setting is on by default and saved (save v11).
- **Fire:** each working building has a 0.01% daily chance, halved within reach of a well. A burning building drops back to half-built: builders repair it with labour only, its workers and residents leave, and its work in progress is lost. A stockpile fire burns a quarter of its share of the stored goods (booked as `disaster:fire`). The town centre never burns.
- **Sickness:** from 30 people, a 1.2% daily chance scaled by crowding (people per housing place). Full water coverage halves it and faith lowers it by 30%. 2–5% of people fall sick for 5–10 days. Sick people don't work and get −10 mood. There's one outbreak at a time and nobody dies.
- **Poor harvest:** at the start of each autumn there's a 15% chance. Farms and gardens produce 30% less for 30 days.
- **Result over 720 days (seeds 42, 7 and 1234):** big towns see about 2–6 fires and 1–2 outbreaks a year. Populations end at 707/733/740 with disasters and 697/739/732 without, with no starvation.
- **Governor fix found while testing:** on seed 42, nearly all free land near the centre lay across a lake, too wide to bridge. The site search spent all its route checks there and the town stopped building homes. Now it:
  - tries sites reachable without a new bridge first;
  - skips sites next to one that couldn't be connected;
  - maps larger cut-off areas.

## Colony Simulation (Dwarf Fortress / Songs of Syx-inspired depth, Stronghold look)

**Principle:** *the people are real, their footsteps are not.* Every citizen is a full data record simulated on the server. Their moment-to-moment walking is drawn by the client and doesn't drive the simulation.

**Citizen record (cheap, around 200–500 bytes each):** name, age, house/family links, traits, skills (improve with work), current job and workplace, home, needs, mood, health/injuries, equipment, loyalty, memories/notable events (for emergent stories), and a squad ID when mobilized.

**Systems (all data-driven):**
- **Needs & mood:** food variety, housing quality, religion, entertainment (tavern), hygiene (bathhouse), safety, and family. Unmet needs → unhappiness → slower work, crime, riots, emigration, and joining rebels in civil wars.
- **Social classes:** e.g. peasants, craftsmen, nobles/clergy. Each class has different needs and services, and promotion between classes is possible.
- **Jobs & workplaces:** buildings have job slots. Citizens are assigned by your rules and priorities, and skill affects output and quality.
- **Logistics:** stockpiles/warehouses, and hauler jobs. Hauling cost is calculated from real distances in the town layout (the server's path graph). So good city planning matters without simulating every step.
- **Services & coverage:** services have a radius and a capacity (wells, markets, chapels, taverns, guards). This is the Songs of Syx-style "access" model.
- **Health & disasters:** disease spreads through crowding and poor hygiene. Fire spreads through wooden buildings. Food spoils. Bad harvests happen.
- **Law & crime:** a justice building, guards, punishments. These trade off stability against happiness.
- **Lineage & notable people:** generals, nobles and heirs come from the citizen pool. Their histories feed the faction's chronicle and Legacy perks.

**Tick frequencies (fits the 1 game day = 1 real hour scale):**
- Job assignment, production and hauling: every economy tick (about 10–60 s).
- Needs, mood and health: a few times per game day.
- Life events (births, deaths, marriages, aging, promotions): once per game day.
- NPC settlements run the **same** system. Settlements that are far away and unwatched can update less often, but with the same rules and outcomes.

**Scale budget:** Songs of Syx runs tens of thousands of citizens on one PC. With low-frequency ticks and compact data (ECS), a server can hold roughly **100–300k citizens world-wide**. Suggested soft cap: a few hundred people per village, up to ~5,000 per large city, with soft limits through stability and corruption.

**Client view:** the Stronghold-style isometric town shows citizens walking between home, workplace and stockpile with carried goods. Click anyone to see their name, mood, needs, skills and history (DF-style inspection). A town ledger shows needs coverage, production chains and complaints.

**Roadmap change:** the colony sim *is* the core game, so Phase 0–1 expands into needs, jobs, hauling, services and an inspection UI for one settlement. This is the "is it fun?" milestone, before multiplayer.

## Phase 1b decisions (roads, demolition, services)
- **Dirt roads are free but take labour.** A planned road is pegged out at once. Roadworkers based at the town centre (Keep 4, Town Hall 6, Castle 8 jobs, about one per 10 planned tiles) build it in order: 0.05 roadworker-days per tile, or 0.5 for a bridge, slowed by distance from the centre (half speed at 60 tiles). Bridges also cost 4 wood per tile up front. Roads go on grass or fertile land (bridges over water), may extend up to 600 tiles from the centre on unclaimed land, but never into another settlement's territory. A new town starts with its first roads built. Placing a building over a road replaces it. People walking pay 0.4 per road tile, 1 on open ground, 1.6 in forest and 2 on rock; water and buildings block them.
- **Logistics use real walking cost.** A multi-source Dijkstra from stockpile entrances sets each building's hauling efficiency (`1/(1+cost/40)`). Buildings with no route get 30% and a warning. The governor only builds where people can walk, so there are no farms across rivers until bridges exist. **Hauling is a job:** each stockpile has 10 hauler slots (lowest job priority). The town wants one hauler per worker-day that workplaces lose to carrying, `workers × (1 − haul factor)`. Haulers win back that share of the lost time (`effective = factor + (1 − factor) × coverage`). Unreachable buildings get no help.
- **Standby and work-aware immigration:** a workplace at its stock limit keeps its workers on standby instead of releasing them. They don't produce, but any other job (except another paused workplace) can take them, or a hauler, at once. Newcomers stay away while more than 25% of adults (and at least 4) have no job at all. This brought idle adults from about 20% to about 5% at the same population.
- **Demolition refunds** nothing while awaiting materials (nothing was spent), half while under construction, and a quarter once finished.
- **Service coverage model:** each need (water, faith, drink, market) is met by the nearest staffed service building within its radius (Manhattan distance from the home) that has spare capacity. It's recalculated daily. Taverns use ale per person served. A need's "missing" penalty only applies from a minimum population, so small villages aren't punished.
- **Food variety:** +3 mood per distinct food eaten in the last 3 days beyond the first (max +6). Without a market people always eat the tastiest food; with a market they shop around.
- **Terrain shape is cosmetic:** the client draws the land as a low-poly heightfield (heights on tile corners, blended between neighbouring tiles), so riverbanks slope and rock forms hills. Buildable ground stays nearly flat, and buildings get an earthen footing. The territory border is smoothed with marching squares. The simulation still works on tiles.
- **Walking stays cosmetic:** the client draws townsfolk following roads using the same pathfinder, but the simulation never depends on their drawn position.
- **Sound:** all audio is synthesized in code (no third-party files), so there are no licences to track. Any future internet asset must have its licence verified at the source and be recorded in `CREDITS.md`.
- **Bridges and territory (Phase 1c):** roads over water become bridges (4 wood per tile, up to 8 tiles per governor-built crossing). The governor plans each building's connecting road before placing it, so it never builds somewhere it can't reach. Planned roads stay inside the territory, as placed roads must. When a candidate site turns out to be on land that can only be reached by a bridge longer than the limit, the governor skips that whole patch of land for the rest of the day, so it doesn't use up its route checks there. It also tries sites reachable without any new bridge first, and skips sites next to one that couldn't be connected (see Disasters for the case that prompted this).
- **Governor food sizing:** the governor adds farms until the estimated food supply covers 1.2 × daily need. The estimate uses recipes and nutrition from the data files, assumes 1.2 labour per worker-day, counts grain baked into bread as far as bakeries can take it, subtracts grain for breweries and includes garden vegetables. It also adds a farm whenever food falls below 3 days. Before this, each farm was counted as 6 nutrition of raw grain, so towns built about 1.7× the food they needed and whole sectors stopped at their stock limits together.
- **Territory is painted, not a radius (Phase 1d).** Territory is a set of claimed tiles (each tile records its owner, so settlements can't overlap). Buildings need every footprint tile inside it; roads need every tile inside it. The player paints it with a brush, so a town can grow along one side of a river or stop at a mountain. New land must touch existing territory, and nothing beyond 160 tiles from the centre can be claimed. Land without buildings or roads can be given back.
- **Allowance, gated by the town centre.** The tiles a settlement may hold = the area of a diamond of radius 44 + population / 8, capped by its town-centre tier: Keep 7,321 (radius 60), Town Hall 12,961 (radius 80, needs 90 people), Castle 20,201 (radius 100, needs 240 people). Tiers live in `buildings.json` (`townCentre`). Each settlement has exactly one centre. It's placed at founding, can't be built or demolished by command, and is upgraded in place (`UpgradeTownCentreCommand`) as a normal construction site. The cap rises when the new tier is finished and never drops while it's being built.
- **Auto land.** The rule `AutoExpandTerritory` (on by default, and always on for the governor) spends spare allowance each day ring by ring around the centre, which reproduces the old diamond. The governor upgrades the centre once population would claim 85% of the current cap and it can afford the materials.
- **Production chains (Phase 1e).** Iron Mine (on iron deposits) produces iron ore; Smithy turns ore and wood into tools. Sheep Pasture produces wool; Weaver turns it into cloth. Carpenter turns wood into furniture.
  - *Tools* (`workBonus`, `wearPerWorkerDay` in `resources.json`): while any are in stock, workplace and construction labour is +25%. Each worker-day wears 0.01 tools.
  - *Household goods* are needs supplied *via* another service (`via`, `goods` in `needs.json`). Clothing (0.01 cloth per person per day, from 100 people) and Furniture (0.005, from 150) reach people who have market access, as far as the stock lasts. Fractional use and wear add up until whole units are taken, so small towns pay their share too.
  - *Governor*: chain buildings carry a `governor` hint in `buildings.json` (min population, people per building, low-stock threshold). A converter is only wished for once its inputs are in stock or produced. New chains need only data.
  - Result (720 days, three seeds): clothing and furniture reach about 99% of people, and mood rises from about 80 to 85–87. Population is unchanged because growth is limited by housing.
- **Saves (version 2)** store territory as row runs. Version-1 saves load with the old diamond and the old maximum cap, and have no town centre to upgrade.
- **Saves:** versioned JSON. Only changes are stored (terrain comes from the seed). Save and load are exact: a loaded world continues with the same state hash as the original.

## War (Phase 3a) (`War/`, `units.json`)
- **Military goods.** Weaponsmith (iron ore + wood → weapons), bowyer (wood → bows) and armourer (iron ore → armour; ore only, so armour doesn't compete with clothing for cloth). These jobs have the lowest priority (1), so they never pull workers from food, ale or services.
- **Squads from townsfolk.** `RecruitSquadCommand` turns 10 adults into a squad of a unit kind from `units.json`: militia (no gear), spearmen (weapons), archers (bows), men-at-arms (weapons + armour). Gear comes out of stock and goes back when the squad is disbanded. Soldiers keep their homes and food, don't work and pay no tax. Each squad costs a little coin a day (by unit), booked as "Army" upkeep. A barracks holds 4 squads; levies (the muster) don't need one. The jobless, haulers and people on standby are called up first, and service workers last.
- **Generals.** `PromoteGeneralCommand` appoints one general per town with two random traits (tactician, inspiring, siege expert, bowmaster, stubborn, reckless). Battles give XP, and skill = 10 + 3√XP. The general leads instead of working and gets a mood bonus.
- **Battle resolver** (`War/Battle.cs`): up to 10 rounds, with an archers' opening volley. Damage comes from count × attack × veterancy, against the other side's defence. Defenders get walls (by ring tier) and towers. Losses fall mostly on the melee line. Morale drops with losses, and a side routs on broken morale or heavy losses. The fallen are specific citizens. Deterministic via `world.Rng`.
- **Raids and the call to arms** (`War/Raids.cs`):
  - From day 75, a town of 40+ people may be targeted by a bandit warband (about 1% a day). Its size scales with population and treasury.
  - The band marches in at 12 tiles a day. The alert climbs *Watch* (towers extend sight) → *Muster* (a levy of `MusterPercent`% of adults, 10% by default) → *Call to arms* (twice that), and then the battle is fought.
  - If the town loses, the raiders loot a share of coin and goods.
  - Levies stand down after 2 quiet days. A `Raids` rule turns it off per town. Battle reports are kept (last 20).
- **Scouting** (`Raids.Describe`): far bands give a rough size, nearer ones a rounded breakdown, and bands at the walls give exact numbers.
- **Client:**
  - The army panel (K) shows the alert, the general and squads, recruit and disband buttons, gear, the raid and muster rules, and recent battles.
  - An alert banner shows while raiders are about.
  - Warbands are drawn as a dark crowd under a red banner. They only appear inside the town's watch radius (fog of war, applied in the client), and slide smoothly between daily moves.
  - Soldiers wear their unit's colour (militia brown, spearmen blue, archers green, men-at-arms steel). They drill in the barracks yard and gather at the keep or castle during a muster.
  - Lockstep clients hold the full state, so the fog of war is a presentation rule for now. A server-authoritative mode would have to filter it on the server.
- **Governor:**
  - From 150 people, a governed town appoints a general and keeps a garrison of about 3% of its people (in squads, up to its barracks' room). It wishes for a barracks.
  - It recruits only from spare hands, never during a muster.
  - It picks men-at-arms when it has the gear, otherwise it alternates archers and spearmen.
- **Balance findings:** a garrison that took real workers emptied taverns and breweries, homes slipped back, taxes fell and upkeep went unpaid in a spiral. Hence the spare-hands rule, the lowest job priority, and a 10-day upkeep reserve before home upgrades. The governor also now:
  - builds another service building where a home's *nearby* providers are full, not only when every one in town is full;
  - keeps about 1.25 breweries per tavern while ale is under half its limit;
  - sells food beyond 1.5 winter reserves in any season (1.25 in autumn).

## Household economy (`Economy/Households.cs`, `households.json`)
- **Wages.** Every working adult earns coin each day, set by their job's tier and their skill at it.
  - Tiers: labourers 0.12, skilled trades such as bakers, brewers and weavers 0.18, masters such as carpenters, weaponsmiths and armourers 0.26.
  - Skill moves pay ±20%. Idle (standby) workplaces pay half.
  - Adults without a job earn a little from odd jobs. Soldiers get pay by their gear tier, and the general gets a salary.
- **Who pays wages:**
  - Producing work is paid in new coin, which stands for the value made.
  - The treasury pays city jobs (builders, haulers, roadworkers) and the army as upkeep. So construction costs coin through its builders' wages, on top of materials.
- **Income tax** is the government's share of wages. It replaces the old flat tax per class; soldiers pay none.
- **Families buy what they use.** Every day each home pays from its adults' purses, in order:
  1. food (per person; children half);
  2. warmth (per home, in winter);
  3. then the services and goods its people used: faith, market, clothing, the tavern, furniture.

  What a family can't afford, it goes without. That need counts as unmet: mood falls and homes slip back. The exception is anything the government gives to the poor for free. Spending goes to the treasury.
- **Classes come from income.** Skilled and master households afford the tavern and clothes and rise to townhouses. Labourers get by in cottages.
- **Unemployment** is the player's problem to solve: jobless adults only earn odd-job pennies.
- **Savings and inheritance:**
  - Savings pile up in well-off homes.
  - When someone dies or leaves, their savings go to their spouse, else to another adult at home, else to the town.
  - New adults (settlers, newcomers) bring 4 coin.
- **Families pay for their own home upgrades.** A family with twice the coin part of an upgrade saved pays that part itself (builders' pay), and the town only gives the materials. This is a sink for burgher savings, and it makes upgrades follow wealth.
- **Gear by what families can afford.** A recruit takes up a unit's gear only if their family has saved 40% of the gear's value. That is 3.2 coin for spearmen and 8.8 for men-at-arms, so a prosperous town fields better soldiers. At a muster, those who can afford it take up the best gear in store (men-at-arms, then spearmen, then archers), and the rest come as militia.
- **Governments** (per town, `SetGovernmentCommand`, in the Rules panel):

  | Government | Tax on wages | Pay | Prices | Free for the poor |
  |---|---|---|---|---|
  | **Free Traders** (default) | 10% | by skill | normal | food only |
  | **Common Weal** | 25% | partly evened out | normal | food, warmth, water, faith and market |
  | **Commune** | 50% | the same for everyone | ×0.75 | the same as Common Weal |
  | **Iron Rule** | 20% | by skill | normal | food |

  Under Iron Rule, soldiers also get 1.5× pay.
- **Performance:**
  - Payday is one pass over citizens and spending one pass over homes, once per game day. That's O(people), with no per-tick or per-frame work.
  - A test holds 5,000 citizens under 25 ms per game day. Real runs are far below that; a game day lasts at least 20 real seconds even on the fastest worlds.
- **Balance (720 days, Free Traders):**

  | Seed | Townhouses | Treasury |
  |---|---|---|
  | 42 | 43 | 1,236 |
  | 7 | 70 | 7,610 |
  | 1234 | 53 | 1,987 |

  Burgher homes hold about 50 coin and craftsman homes 2–12. About 20 homes go without something, mostly ale.
- **Later:**
  - shops owned by families (profits instead of minted wages);
  - prices that follow supply;
  - per-government moods and laws;
  - migration drawn to high wages.

## Health, cleanliness and the watch (`Systems/CrimeSystem.cs`, `disasters.json`)
- **Herbalist's Hut** (healing; 1 herbalist, reach 24, 150 people). People it reaches recover from sickness twice as fast, and full coverage lowers outbreaks by 20%. Mood +2, and families pay 0.004 a day.
- **Bathhouse** (hygiene; 1 bath keeper, reach 16, 60 people, burns 0.02 firewood per person). Full coverage lowers outbreaks by 40%. Mood +3, and families pay 0.006 a day.
- **Guardhouse** (law; 2 guards paid by the treasury as city workers, reach 22, 150 people). Mood +2.
- **Crime:**
  - From 100 people, troubled adults may steal: those out of the watch's reach who are unhappy (mood under 40) or whose family has under 0.5 coin saved. Each adds 0.2% to the day's chance of a theft, up to 40%.
  - A thief takes 25% (at most 20 coin) from the richest home the watch doesn't cover. If no family has a coin, they rob the treasury instead, unless a guardhouse reaches the town centre.
  - The loot goes to the thief's family, so poverty, the household economy and the watch feed into each other.
  - A theft unsettles everyone the watch doesn't reach ("Thieves about", −2 mood) for 10 days.
  - A per-town rule switches crime off (`SetCrimeCommand`, Rules panel).
- **Governor:** it builds about one of each per 150 people (the first anywhere, later ones where people aren't reached). The jobs have middling priority, so these comforts don't drain the workforce.
- **Also fixed:** after a building's site search fails, the governor skips that building's other spots for the day. A crowded old town that keeps failing to find a tavern site no longer uses up every daily search.
- 720-day checks:

  | Seed | Townhouses | Treasury | Thefts in two years |
  |---|---|---|---|
  | 42 | 37 | 2,494 | 18 |
  | 7 | 77 | 8,058 | 26 |
  | 1234 | 58 | 682 | 15 |

  No starvation, and every raid was beaten.

## Sieges (`War/Siege.cs`, `units.json` "siege")
- **When:** a warband of 20 or more reaches a town with a closed ring of walls and digs in; smaller bands, or towns without a closed ring, are attacked at once as before.
- **Engines** take game days to build, one at a time, up to 1 + size/20 (at most 4). That gives offline players real time to react.

  | Engine | Build days | Damage a day | Best against |
  |---|---|---|---|
  | Battering ram | 2 | 60 | ×3 against palisade, ×2 against gates |
  | Catapult | 5 | 45 | ×1.5 against stone walls |
  | Trebuchet | 8 | 80 | ×2 against large stone walls |

  The band builds whichever engine would bring down some wall tile soonest for its build time.
- **Battering:** built engines batter the wall tile they can bring down soonest. Only tiles with outside ground on one side and walled-in ground on the other count, so a corner is never wasted on.
- **Breach:** when a tile reaches 0 hit points it is knocked down (nothing to salvage), the ring opens and the band storms in. Everyone eligible is called up, and the walls now help less.
- **Siege length:** after 30 days the band gives up; it also gives up if fewer than 10 are left.
- **What the town can do:**
  - **Sally out** (once a day): a battle without the walls' help. A win burns every engine.
  - **Pay the band to leave:** 6 coin per raider.
  - **Hold out.**
- **Mild pressure while besieged:**
  - Work outside the walls runs at half output.
  - No trade offers, routes or caravans.
  - "Under siege" costs 3 mood.
  - The normal muster stays armed. The full call-up only comes for the assault.
- **Repairs:** afterwards, damaged walls regain 40 hit points a tile a day, paid in their own materials. Breached tiles are rebuilt by the governor's ring planner.
- **AI towns:** once the band starts on its engines, the AI sallies if it has 1.25× the band's numbers, or pays if the price is under 30% of the treasury; otherwise it holds out.
- **Client:**
  - Army panel (K): day, engines, the wall under attack, and Sally and Pay buttons.
  - A banner: "Under siege, day N: …".
  - The camp is drawn with tents, and the engines before the wall (half-built ones drawn short).
  - Battered walls show soot cracks and fallen stones.
  - `--demo-siege` sets up a siege for screenshots.
- **720 days:** each seed had several sieges: seed 42 paid off all 6; seed 7 paid off 3 of 3; seed 1234 paid off 2 of 5, with 1 breach that was beaten back. No starvation.
- **Later:** player-vs-player sieges, archers on walls shooting at engines, fire, mining, starving a town out.

## Battles: individual units + destructible structures (hybrid LOD simulation)

**Two battle simulators that share the same stats:**
- **Tactical sim (individual soldiers):** used for any battle involving a player, and for any battle a player is watching. Every soldier is a real entity: a position, HP, a weapon, and morale. Soldiers fight individually, but **AI controls them inside formation blocks** (front line, archers behind, cavalry on the flanks). The player still gives orders to the army, not to individual soldiers. This is the Total War model.
- **Aggregate resolver (the original plan):** used for NPC-vs-NPC battles nobody is watching, and as a fallback under heavy server load. It's tuned against the tactical sim so outcomes statistically match.

**Siege phases.** Only the *assault* runs the tactical sim. Encirclement, starvation and engine building stay as aggregate state, which keeps a multi-day siege cheap.

**Squad-based tactical sim (chosen compromise):**
- The simulation entity is a **squad** of about 8–16 soldiers (size is data-driven per unit type), not a single soldier. Squads move, path, collide and engage other squads.
- Inside a squad, each soldier is still tracked as a cheap record: HP, gear, and the townsperson ID. Casualties remove specific people (dead stay dead), and the squad weakens as it shrinks. Small squads merge after battle.
- About 8× fewer pathing and collision agents and network updates. This raises the budget to roughly 10–20k soldiers per battle, or lets more battles run the full tactical sim (possibly even NPC-vs-NPC battles).
- **Client shows individuals.** The 8–16 models in a squad are placed and animated by the client, with paired duel animations when two squads clash. On screen it looks like individuals fighting.
- Squads map cleanly to the aggregate resolver (sum of squads), so the two simulators stay consistent.

**Server tactical sim (plain C#, data-oriented / ECS e.g. Arch or Friflo, no engine physics):**
- 10 Hz tick, spatial-hash collision, and **flow fields** for pathfinding. One field per target is shared by all soldiers heading there, instead of A* per unit.
- Budget: about 2,000–5,000 units per battle, and around 20–40k units across all concurrent battles on one decent server.
- Per-battle troop caps, or a "1 model = N men" ratio for very large armies.
- **Fairness rule:** the simulator choice depends only on *who is involved*, never on who is watching mid-battle. A battle doesn't switch simulator once it has started.

**Destructible walls and buildings (Stronghold-style, no physics):**
- Every wall segment, tower, gate and building has HP plus discrete damage states (intact → damaged → breached/rubble), shown by swapping meshes.
- A breach updates the pathing grid locally and regenerates the affected flow fields. Attackers then pour through.
- Damage persists in the world. Repairs cost stone and time, and townsfolk do the repairs.
- Siege engines (ram, trebuchet, catapult) and fire deal structure damage. Oil and archers on walls get height and cover bonuses.

**Client:** low-poly soldiers drawn with Godot MultiMesh plus vertex-animated textures, so thousands of soldiers fit on screen. Zoomed out, they're drawn as formation blocks with banners.

**Roadmap change:** Phase 3 builds the aggregate resolver first, since everything needs it. A new **Phase 3b** adds the tactical sim. Phase 4 then adds destructible walls on top of the tactical sim.

## Verification (per phase)
- `Sim.Core` unit tests: production chains balance, battle resolver odds behave as expected (e.g. a 2:1 numbers advantage wins at a predictable rate), deterministic replay from the same seed and commands.
- Headless soak test: run the server at 100× speed for a simulated month with only NPC factions. Check it stays stable (no runaway snowball, no crashes, memory stays flat).
- Restart test: kill the server mid-siege and confirm it resumes from the snapshot plus the log.
- Two-client test: two players on the VPS, one besieges the other, and the defender gets a Discord alert.
