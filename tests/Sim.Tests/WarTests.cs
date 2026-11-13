using Sim.Core.Systems;
using Sim.Core.Citizens;
using Sim.Core.Persistence;
using Sim.Core.Settlements;
using Sim.Core.Simulation;
using Sim.Core.War;

namespace Sim.Tests;

public class WarTests
{
    private static void Grow(GameWorld world, Settlement town, int people)
    {
        while (people > 0)
            foreach (var c in CitizenFactory.CreateHousehold(world, Math.Min(4, people)))
            {
                town.AddCitizen(c);
                people--;
            }
    }

    private static (GameWorld World, Settlement Town, Simulator Sim) TownWithBarracks()
    {
        var (world, town, sim) = Scenario.NewTown();
        town.Rules.Disasters = false;
        town.Rules.Raids = false;
        Grow(world, town, 40);
        town.Stock.Add("wood", 200);
        town.Stock.Add("stone", 200);
        var barracks = Scenario.Place(world, town, "barracks");
        Scenario.Finish(world, town, barracks);
        return (world, town, sim);
    }

    private static List<Block> Side(GameWorld world, string unit, int count) =>
        new() { new Block { Unit = world.Data.War.Unit(unit)!, Count = count } };

    [Fact]
    public void RecruitingTakesPeopleAndGearAndDisbandingGivesThemBack()
    {
        var (world, town, _) = TownWithBarracks();
        Assert.False(world.Execute(new RecruitSquadCommand(town.Id, "spearmen")).Ok); // no weapons yet

        town.Stock.Add("weapons", 10);
        var result = world.Execute(new RecruitSquadCommand(town.Id, "spearmen"));
        Assert.True(result.Ok, result.Error);
        var squad = Assert.Single(town.Squads);
        Assert.Equal(world.Data.War.SquadSize, squad.SoldierIds.Count);
        Assert.Equal(0, town.Stock.Get("weapons"));
        Assert.All(squad.SoldierIds, id =>
        {
            var c = town.FindCitizen(id)!;
            Assert.Equal(squad.Id, c.SquadId);
            Assert.False(c.CanWork);
            Assert.Null(c.JobBuildingId);
        });
        Assert.True(Sim.Core.Systems.EconomySystem.DailyUpkeep(world, town) >= Armies.DailyUpkeep(world, town));
        Assert.True(Armies.DailyUpkeep(world, town) > 0);

        Assert.True(world.Execute(new DisbandSquadCommand(town.Id, squad.Id)).Ok);
        Assert.Empty(town.Squads);
        Assert.Equal(10, town.Stock.Get("weapons"));
        Assert.All(town.Citizens, c => Assert.Null(c.SquadId));
    }

    [Fact]
    public void RecruitingNeedsRoomInABarracks()
    {
        var (world, town, _) = Scenario.NewTown();
        Grow(world, town, 20);
        Assert.False(world.Execute(new RecruitSquadCommand(town.Id, "militia")).Ok);
    }

    [Fact]
    public void AGeneralGetsTraitsAndDoesNotWork()
    {
        var (world, town, _) = TownWithBarracks();
        var pick = town.Citizens.First(c => c.IsAdult);
        Assert.True(world.Execute(new PromoteGeneralCommand(town.Id, pick.Id)).Ok);
        Assert.NotNull(town.General);
        Assert.Equal(pick.Id, town.General!.CitizenId);
        Assert.Equal(2, town.General.Traits.Count);
        Assert.All(town.General.Traits, t => Assert.NotNull(world.Data.War.Trait(t)));
        Assert.DoesNotContain(Armies.Recruitable(town), c => c.Id == pick.Id);
    }

    [Fact]
    public void BiggerArmiesUsuallyWin()
    {
        var (world, _, _) = Scenario.NewTown();
        int wins = 0;
        for (int i = 0; i < 40; i++)
            if (BattleResolver.Resolve(world, Side(world, "raider", 10), Side(world, "militia", 20), default).DefendersWon) wins++;
        Assert.True(wins >= 34, $"20 militia beat 10 raiders {wins}/40 times");

        wins = 0;
        for (int i = 0; i < 40; i++)
            if (BattleResolver.Resolve(world, Side(world, "raider", 20), Side(world, "militia", 10), default).DefendersWon) wins++;
        Assert.True(wins <= 6, $"10 militia beat 20 raiders {wins}/40 times");
    }

    [Fact]
    public void WallsAndGearHelpTheDefenders()
    {
        var (world, _, _) = Scenario.NewTown();
        int Wins(string unit, Fortification fort)
        {
            int wins = 0;
            for (int i = 0; i < 60; i++)
                if (BattleResolver.Resolve(world, Side(world, "raider", 18), Side(world, unit, 14), fort).DefendersWon) wins++;
            return wins;
        }
        int open = Wins("militia", default);
        Assert.True(Wins("militia", new Fortification(1, 4)) > open, "a stone wall and towers should help");
        Assert.True(Wins("men_at_arms", default) > open, "armoured soldiers should beat what militia can");
    }

    [Fact]
    public void ARaidRaisesTheAlarmMustersFightsAndStandsDown()
    {
        var (world, town, _) = TownWithBarracks();
        town.Rules.Raids = true;
        town.Rules.MusterPercent = 50;
        var band = new Warband
        {
            Id = world.NextId(), TargetId = town.Id, Raiders = 3, Archers = 0,
            X = town.CenterX + Raids.WatchRadius(town) - 1, Y = town.CenterY,
        };
        world.War.Warbands.Add(band);
        int people = town.Citizens.Count;
        var seen = new List<Alert>();

        for (int day = 0; day < 20 && world.War.Warbands.Count > 0; day++)
        {
            Raids.Daily(world);
            seen.Add(town.Alert);
            if (town.Alert == Alert.Muster) Assert.Contains(town.Squads, q => q.Levy);
        }
        Assert.Contains(Alert.Watch, seen);
        Assert.Contains(Alert.Muster, seen);
        Assert.Empty(world.War.Warbands);
        var report = Assert.Single(world.War.Reports);
        Assert.True(report.DefendersWon);
        Assert.True(town.Citizens.Count >= people - report.DefenderLosses);

        for (int day = 0; day < world.Data.War.Raids.StandDownDays + 2; day++) Raids.Daily(world);
        Assert.DoesNotContain(town.Squads, q => q.Levy);
        Assert.Equal(Alert.None, town.Alert);
    }

    [Fact]
    public void ArmiesAndWarbandsSurviveSaveAndLoad()
    {
        var (world, town, _) = TownWithBarracks();
        town.Stock.Add("bows", 10);
        Assert.True(world.Execute(new RecruitSquadCommand(town.Id, "archers")).Ok);
        Assert.True(world.Execute(new PromoteGeneralCommand(town.Id, town.Citizens.First(c => c.CanWork).Id)).Ok);
        world.War.Warbands.Add(new Warband { Id = world.NextId(), TargetId = town.Id, Raiders = 7, Archers = 2, X = 5.5, Y = -3 });

        var loaded = SaveGame.FromJson(SaveGame.ToJson(world));
        Assert.Equal(world.ComputeStateHash(), loaded.ComputeStateHash());
        var t = loaded.Settlements[0];
        Assert.Equal(town.Squads[0].SoldierIds, t.Squads[0].SoldierIds);
        Assert.Equal(town.General!.Traits, t.General!.Traits);
        Assert.Equal(9, loaded.War.Warbands[0].Size);
    }
}
