using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Sim.Core.Commands;
using Sim.Core.Persistence;
using Sim.Core.Simulation;
using Sim.Core.Time;

namespace Sim.Net;

/// <summary>
/// Runs a shared world: owns the authoritative simulation, admits players, checks and orders their commands,
/// moves time forward under the world's speed and offline rules, and tells every client what happened.
/// Transport-agnostic: feed it lines with <see cref="Receive"/> and call <see cref="Update"/> regularly from
/// one thread.
/// </summary>
public sealed class WorldHost
{
    /// <summary>Most ticks simulated per update, so catch-up is spread over several updates (10 game days).</summary>
    public const int MaxTicksPerUpdate = TimeScale.TicksPerDay * 10;

    private sealed class Player
    {
        public required IPeer Peer { get; init; }
        public required string Name { get; init; }
        public double SpeedRequest { get; set; } = 1;
    }

    /// <summary>Someone who said hello and was asked to prove who they are.</summary>
    private sealed class Pending
    {
        public required string Name { get; init; }
        public required byte[] Salt { get; init; }
        public required byte[] Nonce { get; init; }
        public required bool Register { get; init; }
    }

    private readonly Dictionary<IPeer, Pending> _pending = new();
    private readonly ConcurrentQueue<(IPeer Peer, string? Line)> _inbox = new();
    private readonly List<Player> _players = new();
    private readonly Simulator _simulator;
    private double _pendingTicks;
    private long _catchUpTicks;
    private long _lastHashDay;
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    public GameWorld World { get; }

    /// <summary>The player who controls speed under <see cref="SpeedPolicy.Host"/> (null: whoever joined first).</summary>
    public string? HostName { get; }

    public event Action<string>? Log;

    /// <summary>Player accounts (null: names are trusted, as between friends).</summary>
    public AccountBook? Accounts { get; }

    public WorldHost(GameWorld world, string? hostName = null, DateTimeOffset? nowUtc = null, AccountBook? accounts = null)
    {
        World = world;
        Accounts = accounts;
        HostName = hostName;
        _simulator = new Simulator(world);
        _lastHashDay = world.Time.Day;
        _now = nowUtc ?? DateTimeOffset.UtcNow;

        // A 24/7 world catches up on the time the server itself was down.
        if (world.Settings.WhenEmpty == EmptyWorldPolicy.Run) _catchUpTicks = OwedTicks(_now);
    }

    public IReadOnlyList<string> Players => _players.Select(p => p.Name).ToList();
    public bool IsCatchingUp => _catchUpTicks > 0;
    public long CatchUpTicksLeft => _catchUpTicks;

    /// <summary>Current speed as a multiple of the world's normal speed.</summary>
    public double SpeedMultiplier
    {
        get
        {
            var settings = World.Settings;
            double x = settings.SpeedPolicy switch
            {
                SpeedPolicy.Fixed => 1,
                SpeedPolicy.Host => (_players.FirstOrDefault(p => p.Name == HostName) ?? _players.FirstOrDefault())?.SpeedRequest ?? 1,
                _ => _players.Count == 0 ? 1 : _players.Min(p => p.SpeedRequest),
            };
            return Math.Clamp(x, 1, Math.Max(1, settings.MaxSpeedMultiplier));
        }
    }

    // ---- input (any thread) ----

    public void Receive(IPeer peer, string line) => _inbox.Enqueue((peer, line));
    public void Disconnected(IPeer peer) => _inbox.Enqueue((peer, null));

    // ---- main loop (one thread) ----

    /// <summary>Handles messages, then moves time on by <paramref name="realSeconds"/> under the world's rules.</summary>
    public void Update(double realSeconds, DateTimeOffset? nowUtc = null)
    {
        _now = nowUtc ?? DateTimeOffset.UtcNow;
        while (_inbox.TryDequeue(out var item))
        {
            if (item.Line == null) Leave(item.Peer);
            else Handle(item.Peer, item.Line);
        }

        bool active = _players.Count > 0 || World.Settings.WhenEmpty == EmptyWorldPolicy.Run;
        long ticks = 0;
        if (_catchUpTicks > 0)
        {
            ticks = Math.Min(_catchUpTicks, MaxTicksPerUpdate);
            _catchUpTicks -= ticks;
        }
        else if (active)
        {
            _pendingTicks += realSeconds / WorldClock.SecondsPerTick(World.Settings, SpeedMultiplier);
            ticks = Math.Min((long)_pendingTicks, MaxTicksPerUpdate);
            _pendingTicks = ticks == MaxTicksPerUpdate ? 0 : _pendingTicks - ticks; // drop a backlog we can't keep up with
        }

        for (long i = 0; i < ticks; i++) _simulator.Step();
        if (ticks > 0) Broadcast(Protocol.Message("adv", ("tick", World.Time.Tick)));
        if (World.Time.Day != _lastHashDay)
        {
            _lastHashDay = World.Time.Day;
            Broadcast(Protocol.Message("hash", ("tick", World.Time.Tick), ("hash", World.ComputeStateHash().ToString("X16"))));
        }
        if (active) World.LastActiveUtc = _now;
    }

    private long OwedTicks(DateTimeOffset now)
    {
        var settings = World.Settings.Clone();
        settings.WhenEmpty = EmptyWorldPolicy.CatchUp;
        return WorldClock.CatchUpTicks(settings, World.LastActiveUtc, now);
    }

    private void Handle(IPeer peer, string line)
    {
        var msg = Protocol.Parse(line);
        if (msg == null) return;
        string type = msg["t"]?.GetValue<string>() ?? "";
        var player = _players.FirstOrDefault(p => p.Peer == peer);

        if (type == "hello")
        {
            if (player == null) Hello(peer, msg);
            return;
        }
        if (type == "auth")
        {
            if (player == null && _pending.Remove(peer, out var pending)) Authenticate(peer, pending, msg);
            return;
        }
        if (player == null) return; // must say hello first

        switch (type)
        {
            case "cmd":
                HandleCommand(player, msg);
                break;
            case "speed":
                player.SpeedRequest = Math.Clamp(msg["x"]?.GetValue<double>() ?? 1, 1, Math.Max(1, World.Settings.MaxSpeedMultiplier));
                Broadcast(Protocol.Message("speed", ("x", SpeedMultiplier)));
                break;
            case "resync":
                SendWelcome(player);
                break;
        }
    }

    private void Refuse(IPeer peer, string problem)
    {
        peer.Send(Protocol.Line(Protocol.Message("error", ("msg", problem))));
        peer.Close();
    }

    private void Hello(IPeer peer, JsonObject hello)
    {
        string name = (hello["name"]?.GetValue<string>() ?? "").Trim();
        int version = hello["version"]?.GetValue<int>() ?? 0;
        string? problem =
            version != Protocol.Version ? $"Version mismatch: server {Protocol.Version}, client {version}" :
            name.Length == 0 || name.Length > Protocol.MaxNameLength ? $"Names must be 1-{Protocol.MaxNameLength} characters" :
            _players.Any(p => p.Name == name) ? $"{name} is already playing" :
            Accounts?.IsLocked(name, _now) == true ? "Too many wrong passwords: try again in a minute" : null;
        if (problem != null)
        {
            Refuse(peer, problem);
            return;
        }
        if (Accounts == null)
        {
            Join(peer, name);
            return;
        }

        // Prove who you are: the account's salt (or a new one to register with) and a fresh nonce.
        var account = Accounts.Accounts.GetValueOrDefault(name);
        var pending = new Pending
        {
            Name = name,
            Salt = account != null ? Convert.FromBase64String(account.Salt) : AccountBook.NewRandom(AccountBook.SaltBytes),
            Nonce = AccountBook.NewRandom(AccountBook.NonceBytes),
            Register = account == null,
        };
        _pending[peer] = pending;
        peer.Send(Protocol.Line(Protocol.Message("auth",
            ("salt", Convert.ToBase64String(pending.Salt)), ("nonce", Convert.ToBase64String(pending.Nonce)),
            ("iterations", account?.Iterations ?? AccountBook.Iterations), ("register", pending.Register))));
    }

    private void Authenticate(IPeer peer, Pending pending, JsonObject msg)
    {
        string name = pending.Name;
        if (_players.Any(p => p.Name == name))
        {
            Refuse(peer, $"{name} is already playing");
            return;
        }
        bool ok = pending.Register
            ? Accounts!.Register(name, pending.Salt, msg["key"]?.GetValue<string>() ?? "", _now)
            : Accounts!.Verify(name, pending.Nonce, msg["proof"]?.GetValue<string>() ?? "", _now);
        if (!ok)
        {
            Log?.Invoke($"Failed login for {name} from {peer.Address}");
            Refuse(peer, pending.Register ? "Couldn't register that name" : "Wrong password");
            return;
        }
        if (pending.Register) Log?.Invoke($"Registered {name}");
        Join(peer, name);
    }

    private void Join(IPeer peer, string name)
    {

        // Coming back to an empty world: catch up on the time away first (the new player watches it happen).
        if (_players.Count == 0 && World.Settings.WhenEmpty == EmptyWorldPolicy.CatchUp)
        {
            _catchUpTicks = OwedTicks(_now);
            if (_catchUpTicks > 0) Log?.Invoke($"Catching up {(double)_catchUpTicks / TimeScale.TicksPerDay:0.#} game days");
            for (; _catchUpTicks > 0; _catchUpTicks--) _simulator.Step(); // before the snapshot, so it's included
        }

        if (!World.Settlements.Any(s => s.OwnerId == name))
        {
            // A single-player world turned shared: its first player takes over the unowned town.
            var unowned = World.Settlements.FirstOrDefault(s => s.OwnerId == null && !Sim.Core.Diplomacy.Factions.IsNpc(World, s));
            if (unowned != null && _players.Count == 0 && (HostName == null || HostName == name))
            {
                unowned.OwnerId = name;
                if (Sim.Core.Diplomacy.Factions.Of(World, unowned) is { } realm)
                {
                    realm.PlayerName = name;
                    realm.Name = $"Realm of {name}";
                }
            }
            else
            {
                var result = Apply(name, -1, new FoundSettlementCommand(name));
                if (!result.Ok)
                {
                    peer.Send(Protocol.Line(Protocol.Message("error", ("msg", result.Error))));
                    peer.Close();
                    return;
                }
            }
        }

        var player = new Player { Peer = peer, Name = name };
        _players.Add(player);
        SendWelcome(player);
        Broadcast(Protocol.Message("speed", ("x", SpeedMultiplier)));
        Broadcast(Protocol.Message("info", ("msg", $"{name} joined")));
        Log?.Invoke($"{name} joined from {peer.Address}");
    }

    private void SendWelcome(Player player)
    {
        var town = World.Settlements.First(s => s.OwnerId == player.Name);
        World.LastActiveUtc = _now;
        player.Peer.Send(Protocol.Line(Protocol.Message("welcome",
            ("name", player.Name),
            ("settlement", town.Id),
            ("host", player.Name == (HostName ?? _players.FirstOrDefault()?.Name)),
            ("speed", SpeedMultiplier),
            ("save", SaveGame.ToJson(World)))));
    }

    private void HandleCommand(Player player, JsonObject msg)
    {
        int seq = msg["seq"]?.GetValue<int>() ?? -1;
        ICommand command;
        try
        {
            command = CommandCodec.Decode(msg["cmd"]!);
        }
        catch (Exception e) when (e is InvalidDataException or System.Text.Json.JsonException or NullReferenceException)
        {
            Reject(player, seq, "Unreadable command");
            return;
        }

        if (command is FoundSettlementCommand)
        {
            Reject(player, seq, "Settlements are founded when you join");
            return;
        }
        if (command is ISettlementCommand sc && World.FindSettlement(sc.SettlementId)?.OwnerId != player.Name)
        {
            Reject(player, seq, "That isn't your settlement");
            return;
        }
        Apply(player.Name, seq, command);
    }

    /// <summary>Runs a command on the authoritative world and sends it, with its result, to everyone.</summary>
    private CommandResult Apply(string from, int seq, ICommand command)
    {
        var result = World.Execute(command);
        Broadcast(Protocol.Message("apply",
            ("tick", World.Time.Tick), ("from", from), ("seq", seq), ("cmd", CommandCodec.Encode(command)),
            ("ok", result.Ok), ("error", result.Error), ("id", result.CreatedId)));
        return result;
    }

    private void Reject(Player player, int seq, string error) =>
        player.Peer.Send(Protocol.Line(Protocol.Message("reject", ("seq", seq), ("error", error))));

    private void Leave(IPeer peer)
    {
        _pending.Remove(peer);
        var player = _players.FirstOrDefault(p => p.Peer == peer);
        if (player == null) return;
        _players.Remove(player);
        World.LastActiveUtc = _now;
        Broadcast(Protocol.Message("info", ("msg", $"{player.Name} left")));
        Broadcast(Protocol.Message("speed", ("x", SpeedMultiplier)));
        Log?.Invoke($"{player.Name} left");
    }

    private void Broadcast(JsonObject message)
    {
        string line = Protocol.Line(message);
        foreach (var p in _players) p.Peer.Send(line);
    }
}
