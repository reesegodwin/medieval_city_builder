# Notes for Claude

- Design and roadmap: `docs/DESIGN.md`. Keep it updated when design decisions change.
- End every response with suggested next steps, unless it ends with a question for the user.
- `src/Sim.Core` and `src/Sim.Net` must stay engine-free (no Godot or Unity references). They are shared by the dedicated server (`src/Sim.Server`) and the client.
- Multiplayer relies on determinism: clients replay the server's ticks and commands. Anything that changes the world must be an `ICommand` registered in `CommandCodec`, and new state must be saved by `SaveGame` and included in `ComputeStateHash`.
- The simulation must stay deterministic. Use `GameWorld.Rng` (never `System.Random`, wall-clock time, or unordered dictionary iteration), and iterate settlements, buildings and citizens in their list order.
- All changes from players or AI go through `ICommand`s in `src/Sim.Core/Commands`.
- Write durations in game days (`TimeScale.DaysToTicks`). Never hard-code ticks.
- Building and resource balance lives in `src/Sim.Core/Data/*.json`.
- Before committing, run `dotnet test`. For balance changes, also run a 720-day check on seeds 42, 7 and 1234:
  `dotnet run -c Release --project src/Sim.Console -- --days 720 --report none --seed 42`

## Client (`client/`)
- Godot 4.7.2 .NET, pinned through `Godot.NET.Sdk/4.7.2` in `client/MedievalClient.csproj`. If you bump it, update the README download link too.
- Rendering and UI stay in `client/`; game rules stay in `Sim.Core`. The client talks to the simulation only through `client/Scripts/SimSession.cs` (single-player, host or join).
- Art is procedural placeholder meshes (`client/Scripts/LowPolyMeshes.cs`). Citizens' walking is cosmetic; never feed client positions back into the simulation.
- Verify client changes with `dotnet build client/MedievalClient.csproj`. When a Godot binary is available, also render a screenshot with `xvfb-run` (see README) and look at it.

## Assets and licences
- All art and audio are currently generated in code (`LowPolyMeshes.cs`, `Audio/SoundSynth.cs`). Keep it that way unless a third-party asset is clearly worth it.
- Never add an asset from the internet without reading its licence from the original source (the licence file in the download or the creator's page). Prefer CC0. Record it in `CREDITS.md` (creator, URL, licence, required attribution) and keep the licence file next to the asset. If the licence can't be verified, don't use the asset.
- Exported builds must ship Godot's and .NET's licence notices (see `CREDITS.md`).
