using Sim.Core.Commands;
using Sim.Core.Persistence;
using Sim.Core.Simulation;
using Sim.Core.Time;
using Sim.Net;

namespace Sim.Tests;

/// <summary>An in-memory connection between a host and one client.</summary>
internal sealed class LoopbackPeer : IPeer
{
    public NetClient Client { get; }
    public bool Closed { get; private set; }
    public string Address => "loopback";

    /// <summary>Everything the client sent, to check what goes over the wire.</summary>
    public List<string> Sent { get; } = new();

    public LoopbackPeer(WorldHost host, string name, string? password = null)
    {
        Client = new NetClient(name, line =>
        {
            Sent.Add(line);
            host.Receive(this, line);
        }, password);
    }

    public void Send(string line)
    {
        if (!Closed) Client.Receive(line);
    }

    public void Close() => Closed = true;
}

public class NetworkTests
{
    private static readonly DateTimeOffset T0 = new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static GameWorld NewWorld(Action<WorldSettings>? configure = null)
    {
        var world = new GameWorld(42);
        configure?.Invoke(world.Settings);
        world.Execute(new FoundSettlementCommand("alice"));
        world.LastActiveUtc = T0;
        return world;
    }

    private static LoopbackPeer Join(WorldHost host, string name, DateTimeOffset now)
    {
        var peer = new LoopbackPeer(host, name);
        peer.Client.Hello();
        host.Update(0, now);
        peer.Client.Poll();
        return peer;
    }

    /// <summary>Advances the host by game days at normal speed and lets the clients catch up.</summary>
    private static void Run(WorldHost host, double days, DateTimeOffset now, params LoopbackPeer[] peers)
    {
        double seconds = days * host.World.Settings.SecondsPerGameDay / host.SpeedMultiplier;
        for (double t = 0; t < seconds; t += seconds / 20)
        {
            host.Update(seconds / 20, now);
            foreach (var p in peers) p.Client.Poll();
        }
    }

    [Fact]
    public void CommandsSurviveTheTripOverTheWire()
    {
        var commands = new ICommand[]
        {
            new PlaceBuildingCommand(1, "well", 3, 4), new PlaceRoadCommand(1, new[] { (1, 2), (3, 4) }),
            new ClaimTerritoryCommand(2, new[] { (5, 6) }), new SetJobPriorityCommand(1, "farmer", 7),
            new UpgradeTownCentreCommand(9), new FoundSettlementCommand("bob"), new SetAutoExpandTerritoryCommand(1, false),
        };
        foreach (var command in commands)
        {
            var back = CommandCodec.Decode(System.Text.Json.Nodes.JsonNode.Parse(CommandCodec.Encode(command).ToJsonString())!);
            Assert.Equal(command.GetType(), back.GetType());
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(command, command.GetType(), new System.Text.Json.JsonSerializerOptions { IncludeFields = true }),
                System.Text.Json.JsonSerializer.Serialize(back, back.GetType(), new System.Text.Json.JsonSerializerOptions { IncludeFields = true }));
        }
    }

    [Fact]
    public void PlayersStayInStepWithTheServer()
    {
        var host = new WorldHost(NewWorld(s => s.SecondsPerGameDay = 60), nowUtc: T0);
        var alice = Join(host, "alice", T0);
        var bob = Join(host, "bob", T0);
        alice.Client.Poll();

        Assert.NotNull(alice.Client.Town);
        Assert.NotNull(bob.Client.Town);
        Assert.NotEqual(alice.Client.Town!.Id, bob.Client.Town!.Id);
        Assert.Equal("bob", host.World.Settlements.Single(s => s.Id == bob.Client.Town.Id).OwnerId);

        // Alice changes a rule; both clients see it, in step with the server.
        alice.Client.Send(new SetAutoBuildCommand(alice.Client.Town.Id, false));
        Run(host, 3, T0, alice, bob);

        Assert.False(host.World.FindSettlement(alice.Client.Town.Id)!.Rules.AutoBuild);
        Assert.False(bob.Client.World!.FindSettlement(alice.Client.Town.Id)!.Rules.AutoBuild);
        Assert.Equal(host.World.Time, alice.Client.World!.Time);
        Assert.Equal(host.World.ComputeStateHash(), alice.Client.World.ComputeStateHash());
        Assert.Equal(host.World.ComputeStateHash(), bob.Client.World.ComputeStateHash());
        Assert.Equal(0, alice.Client.Resyncs + bob.Client.Resyncs);
    }

    [Fact]
    public void PlayersCanOnlyCommandTheirOwnSettlement()
    {
        var host = new WorldHost(NewWorld(), nowUtc: T0);
        var alice = Join(host, "alice", T0);
        var bob = Join(host, "bob", T0);

        CommandResult? result = null;
        bob.Client.CommandFinished += (_, r) => result = r;
        bob.Client.Send(new SetAutoBuildCommand(alice.Client.Town!.Id, false));
        host.Update(0, T0);
        bob.Client.Poll();

        Assert.False(result!.Value.Ok);
        Assert.True(host.World.FindSettlement(alice.Client.Town.Id)!.Rules.AutoBuild);
    }

    [Fact]
    public void SpeedFollowsTheWorldsPolicy()
    {
        var host = new WorldHost(NewWorld(s => s.SpeedPolicy = SpeedPolicy.Unanimous), nowUtc: T0);
        var alice = Join(host, "alice", T0);
        var bob = Join(host, "bob", T0);

        alice.Client.RequestSpeed(4);
        host.Update(0, T0);
        Assert.Equal(1, host.SpeedMultiplier); // Bob hasn't agreed

        bob.Client.RequestSpeed(2);
        host.Update(0, T0);
        Assert.Equal(2, host.SpeedMultiplier); // the slowest request wins
        bob.Client.Poll();
        Assert.Equal(2, bob.Client.Speed);

        host.World.Settings.SpeedPolicy = SpeedPolicy.Host;
        Assert.Equal(4, host.SpeedMultiplier); // first to join (Alice) hosts

        host.World.Settings.SpeedPolicy = SpeedPolicy.Fixed;
        Assert.Equal(1, host.SpeedMultiplier);
    }

    [Fact]
    public void EmptyWorldsPauseAndCatchUpWhenSomeoneReturns()
    {
        var host = new WorldHost(NewWorld(s =>
        {
            s.SecondsPerGameDay = 60;
            s.WhenEmpty = EmptyWorldPolicy.CatchUp;
            s.CatchUpCapDays = 5;
        }), nowUtc: T0);
        long start = host.World.Time.Tick;

        host.Update(3600, T0.AddHours(1)); // an hour with nobody there
        Assert.Equal(start, host.World.Time.Tick);

        // Two hours later (120 game days at this speed) Alice returns: the world catches up 5 days, the cap.
        var alice = Join(host, "alice", T0.AddHours(2));
        Assert.Equal(start + TimeScale.DaysToTicks(5), host.World.Time.Tick);
        Assert.Equal(host.World.Time, alice.Client.World!.Time);

        // A clock that went backwards owes nothing.
        Assert.Equal(0, WorldClock.CatchUpTicks(host.World.Settings, T0, T0.AddHours(-3)));
    }

    [Fact]
    public void PausedWorldsDontCatchUp()
    {
        var host = new WorldHost(NewWorld(s => s.WhenEmpty = EmptyWorldPolicy.Pause), nowUtc: T0);
        long start = host.World.Time.Tick;
        Join(host, "alice", T0.AddDays(3));
        Assert.Equal(start, host.World.Time.Tick);
    }

    [Fact]
    public void SinglePlayerWorldsCanBeShared()
    {
        // A single-player world has an unowned town; the host takes it over when they open it to friends.
        var world = new GameWorld(7);
        SettlementFounder.Found(world);
        var shared = SaveGame.FromJson(SaveGame.ToJson(world));
        var host = new WorldHost(shared, hostName: "carol", nowUtc: T0);

        var carol = Join(host, "carol", T0);
        Assert.Equal(world.Settlements[0].Id, carol.Client.Town!.Id);
        Assert.True(carol.Client.IsHost);
        var dave = Join(host, "dave", T0);
        Assert.Equal(2, host.World.Settlements.Count);
        Assert.False(dave.Client.IsHost);
    }

    [Fact]
    public void PlayersConnectOverTcp()
    {
        var host = new WorldHost(NewWorld(s => s.SecondsPerGameDay = 20), nowUtc: T0);
        using var listener = new TcpHostListener(host, 0, System.Net.IPAddress.Loopback);
        NetClient? client = null;
        using var connection = TcpLineConnection.Connect("127.0.0.1", listener.Port,
            line => client!.Receive(line), () => client!.Disconnected());
        client = new NetClient("alice", connection.Send);
        client.Hello();

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (client.World == null && DateTime.UtcNow < deadline)
        {
            host.Update(0.05);
            client.Poll();
            Thread.Sleep(10);
        }
        Assert.NotNull(client.World);
        Assert.Equal("alice", client.Town!.OwnerId);

        client.Send(new SetAutoBuildCommand(client.Town.Id, false));
        while (client.Town.Rules.AutoBuild && DateTime.UtcNow < deadline)
        {
            host.Update(0.05);
            client.Poll();
            Thread.Sleep(10);
        }
        Assert.False(client.Town.Rules.AutoBuild);
    }

    [Fact]
    public void FoundingSpotsRingTheFirstSettlement()
    {
        Assert.Equal((0, 0), FoundSettlementCommand.SpotOffset(0));
        var ring = Enumerable.Range(1, 8).Select(FoundSettlementCommand.SpotOffset).ToList();
        Assert.Equal(8, ring.Distinct().Count());
        Assert.All(ring, o => Assert.Equal(1, Math.Max(Math.Abs(o.Dx), Math.Abs(o.Dy))));
    }

    [Fact]
    public void WorldSettingsAreSaved()
    {
        var world = NewWorld(s =>
        {
            s.SecondsPerGameDay = 60;
            s.SpeedPolicy = SpeedPolicy.Unanimous;
            s.WhenEmpty = EmptyWorldPolicy.Run;
        });
        var copy = SaveGame.FromJson(SaveGame.ToJson(world));
        Assert.Equal(60, copy.Settings.SecondsPerGameDay);
        Assert.Equal(SpeedPolicy.Unanimous, copy.Settings.SpeedPolicy);
        Assert.Equal(EmptyWorldPolicy.Run, copy.Settings.WhenEmpty);
        Assert.Equal(T0, copy.LastActiveUtc);
        Assert.Equal("alice", copy.Settlements[0].OwnerId);
    }
}
