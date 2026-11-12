# Medieval City Builder

A persistent, multiplayer medieval city-builder and RTS that runs on a dedicated server. Cities grow over weeks and sieges last days. It looks like classic isometric castle-builders, and its colony simulation takes its depth from games like Dwarf Fortress and Songs of Syx.

The full design and roadmap are in [docs/DESIGN.md](docs/DESIGN.md).

## Status: Phase 0, headless colony simulation

This phase has no graphics yet. The game rules live in a plain C# library that the future Godot client and dedicated server will share.

What works today:
- **An endless procedural world.** The map is generated in 64×64 chunks from a seed, with grassland, fertile river valleys, forest, rock, iron deposits, lakes and rivers. Chunks are created only when something looks at them, and the result doesn't depend on the order they were visited.
- **Time.** One game day is one real hour (240 ticks of 6 game minutes each), with seasons and years. Every duration is written in game days, so the time scale is one constant.
- **Citizens.** Each person is a real record: name, age, sex, family, traits, skills that improve with work, job, home, hunger, mood and a life history.
- **Jobs.** People are assigned by settlement rules (job priorities). The rules give priority to food during shortages, to producers of scarce goods, and to builders.
- **Production chains** defined in JSON: farm → grain, then mill & bakery → bread; woodcutter → wood; quarry → stone. They respect stock reserves and stock limits (like Dwarf Fortress work orders), and workers are freed once stores are full.
- **Construction.** Buildings wait for materials, then builders put them up. Placement rules check terrain and nearby forest or rock.
- **Logistics.** Distance to the nearest stockpile slows workers down, so town layout matters.
- **Needs and mood.** Bread makes people happier than raw grain. Homelessness and hunger hurt. Starvation kills.
- **Life events.** Ageing, marriage, births, deaths, newcomers arriving when the town thrives, and unhappy people leaving.
- **A governor AI** grows the town automatically, using the same commands a player would. This is the seed of the NPC faction AI.
- **Deterministic.** The same seed always produces the same world. A state hash proves it, and the tests check it.

## Running it

You need the [.NET 8 SDK](https://dotnet.microsoft.com/download).

Simulate 100 game days and print a weekly report:

```bash
dotnet run --project src/Sim.Console -- --days 100
```

Print the town map at the start and end, a daily report, and every citizen at the end:

```bash
dotnet run --project src/Sim.Console -- --seed 7 --days 200 --report daily --map --citizens
```

Run at real speed, sped up 60× (one game day per real minute):

```bash
dotnet run --project src/Sim.Console -- --realtime 60
```

Run the tests:

```bash
dotnet test
```

## Project layout

| Path | Contents |
|---|---|
| `src/Sim.Core/` | The simulation. No engine code; it will be shared by the server and the Godot client. |
| `src/Sim.Core/Data/*.json` | Data-driven definitions for resources and buildings. |
| `src/Sim.Core/World/` | Seeded chunk-based world generation. |
| `src/Sim.Core/Citizens/` | Citizen records and names. |
| `src/Sim.Core/Settlements/` | Settlements, buildings, settlement rules. |
| `src/Sim.Core/Systems/` | Systems that run each tick, hour or day: construction, production, jobs, housing, needs, life events, migration. |
| `src/Sim.Core/AI/` | Site finder and settlement governor. |
| `src/Sim.Core/Commands/` | The commands players and AI use to change the world. |
| `src/Sim.Core/Simulation/` | The world state root, the simulator loop, and settlement founding. |
| `src/Sim.Console/` | Headless runner with text reports and an ASCII map. |
| `tests/Sim.Tests/` | xUnit tests. |

## Next steps

1. **Phase 0, continued:** services (well, market, chapel, tavern), food variety, a hauling-job model, and more settlements on one map.
2. **Phase 1:** a Godot 4 (C#) client that draws a map chunk isometrically, with low-poly buildings, townsfolk walking to work, building placement, and clicking a citizen to inspect them.
3. **Phase 2:** a dedicated server with PostgreSQL persistence and multiplayer.
