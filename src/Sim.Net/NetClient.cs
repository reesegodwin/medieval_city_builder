using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Sim.Core.Commands;
using Sim.Core.Events;
using Sim.Core.Persistence;
using Sim.Core.Settlements;
using Sim.Core.Simulation;

namespace Sim.Net;

/// <summary>
/// A player's view of a shared world. Keeps a full copy of the simulation in step with the server: it loads the
/// snapshot it's welcomed with, runs the ticks the server announces and applies the commands the server
/// relays, in order. The player's own commands only take effect when the server sends them back.
/// </summary>
public sealed class NetClient
{
    private readonly ConcurrentQueue<string> _inbox = new();
    private readonly Action<string> _send;
    private Simulator? _simulator;
    private long _advanceTo = -1;
    private int _nextSeq;

    private readonly string? _password;

    /// <param name="password">For servers with accounts: never sent, only used to answer the server's challenge.</param>
    public NetClient(string name, Action<string> send, string? password = null)
    {
        Name = name;
        _send = send;
        _password = password;
    }

    public string Name { get; }
    public GameWorld? World { get; private set; }
    public Settlement? Town { get; private set; }
    public bool IsHost { get; private set; }

    /// <summary>The world's current speed multiplier, as last reported by the server.</summary>
    public double Speed { get; private set; } = 1;

    /// <summary>Why the server turned us away or something failed, if it did.</summary>
    public string? Error { get; private set; }

    /// <summary>Times our copy of the world drifted from the server's and had to be reloaded.</summary>
    public int Resyncs { get; private set; }

    public bool Connected => World != null && Error == null;

    /// <summary>Server tick we've been told about but not yet simulated (0 when caught up).</summary>
    public long TicksBehind => _advanceTo < 0 || World == null ? 0 : _advanceTo - World.Time.Tick;

    /// <summary>A command we sent has been carried out (or refused) by the server: (seq, result).</summary>
    public event Action<int, CommandResult>? CommandFinished;

    /// <summary>Raised when a new snapshot replaced the world (first join or a resync).</summary>
    public event Action? WorldLoaded;

    /// <summary>Server notices (players joining or leaving...).</summary>
    public event Action<string>? Info;

    /// <summary>
    /// The server wants proof of who we are: derive the key from our password and its salt, then either register
    /// it (a new name) or answer its nonce with an HMAC. The password itself never leaves this machine.
    /// </summary>
    private void Authenticate(JsonObject msg)
    {
        if (string.IsNullOrEmpty(_password))
        {
            Error = "This server needs a password";
            return;
        }
        var salt = Convert.FromBase64String(msg["salt"]!.GetValue<string>());
        int iterations = Math.Clamp(msg["iterations"]?.GetValue<int>() ?? AccountBook.Iterations, 10_000, 1_000_000);
        var key = AccountBook.DeriveKey(_password, salt, iterations);
        if (msg["register"]?.GetValue<bool>() == true)
            _send(Protocol.Line(Protocol.Message("auth", ("key", Convert.ToBase64String(key)))));
        else
        {
            var nonce = Convert.FromBase64String(msg["nonce"]!.GetValue<string>());
            _send(Protocol.Line(Protocol.Message("auth", ("proof", Convert.ToBase64String(AccountBook.Proof(key, nonce))))));
        }
    }

    public void Hello() => _send(Protocol.Line(Protocol.Message("hello", ("name", Name), ("version", Protocol.Version))));

    /// <summary>Called by the transport (any thread) for each line from the server.</summary>
    public void Receive(string line) => _inbox.Enqueue(line);

    public void Disconnected() => _inbox.Enqueue(Protocol.Line(Protocol.Message("error", ("msg", "Disconnected from the server"))));

    /// <summary>Sends a command for the server to carry out. Returns its sequence number.</summary>
    public int Send(ICommand command)
    {
        int seq = ++_nextSeq;
        _send(Protocol.Line(Protocol.Message("cmd", ("seq", seq), ("cmd", CommandCodec.Encode(command)))));
        return seq;
    }

    /// <summary>Asks for a speed (a multiple of normal); what counts depends on the world's speed policy.</summary>
    public void RequestSpeed(double multiplier) => _send(Protocol.Line(Protocol.Message("speed", ("x", multiplier))));

    /// <summary>Processes server messages in order, simulating at most <paramref name="maxTicks"/> ticks.</summary>
    public void Poll(int maxTicks = int.MaxValue)
    {
        int budget = maxTicks;
        while (true)
        {
            if (_advanceTo >= 0 && World != null)
            {
                while (World.Time.Tick < _advanceTo && budget > 0)
                {
                    _simulator!.Step();
                    budget--;
                }
                if (World.Time.Tick < _advanceTo) return; // continue next frame
                _advanceTo = -1;
            }
            if (!_inbox.TryDequeue(out var line)) return;
            if (Protocol.Parse(line) is { } msg) Handle(msg);
        }
    }

    private void Handle(JsonObject msg)
    {
        switch (msg["t"]?.GetValue<string>())
        {
            case "auth":
                Authenticate(msg);
                break;

            case "welcome":
                World = SaveGame.FromJson(msg["save"]!.GetValue<string>());
                _simulator = new Simulator(World);
                Town = World.FindSettlement(msg["settlement"]!.GetValue<int>());
                IsHost = msg["host"]?.GetValue<bool>() ?? false;
                Speed = msg["speed"]?.GetValue<double>() ?? 1;
                _advanceTo = -1;
                WorldLoaded?.Invoke();
                break;

            case "adv":
                if (World != null) _advanceTo = msg["tick"]!.GetValue<long>();
                break;

            case "apply":
                if (World == null) break;
                var result = World.Execute(CommandCodec.Decode(msg["cmd"]!));
                if (msg["from"]?.GetValue<string>() == Name && msg["seq"]?.GetValue<int>() is int seq and > 0)
                {
                    bool ok = msg["ok"]?.GetValue<bool>() ?? result.Ok;
                    CommandFinished?.Invoke(seq, ok
                        ? CommandResult.Success(msg["id"]?.GetValue<int>())
                        : CommandResult.Fail(msg["error"]?.GetValue<string>() ?? "Failed"));
                }
                break;

            case "reject":
                CommandFinished?.Invoke(msg["seq"]?.GetValue<int>() ?? -1, CommandResult.Fail(msg["error"]?.GetValue<string>() ?? "Refused"));
                break;

            case "hash":
                if (World == null) break;
                string expected = msg["hash"]!.GetValue<string>();
                if (World.ComputeStateHash().ToString("X16") != expected)
                {
                    Resyncs++;
                    _send(Protocol.Line(Protocol.Message("resync")));
                }
                break;

            case "speed":
                Speed = msg["x"]?.GetValue<double>() ?? Speed;
                break;

            case "info":
                Info?.Invoke(msg["msg"]?.GetValue<string>() ?? "");
                break;

            case "error":
                Error = msg["msg"]?.GetValue<string>() ?? "Error";
                break;
        }
    }

    /// <summary>Events in our world copy since the given index (for the event feed).</summary>
    public IEnumerable<GameEvent> EventsSince(int index) => World?.Events.Skip(index) ?? Enumerable.Empty<GameEvent>();
}
