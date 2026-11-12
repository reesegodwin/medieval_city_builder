# Notes for Claude

- Design and roadmap: `docs/DESIGN.md`. Keep it updated when design decisions change.
- `src/Sim.Core` must stay engine-free (no Godot or Unity references). It is shared by the future server and client.
- The simulation must stay deterministic. Use `GameWorld.Rng` (never `System.Random`, wall-clock time, or unordered dictionary iteration), and iterate settlements, buildings and citizens in their list order.
- All changes from players or AI go through `ICommand`s in `src/Sim.Core/Commands`.
- Write durations in game days (`TimeScale.DaysToTicks`). Never hard-code ticks.
- Building and resource balance lives in `src/Sim.Core/Data/*.json`.
- Before committing, run `dotnet test`. For balance changes, also run a 720-day check on seeds 42, 7 and 1234:
  `dotnet run -c Release --project src/Sim.Console -- --days 720 --report none --seed 42`
