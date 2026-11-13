# Autonomous plan: remaining Phase 1–3 work (from docs/DESIGN.md)

## Context
You asked me to take your original ideas (the feature table, roadmap and settled decisions in `docs/DESIGN.md`), drop what we already ruled out, and work through the rest of Phases 1–3 without stopping or asking. Every step is tested and pushed, with a list of changes for you at the end. Check-ins: every 30 minutes isn't allowed, because the scheduler's minimum is hourly, so I'll schedule an hourly check-in that resumes the work if it has stopped.

**Already done:**
- the economy and colony core;
- trade;
- seasons;
- walls, towers and gatehouses;
- hosting: single-player, host-and-play, dedicated server.

**Ruled out earlier:**
- official or Realms hosting (option 4);
- harsh winter and harsh disasters (you chose mild; maybe a later per-world option).

**Out of scope or blocked here:**
- PostgreSQL: file saves already give versioned persistence.
- Steam relay: needs the Steam SDK and an account.
- Discord/email alerts: need credentials, and the roadmap puts them in Phase 4.

The plan is also copied to `docs/AUTONOMOUS_PLAN.md` with a progress log, so the scheduled check-in can resume from it.

## Working rules (each step)
- **Engine-free core:** the simulation stays engine-free and deterministic. Every change goes through `ICommand`s in `CommandCodec`, saved by `SaveGame` and hashed. Durations are in game days, balance lives in JSON.
- **Checks:** `dotnet test`, the 720-day runs (seeds 42, 7, 1234), the client build, and xvfb screenshots for UI.
- **Docs:** update `DESIGN.md` and the README.
- **Git:** commit, push to `claude/jolly-hamilton-lt4dvk`, open a PR. I merge it myself when green, as we've been doing, so each step lands on `main` and the next starts clean.

## Order of work
0. **Merge PR #13** (towers and gatehouses).
1. **Military economy (Phase 3a)**
   - New goods and chains in `buildings.json` and `resources.json`:
     - Weaponsmith: iron ore + wood → weapons.
     - Bowyer: wood → bows.
     - Armourer: iron ore + cloth → armour.
   - **Barracks:** a building where squads train; its training slots allow recruiting.
   - Governor hints for these buildings.
2. **Armies from townsfolk (Phase 3)**
   - `Army` and `Squad` state on the settlement. A squad has a unit type from `units.json`: militia (no gear), spearmen (weapons), archers (bows), men-at-arms (weapons + armour). Its soldiers are citizen ids.
   - `RecruitSquadCommand` takes adults out of their jobs (`Citizen.SquadId`, `CanWork` false) and uses gear from stock. `DisbandSquadCommand` returns people and gear.
   - Soldiers keep eating and pay no tax. Squads cost upkeep in coin.
   - Mood: "Serving in the army".
   - Rules: recruit quotas, and muster % for call to arms.
3. **Generals**
   - `PromoteGeneralCommand` makes a citizen a general with traits from data (siege expert, cavalry commander, tactician, inspiring) and gives the army a general.
   - XP from battles raises skill.
4. **Aggregate battle resolver** (`Sim.Core/War/BattleResolver.cs`)
   - Rounds of combat between two sides of blocks: front, archers, flanks.
   - Odds come from troop counts, equipment, general skill and traits, terrain, morale and fortifications. Fortifications are wall and tower defenders, using `DefenderCapacity`, towers and walls.
   - Casualties remove specific citizens: the dead stay dead and death events are logged. Morale breaks cause a rout.
   - Deterministic through `world.Rng`.
   - Tests: a 2:1 numbers advantage wins at a predictable rate; walls help defenders; equipment matters.
5. **Threats and the offline call to arms**
   - Bandit warbands (NPC raiders) appear on the map outside a town's land every so often, scaled to its wealth and size, and march towards it at a set pace. They're real map entities, like caravans.
   - Alert levels (`Settlement.Alert`):
     - **Watch:** spotted within the watch radius. Towers extend that radius. A feed event, no cost.
     - **Muster:** close. Garrison squads plus muster % of townsfolk arm as militia and stop work.
     - **Call to arms:** adjacent. Everyone eligible is called up.
   - Defenders man the walls. When the raiders arrive they fight a battle; if the town loses, the raiders loot stock and leave.
   - **Stand down** after the threat leaves.
   - Rule toggle `Raids` (on by default, mild).
   - The governor recruits a garrison, sized to the threat and its population.
6. **Fog of war and scouting**
   - Armies seen far away give a rough size ("about 30"). Nearer shows the unit breakdown, adjacent shows exact numbers.
   - Watchtowers and towers extend sight.
   - Enforced in what the client is told (helper `Intel.Estimate`), and shown in the UI.
7. **Client for war**
   - Military panel (M): squads, recruit and disband, general, gear stock, alert level.
   - Soldiers drawn as squads of small figures (MultiMesh); warbands marching on the map.
   - Battle reports in the feed and ledger. An alert banner on the HUD.
8. **Phase 1 depth leftovers**
   - Hygiene need: bathhouse (water and wood).
   - Healer: herbalist's hut, herbs; it shortens sickness and cuts disease chance.
   - Law and crime: theft and unrest from unhappy or poor people. A guardhouse service reduces crime. Crime is logged and hurts stability and mood.
9. **Phase 2 leftovers**
   - Simple accounts on the dedicated server: name plus password, with a salted hash stored server-side. Joining checks it, so public servers aren't name-trust only.
   - Interest management: send only nearby settlements' detail. Only if time allows.
10. **Then:** a balance pass and polish, repeating the checks, until you're back.
11. **Household economy (added mid-run at your request; done after item 7, before 8–9)**
    - **Wages:** every family earns coin from work. A job pays by its skill tier (labourer < craftsman < master), so
      workshops making dearer goods pay more. Taxes become a share of wages.
    - **Family purses:** each household keeps coin. Food, firewood, clothing, furniture and ale are bought at the
      market from the town's stock at set prices; the takings go to the producers (the town fund for now). A family
      that can't afford a need goes without, so classes come from income, not just home tier.
    - **Soldiers:** gear quality follows what a recruit's family can afford (militia, spearman, men-at-arms tiers),
      on top of experience. Soldiers are paid a salary by gear tier, from the treasury.
    - **Unemployment as gameplay:** jobless adults can take city work (builders and haulers) paid from the town fund.
      Construction gets a labour cost in coin on top of materials.
    - **Governments:** a per-town policy that sets how wages, prices and the fund work: Free Traders (market wages,
      low tax) first, with others such as a commune (shared stores, flat pay), a welfare state (high tax, support for
      the jobless) and a martial state (soldiers first) behind it.
    - **Performance budget:** money moves once per game day per household (not per frame or tick), in list order,
      with aggregate per-town prices. That's O(households) per day: thousands of NPCs across many towns cost well
      under a millisecond per day on the server, and the client only renders. A benchmark test with 5,000 citizens
      guards it, and the feature ships only if it stays inside budget.

## Verification (each step)
- `dotnet test` all green, with new tests per feature.
- 720-day checks on seeds 42, 7 and 1234: populations, treasuries, no starvation or stranding; raids survivable.
- `dotnet build client/MedievalClient.csproj`; screenshots of new UI and models.
- `DESIGN.md` and README updated, PR opened and merged, and the progress log in `docs/AUTONOMOUS_PLAN.md` updated.

## Progress log
- [x] 0. PR #13 (towers and gatehouses) merged.
- [x] 1–5. Military economy, squads from townsfolk, generals, the aggregate battle resolver, raids with Watch/Muster/Call to arms and stand-down (with rough-to-exact scouting descriptions for item 6). Balance: garrison only from spare hands, lowest job priority for military crafts, tavern gaps by nearby fullness, brewery ratio, off-season food sales. 720 days: seeds 42/7/1234 end with 69/77/73 townhouses, coin 1444/6126/770, every raid beaten, no starvation.
- [x] 6–7. Fog of war in the client (warbands drawn only within watch radius; rough-to-exact descriptions), army panel (K), alert banner, warband and soldier rendering. PR after #14.
- [x] 11. Household economy: wages by job tier and skill, family purses, cost of living, going without, relief, income tax, city wages for builders/haulers, soldiers paid by gear, gear by affordability (also in the muster), inheritance, four governments, Rules picker, 5,000-citizen performance test.
- [x] 8. Herbalist (healing), bathhouse (hygiene), guardhouse (law) and crime (thefts by the troubled, from the richest unguarded homes), crime rule; governor builds a few of each.
- [x] 9. Server accounts: salted PBKDF2 keys in a file beside the world, challenge-response joins (the password never crosses the network), lockout after 5 failures, `--accounts`, `--reset-password`, password field in the start menu. Interest management skipped: lockstep clients need the whole world.
- [x] 10a. Polish: families pay the coin part of their own home upgrades; the ledger shows households (savings by class, going without, relief, thefts) and family spending; army panel tooltips show the savings gear needs. 720 days: 48/74/66 townhouses, treasuries 2.8k/8.2k/2.1k.
