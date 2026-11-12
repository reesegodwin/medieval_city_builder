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
2. **Phase 1, Visual settlement:** Godot client that renders a procedural map chunk, lets you place buildings and shows animated workers. *(In progress: first client done in `client/`.)* Decision: the simulation runs in-process inside the client through `SimSession` until Phase 2, which swaps it for a connection to the dedicated server without changing the renderers.
3. **Phase 2, Persistence & multiplayer:** PostgreSQL saves, accounts, several players, interest management, running on a cheap Linux VPS.
4. **Phase 3, War:** armies from population, the formation battle resolver, generals, fog of war and scouting.
5. **Phase 4, Sieges & walls:** walls and towers, siege engines, multi-day sieges, Discord/email alerts.
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

### Time scale: 1 in-game day = 1 real hour, no day/night cycle
- 24 game days per real day. A 360-day game year is 15 real days.
- `TimeScale` is a single config constant so it can be tuned later.
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
