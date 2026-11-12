using Godot;
using Sim.Core.Commands;
using Sim.Core.Events;
using Sim.Core.Settlements;
using Sim.Core.Simulation;
using Sim.Core.Time;

namespace MedievalClient;

/// <summary>
/// The client's only door into the simulation. In Phase 1 the simulation runs in-process;
/// in Phase 2 this becomes a network connection to the dedicated server, and the renderers
/// and UI won't need to change.
/// </summary>
public sealed class SimSession
{
    /// <summary>Speed multipliers relative to real time (1 = 1 game day per real hour).</summary>
    public static readonly double[] Speeds = { 1, 60, 600, 3600 };

    /// <summary>Upper bound on simulation ticks per rendered frame, so the UI stays responsive.</summary>
    public const int MaxTicksPerFrame = 40;

    private readonly Simulator _simulator;
    private double _pendingSeconds;
    private int _eventCursor;

    public GameWorld World { get; }
    public Settlement PlayerTown { get; }

    public double Speed { get; set; } = 60;
    public bool Paused { get; set; }

    public SimSession(ulong seed)
    {
        World = new GameWorld(seed);
        PlayerTown = SettlementFounder.Found(World);
        _simulator = new Simulator(World);
    }

    /// <summary>Runs as many ticks as the elapsed real time (times speed) calls for.</summary>
    public void Advance(double realDeltaSeconds)
    {
        if (Paused) return;
        _pendingSeconds += realDeltaSeconds * Speed;

        int ticks = 0;
        while (_pendingSeconds >= TimeScale.RealSecondsPerTick && ticks < MaxTicksPerFrame)
        {
            _simulator.Step();
            _pendingSeconds -= TimeScale.RealSecondsPerTick;
            ticks++;
        }

        // If the machine can't keep up, drop the backlog instead of spiralling.
        if (ticks == MaxTicksPerFrame) _pendingSeconds = Math.Min(_pendingSeconds, TimeScale.RealSecondsPerTick);
    }

    public CommandResult Execute(ICommand command) => World.Execute(command);

    /// <summary>Fast-forwards the simulation (used for testing and screenshots).</summary>
    public void RunDays(int days) => _simulator.RunDays(days);

    /// <summary>Events logged since the last call.</summary>
    public List<GameEvent> TakeNewEvents()
    {
        var events = World.Events.Skip(_eventCursor).ToList();
        _eventCursor = World.Events.Count;
        return events;
    }

    /// <summary>Value after a user argument (passed after "--" on Godot's command line), if present.</summary>
    public static string? UserArg(string name)
    {
        var args = OS.GetCmdlineUserArgs();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == name) return i + 1 < args.Length ? args[i + 1] : "";
        }
        return null;
    }

    public static ulong SeedFromCommandLine(ulong fallback) =>
        ulong.TryParse(UserArg("--seed"), out ulong seed) ? seed : fallback;
}

/// <summary>Tile ↔ 3D conversion. One tile is one unit; tile (x, y) spans X [x, x+1] and Z [y, y+1].</summary>
public static class Coords
{
    public static Vector3 TileCorner(int x, int y) => new(x, 0, y);
    public static Vector3 TileCenter(int x, int y) => new(x + 0.5f, 0, y + 0.5f);

    public static Vector3 BuildingCenter(Building b) =>
        new(b.X + b.Def.Width / 2f, 0, b.Y + b.Def.Height / 2f);

    /// <summary>Point just outside the building's front (south) edge, where people come and go.</summary>
    public static Vector3 BuildingDoor(Building b) =>
        new(b.X + b.Def.Width / 2f, 0, b.Y + b.Def.Height + 0.2f);

    public static (int X, int Y) WorldToTile(Vector3 p) => (Mathf.FloorToInt(p.X), Mathf.FloorToInt(p.Z));
}
