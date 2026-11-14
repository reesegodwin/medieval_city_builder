using Sim.Core.Commands;
using Sim.Core.Diplomacy;
using Sim.Core.Persistence;
using Sim.Core.Simulation;
using Sim.Core.Time;
using Sim.Net;

namespace Sim.Tests;

public class FactionTests
{
    private static (GameWorld World, Simulator Sim) NewWorld(int npcTowns)
    {
        var world = new GameWorld(42);
        world.Settings.NpcTowns = npcTowns;
        SettlementFounder.Found(world);
        return (world, new Simulator(world));
    }

    [Fact]
    public void WorldsHaveNoNeighboursUnlessAsked()
    {
        var (world, sim) = NewWorld(0);
        sim.RunDays(2);
        Assert.Single(world.Settlements);
        var realm = Assert.Single(world.Diplomacy.Factions);
        Assert.False(realm.IsNpc);
        Assert.Equal(realm.Id, world.Settlements[0].FactionId);
    }

    [Fact]
    public void NpcRealmsAreFoundedNearbyAndGovernThemselves()
    {
        var (world, sim) = NewWorld(2);
        sim.RunDays(3);
        Assert.Equal(3, world.Settlements.Count);
        var player = world.Settlements[0];
        Assert.False(Factions.IsNpc(world, player));
        foreach (var town in world.Settlements.Skip(1))
        {
            var f = Factions.Of(world, town)!;
            Assert.True(f.IsNpc);
            Assert.NotEmpty(f.Personality);
            Assert.True(town.Rules.AutoBuild);
            Assert.Null(town.OwnerId);
            Assert.True(Math.Max(Math.Abs(town.CenterX - player.CenterX), Math.Abs(town.CenterY - player.CenterY)) > 100);
        }
        Assert.Equal(3, world.Diplomacy.Factions.Select(f => f.Id).Distinct().Count());
        Assert.Contains(world.Events, e => e.Message.Contains("founded"));
    }

    [Fact]
    public void MoreRealmsAppearOverTheYearsUpToTheCap()
    {
        var (world, _) = NewWorld(3);
        var rules = world.Data.Factions;
        Assert.Equal(3, Factions.WantedNpcTowns(world, 0));
        Assert.Equal(4, Factions.WantedNpcTowns(world, rules.NewTownEveryDays));
        Assert.Equal(rules.MaxNpcTowns, Factions.WantedNpcTowns(world, rules.NewTownEveryDays * 50L));
        world.Settings.NpcTowns = rules.MaxNpcTowns + 2; // asked for more than the usual cap at the start: keep them
        Assert.Equal(rules.MaxNpcTowns + 2, Factions.WantedNpcTowns(world, rules.NewTownEveryDays * 50L));
    }

    [Fact]
    public void PlayersGetTheirOwnRealm()
    {
        var world = new GameWorld(42);
        Assert.True(world.Execute(new FoundSettlementCommand("alice")).Ok);
        var realm = Factions.Of(world, world.Settlements[0])!;
        Assert.Equal("alice", realm.PlayerName);
        Assert.False(realm.IsNpc);
        Assert.Contains("alice", realm.Name);
    }

    [Fact]
    public void TheFirstPlayerTakesTheirOwnTownNotAnNpcOne()
    {
        var (world, sim) = NewWorld(2);
        sim.RunDays(3);
        world.LastActiveUtc = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var host = new WorldHost(world, nowUtc: world.LastActiveUtc);
        var peer = new LoopbackPeer(host, "bob");
        peer.Client.Hello();
        host.Update(0, world.LastActiveUtc);
        peer.Client.Poll();
        Assert.True(peer.Client.Connected, peer.Client.Error);
        Assert.Equal("bob", world.Settlements[0].OwnerId);
        Assert.All(world.Settlements.Skip(1), s => Assert.Null(s.OwnerId));
        Assert.Equal("bob", Factions.Of(world, world.Settlements[0])!.PlayerName);
    }

    [Fact]
    public void RealmsSurviveSaveAndLoad()
    {
        var (world, sim) = NewWorld(2);
        sim.RunDays(3);
        var loaded = SaveGame.FromJson(SaveGame.ToJson(world));
        Assert.Equal(world.ComputeStateHash(), loaded.ComputeStateHash());
        Assert.Equal(world.Diplomacy.Factions.Select(f => f.Name), loaded.Diplomacy.Factions.Select(f => f.Name));
        Assert.Equal(world.Settlements.Select(s => s.FactionId), loaded.Settlements.Select(s => s.FactionId));
        Assert.Equal(2, loaded.Settings.NpcTowns);
    }
}
