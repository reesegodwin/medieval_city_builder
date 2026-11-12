using Godot;
using Sim.Core.Commands;
using Sim.Core.Events;
using Sim.Core.Persistence;
using Sim.Core.Settlements;
using Sim.Core.Simulation;
using Sim.Core.Time;
using Sim.Net;

namespace MedievalClient;

/// <summary>How this client takes part in a world.</summary>
public enum SessionMode
{
    /// <summary>Single-player: the simulation runs here, nobody else is connected.</summary>
    Local,

    /// <summary>Host-and-play: this client runs the server for friends and plays on it.</summary>
    Host,

    /// <summary>Playing on someone else's host or a dedicated server.</summary>
    Join,
}

/// <summary>
/// The client's only door into the simulation. Locally it runs the world itself; when hosting or joining it
/// holds a <see cref="NetClient"/> copy that the server keeps in step. The renderers and UI only see
/// <see cref="World"/>, <see cref="PlayerTown"/> and <see cref="Execute"/>, so they work the same either way.
/// </summary>
public sealed class SimSession : IDisposable
{
    /// <summary>Speed multipliers offered, relative to the world's normal speed.</summary>
    public static readonly double[] Speeds = { 1, 2, 4, 8, 16, 64 };

    /// <summary>Upper bound on simulation ticks per rendered frame, so the UI stays responsive.</summary>
    public const int MaxTicksPerFrame = 40;

    /// <summary>When catching up with a server, simulate at most this many ticks per frame.</summary>
    public const int MaxCatchUpTicksPerFrame = 400;

    /// <summary>Where single-player quick-saves go (Godot's per-user data folder).</summary>
    public const string SavePath = "user://savegame.json";

    /// <summary>Where a hosted world is kept between sessions.</summary>
    public const string HostedSavePath = "user://hosted_world.json";

    /// <summary>The running session, kept across scene reloads (loading a save, or a resync with the server).</summary>
    public static SimSession? Current { get; set; }

    private readonly Simulator? _simulator;
    private readonly NetClient? _net;
    private readonly WorldHost? _host;
    private readonly TcpHostListener? _listener;
    private readonly HostRunner? _runner;
    private readonly TcpLineConnection? _connection;
    private double _pendingSeconds;
    private int _eventCursor;
    private double _speed = 1;

    public SessionMode Mode { get; }
    public GameWorld World => _net?.World ?? _localWorld!;
    public Settlement PlayerTown => _net?.Town ?? _localWorld!.Settlements[0];
    private readonly GameWorld? _localWorld;

    /// <summary>Port friends connect to when hosting.</summary>
    public int? HostPort => _listener?.Port;

    /// <summary>Messages for the player: refused commands, people joining, connection trouble.</summary>
    public event Action<string>? Notice;

    /// <summary>The world object was replaced (a resync with the server); renderers must be rebuilt.</summary>
    public event Action? WorldReplaced;

    // ---------- creating sessions ----------

    /// <summary>Single-player with a new or loaded world.</summary>
    public SimSession(GameWorld world)
    {
        Mode = SessionMode.Local;
        _localWorld = world;
        _simulator = new Simulator(world);
        // Time spent away from a single-player world follows its rules (new worlds pause).
        long owed = WorldClock.CatchUpTicks(world.Settings, world.LastActiveUtc, DateTimeOffset.UtcNow);
        if (owed > 0)
        {
            _simulator.RunTicks(owed);
            _pendingNotice = $"While you were away: {(double)owed / TimeScale.TicksPerDay:0.#} game days passed";
        }
        world.LastActiveUtc = DateTimeOffset.UtcNow;
        _eventCursor = world.Events.Count;
    }

    private string? _pendingNotice;

    /// <summary>A new single-player world at a named speed.</summary>
    public static SimSession NewLocal(ulong seed, double secondsPerGameDay)
    {
        var world = new GameWorld(seed);
        world.Settings.SecondsPerGameDay = secondsPerGameDay;
        world.Settings.WhenEmpty = EmptyWorldPolicy.Pause;
        SettlementFounder.Found(world);
        world.LastActiveUtc = DateTimeOffset.UtcNow;
        return new SimSession(world);
    }

    private SimSession(SessionMode mode, string playerName, string address, int port, WorldHost? host,
        TcpHostListener? listener, HostRunner? runner)
    {
        Mode = mode;
        _host = host;
        _listener = listener;
        _runner = runner;
        NetClient? client = null;
        _connection = TcpLineConnection.Connect(address, port, line => client!.Receive(line), () => client!.Disconnected());
        _net = client = new NetClient(playerName, _connection.Send);
        _net.Info += message => Notice?.Invoke(message);
        _net.CommandFinished += (_, result) =>
        {
            if (!result.Ok) Notice?.Invoke($"Refused: {result.Error}");
        };
        _net.WorldLoaded += () =>
        {
            if (_eventCursor >= 0) WorldReplaced?.Invoke();
        };
        _eventCursor = -1;
        _net.Hello();

        // Wait for the snapshot so the scene has a world to show.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (_net.World == null && _net.Error == null && DateTime.UtcNow < deadline)
        {
            _net.Poll(0);
            Thread.Sleep(10);
        }
        if (_net.World == null)
        {
            string error = _net.Error ?? "The server didn't answer";
            Dispose();
            throw new InvalidOperationException(error);
        }
        _eventCursor = _net.World.Events.Count;
    }

    /// <summary>Hosts a world for friends (loading the hosted world if there is one) and joins it.</summary>
    public static SimSession HostGame(string playerName, int port, ulong seed, WorldSettings settings)
    {
        string path = ProjectSettings.GlobalizePath(HostedSavePath);
        GameWorld world;
        if (File.Exists(path))
        {
            world = SaveGame.Load(path);
        }
        else
        {
            world = new GameWorld(seed) { Settings = settings, LastActiveUtc = DateTimeOffset.UtcNow };
            SettlementFounder.Found(world);
        }
        return HostWorld(playerName, port, world, path);
    }

    /// <summary>Opens an existing world (e.g. a single-player save) to friends.</summary>
    public static SimSession HostWorld(string playerName, int port, GameWorld world, string savePath)
    {
        var host = new WorldHost(world, playerName);
        var listener = new TcpHostListener(host, port);
        var runner = new HostRunner(host, h => SaveAtomically(h.World, savePath));
        try
        {
            return new SimSession(SessionMode.Host, playerName, "127.0.0.1", listener.Port, host, listener, runner);
        }
        catch
        {
            runner.Dispose();
            listener.Dispose();
            throw;
        }
    }

    /// <summary>Joins a friend's game or a dedicated server.</summary>
    public static SimSession JoinGame(string playerName, string address, int port) =>
        new(SessionMode.Join, playerName, address, port, null, null, null);

    private static void SaveAtomically(GameWorld world, string path)
    {
        string temp = path + ".tmp";
        SaveGame.Save(world, temp);
        File.Move(temp, path, overwrite: true);
    }

    // ---------- playing ----------

    /// <summary>Requested speed multiplier (in a shared world the server decides what actually applies).</summary>
    public double Speed
    {
        get => _net?.Speed ?? _speed;
        set
        {
            if (_net != null) _net.RequestSpeed(value);
            else _speed = value;
        }
    }

    /// <summary>Only a single-player world can be paused; shared worlds follow the server.</summary>
    public bool CanPause => Mode == SessionMode.Local;

    public bool Paused { get; set; }

    /// <summary>Fastest speed the world allows players to ask for.</summary>
    public double MaxSpeed => Mode == SessionMode.Local ? Speeds[^1] : Math.Max(1, World.Settings.MaxSpeedMultiplier);

    /// <summary>Whether this player's speed requests count under the world's policy.</summary>
    public bool CanChangeSpeed => Mode == SessionMode.Local || World.Settings.SpeedPolicy switch
    {
        SpeedPolicy.Fixed => false,
        SpeedPolicy.Host => _net!.IsHost,
        _ => true,
    };

    /// <summary>Server ticks not yet simulated here (shown while catching up).</summary>
    public long TicksBehind => _net?.TicksBehind ?? 0;

    /// <summary>Runs the simulation for the elapsed real time, or catches up with the server.</summary>
    public void Advance(double realDeltaSeconds)
    {
        if (_pendingNotice != null)
        {
            Notice?.Invoke(_pendingNotice);
            _pendingNotice = null;
        }
        if (_net != null)
        {
            _net.Poll(MaxCatchUpTicksPerFrame);
            if (_net.Error is { } error && _netErrorShown == null)
            {
                _netErrorShown = error;
                Notice?.Invoke(error);
            }
            return;
        }

        if (Paused) return;
        _pendingSeconds += realDeltaSeconds;
        double secondsPerTick = WorldClock.SecondsPerTick(World.Settings, _speed);
        int ticks = 0;
        while (_pendingSeconds >= secondsPerTick && ticks < MaxTicksPerFrame)
        {
            _simulator!.Step();
            _pendingSeconds -= secondsPerTick;
            ticks++;
        }
        // If the machine can't keep up, drop the backlog instead of spiralling.
        if (ticks == MaxTicksPerFrame) _pendingSeconds = Math.Min(_pendingSeconds, secondsPerTick);
        World.LastActiveUtc = DateTimeOffset.UtcNow;
    }

    private string? _netErrorShown;

    /// <summary>
    /// Locally the command runs at once. In a shared world it goes to the server and takes effect when the
    /// server sends it back (a fraction of a second); refusals arrive as a <see cref="Notice"/>.
    /// </summary>
    public CommandResult Execute(ICommand command)
    {
        if (_net == null) return World.Execute(command);
        _net.Send(command);
        return CommandResult.Success();
    }

    /// <summary>Fast-forwards a single-player world (testing and screenshots).</summary>
    public void RunDays(int days) => _simulator?.RunDays(days);

    /// <summary>Events logged since the last call.</summary>
    public List<GameEvent> TakeNewEvents()
    {
        if (_eventCursor > World.Events.Count) _eventCursor = World.Events.Count; // world was replaced
        var events = World.Events.Skip(_eventCursor).ToList();
        _eventCursor = World.Events.Count;
        return events;
    }

    // ---------- saving ----------

    /// <summary>Whether this player can save: always locally, only the host in a shared world.</summary>
    public bool CanSave => Mode != SessionMode.Join;

    /// <summary>Saves the world. Returns the full file path, or throws on failure.</summary>
    public string Save()
    {
        switch (Mode)
        {
            case SessionMode.Local:
                string path = ProjectSettings.GlobalizePath(SavePath);
                World.LastActiveUtc = DateTimeOffset.UtcNow;
                SaveGame.Save(World, path);
                return path;
            case SessionMode.Host:
                _runner!.SaveSoon();
                return ProjectSettings.GlobalizePath(HostedSavePath);
            default:
                throw new InvalidOperationException("Only the host can save a shared world");
        }
    }

    public static bool HasSave => File.Exists(ProjectSettings.GlobalizePath(SavePath));
    public static bool HasHostedWorld => File.Exists(ProjectSettings.GlobalizePath(HostedSavePath));

    public static GameWorld LoadSaved() => SaveGame.Load(ProjectSettings.GlobalizePath(SavePath));

    /// <summary>Leaves the world: a host saves and stops serving, a player disconnects.</summary>
    public void Dispose()
    {
        _connection?.Dispose();
        _runner?.Dispose();
        _listener?.Dispose();
    }

    // ---------- command line ----------

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
