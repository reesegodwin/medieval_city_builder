using Sim.Core.Diplomacy;
using Sim.Core.Persistence;
using Sim.Core.Settlements;
using Sim.Core.Simulation;
using Sim.Core.Time;
using Sim.Core.War;

namespace Sim.Tests;

public class FactionWarTests
{
    /// <summary>The player's town and NPC realms, past their founding protection, with nobody else's moves in the way.</summary>
    private static (GameWorld World, Simulator Sim, Settlement Me, Settlement Enemy) Land(int npc = 1)
    {
        var world = new GameWorld(42);
        world.Settings.NpcTowns = npc;
        SettlementFounder.Found(world);
        var sim = new Simulator(world);
        sim.RunDays(2);
        foreach (var f in world.Diplomacy.Factions) f.FoundedDay = -1000;
        foreach (var s in world.Settlements) s.Rules.Raids = false;
        world.Diplomacy.Proposals.Clear();
        return (world, sim, world.Settlements[0], world.Settlements[1]);
    }

    private static List<Squad> Soldiers(GameWorld world, Settlement town, int squads, string unit = "militia")
    {
        while (town.Citizens.Count(c => c.IsAdult) < squads * 10 + 10)
            foreach (var c in Sim.Core.Systems.CitizenFactory.CreateHousehold(world, 4)) town.AddCitizen(c);
        var raised = new List<Squad>();
        for (int i = 0; i < squads; i++) raised.Add(Armies.Raise(world, town, unit, 10, levy: false).Squad!);
        return raised;
    }

    [Fact]
    public void WarNeedsNoTreatyAndComesAfterAWarning()
    {
        var (world, _, me, enemy) = Land();
        Statecraft.Answer(world, Statecraft.Propose(world, me.FactionId, enemy.FactionId, ProposalKind.NonAggression), accept: true);
        Assert.Contains("non-aggression", world.Execute(new DeclareWarCommand(me.Id, enemy.FactionId)).Error);
        world.Diplomacy.Treaties.Clear();

        Factions.Of(world, enemy)!.FoundedDay = world.Time.Day; // newly founded: protected
        Assert.Contains("newly founded", world.Execute(new DeclareWarCommand(me.Id, enemy.FactionId)).Error);
        Factions.Of(world, enemy)!.FoundedDay = -1000;

        world.Settings.SecondsPerGameDay = 600; // 10 real minutes a day: the 60-minute floor means 6 days' warning
        Assert.True(world.Execute(new DeclareWarCommand(me.Id, enemy.FactionId)).Ok);
        var war = Assert.Single(world.Diplomacy.Wars);
        Assert.Equal(TimeScale.DaysToTicks(6), war.StartsTick - world.Time.Tick);
        Assert.False(FactionWar.Fighting(world, me.FactionId, enemy.FactionId));
        Soldiers(world, me, 2);
        Assert.Contains("can't begin", world.Execute(new MarchCommand(me.Id, enemy.Id)).Error);
        Assert.True(Statecraft.Opinion(world, me.FactionId, enemy.FactionId) < -30);
    }

    [Fact]
    public void ArmiesMarchFightWithRealSoldiersAndComeHome()
    {
        var (world, sim, me, enemy) = Land();
        enemy.Rules.AutoBuild = false; // keep the enemy's garrison out of it
        var squads = Soldiers(world, me, 4);
        int people = me.Citizens.Count;
        FactionWar.Declare(world, me.FactionId, enemy.FactionId);
        sim.RunTicks(FactionWar.WarningTicks(world) + 1);
        var march = world.Execute(new MarchCommand(me.Id, enemy.Id));
        Assert.True(march.Ok, march.Error);
        var army = world.War.Warbands.Single(b => b.Id == march.CreatedId);
        Assert.Equal(40, army.Size);
        Assert.All(squads, q => Assert.Equal(army.Id, q.ArmyId));
        Assert.DoesNotContain(squads, q => Raids.DefenderBlocks(world, me).Any(b => b.Citizens!.SequenceEqual(q.SoldierIds)));

        for (int day = 0; day < 80 && world.War.Warbands.Contains(army); day++) sim.RunDays(1);
        Assert.DoesNotContain(army, world.War.Warbands); // fought, then came home
        Assert.Contains(world.War.Reports, r => r.SettlementId == enemy.Id);
        Assert.All(me.Squads, q => Assert.Null(q.ArmyId));
        Assert.Contains(world.Events, e => e.SettlementId == me.Id && e.Message.Contains("came home"));
    }

    [Fact]
    public void PayingAnArmyOffMakesPeace()
    {
        var (world, _, me, enemy) = Land();
        var squads = Soldiers(world, enemy, 3);
        FactionWar.Declare(world, enemy.FactionId, me.FactionId);
        var army = FactionWar.Raise(world, enemy, me, squads);
        army.X = me.CenterX;
        army.Y = me.CenterY + Raids.ArriveRadius(me) - 1;
        Siege.Begin(world, me, army);
        me.Stock.Add("coin", 1000);
        int price = Siege.Tribute(world, army);
        Assert.True(world.Execute(new PayTributeCommand(me.Id)).Ok);
        Assert.Empty(world.Diplomacy.Wars);
        var peace = Assert.Single(world.Diplomacy.Treaties);
        Assert.Equal(TreatyKind.NonAggression, peace.Kind);
        Assert.True(peace.EndsDay > world.Time.Day);
        Assert.True(army.Returning);
        Assert.Equal(price, army.Loot["coin"]);
        Assert.False(Siege.Besieged(world, me));
    }

    [Fact]
    public void PeaceCanBeProposedAndLapsesLater()
    {
        var (world, sim, me, enemy) = Land();
        FactionWar.Declare(world, me.FactionId, enemy.FactionId);
        Assert.True(world.Execute(new Sim.Core.Diplomacy.ProposeCommand(me.Id, enemy.FactionId, ProposalKind.TradePact)).Error!.Contains("war"));
        var offer = Statecraft.Propose(world, me.FactionId, enemy.FactionId, ProposalKind.Peace);
        Statecraft.Answer(world, offer, accept: true);
        Assert.Empty(world.Diplomacy.Wars);
        var pact = Assert.Single(world.Diplomacy.Treaties);
        Assert.NotNull(FactionWar.CheckWar(world, me.FactionId, enemy.FactionId)); // can't go straight back to war
        sim.RunDays((int)(pact.EndsDay - world.Time.Day) + 1);
        Assert.DoesNotContain(world.Diplomacy.Treaties, t => t.Id == pact.Id);
    }

    [Fact]
    public void AlliesSendReliefThatBreaksASiege()
    {
        var (world, _, me, friend) = Land();
        Statecraft.Answer(world, Statecraft.Propose(world, me.FactionId, friend.FactionId, ProposalKind.Alliance), accept: true);
        var bandits = new Warband { Id = world.NextId(), TargetId = me.Id, Raiders = 6, X = me.CenterX, Y = me.CenterY + Raids.ArriveRadius(me) - 1 };
        world.War.Warbands.Add(bandits);
        Siege.Begin(world, me, bandits);
        var relief = FactionWar.Raise(world, friend, me, Soldiers(world, friend, 4), relief: true);
        relief.X = bandits.X;
        relief.Y = bandits.Y;
        Assert.False(Raids.Threatens(relief, me)); // a relief army is no threat
        FactionWar.Arrivals(world);
        Assert.DoesNotContain(bandits, world.War.Warbands); // broken
        Assert.True(relief.Returning);
        Assert.Contains(world.Events, e => e.Message.Contains("broke the siege"));
    }

    [Fact]
    public void WarsAndArmiesSurviveSaveAndLoad()
    {
        var (world, sim, me, enemy) = Land();
        var squads = Soldiers(world, me, 2);
        FactionWar.Declare(world, me.FactionId, enemy.FactionId);
        sim.RunTicks(FactionWar.WarningTicks(world) + 1);
        Assert.True(world.Execute(new MarchCommand(me.Id, enemy.Id)).Ok);
        sim.RunDays(2);
        var loaded = SaveGame.FromJson(SaveGame.ToJson(world));
        Assert.Equal(world.ComputeStateHash(), loaded.ComputeStateHash());
        var army = loaded.War.Warbands.Single(b => b.OwnerSettlementId == me.Id);
        Assert.Equal(squads.Select(q => q.Id), army.SquadIds);
        Assert.All(loaded.Settlements[0].Squads, q => Assert.Equal(army.Id, q.ArmyId));
        Assert.Single(loaded.Diplomacy.Wars);
    }
}
