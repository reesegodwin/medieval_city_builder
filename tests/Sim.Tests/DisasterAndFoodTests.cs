using Sim.Core.AI;
using Sim.Core.Commands;
using Sim.Core.Economy;
using Sim.Core.Events;
using Sim.Core.Persistence;
using Sim.Core.Settlements;
using Sim.Core.Simulation;
using Sim.Core.Systems;
using Sim.Core.Time;
using Sim.Core.World;

namespace Sim.Tests;

public class LedgerChartTests
{
    [Fact]
    public void SeriesHasOneValuePerDayMatchingTheBooks()
    {
        var (_, town, sim) = Scenario.NewTown();
        sim.RunDays(Ledger.HistoryDays + 5);
        var made = town.Ledger.Series("grain", k => k == LedgerKeys.Made);
        Assert.Equal(Ledger.HistoryDays, made.Length);
        for (int i = 0; i < made.Length; i++)
            Assert.Equal(town.Ledger.History[i].Get("grain", LedgerKeys.Made), made[i]);
    }

    [Fact]
    public void StockSeriesEndsAtYesterdaysStock()
    {
        var (_, town, sim) = Scenario.NewTown();
        sim.RunDays(10);
        var series = town.Ledger.StockSeries("wood", town.Stock.Get("wood"));
        Assert.Equal(town.Ledger.History.Count, series.Length);
        Assert.Equal(town.Stock.Get("wood") - town.Ledger.Today.Sum("wood", _ => true), series[^1], 6);
        // Each day's change is that day's net flow.
        for (int i = 1; i < series.Length; i++)
            Assert.Equal(town.Ledger.History[i].Sum("wood", _ => true), series[i] - series[i - 1], 6);
    }
}

public class ForecastTests
{
    private static void Book(Settlement s, string resource, double perDay, int days)
    {
        for (int d = 0; d < days; d++)
        {
            s.Ledger.Record(resource, perDay, LedgerKeys.Used);
            s.Ledger.EndDay(s.Ledger.Today.Day + 1);
        }
    }

    [Fact]
    public void DaysLeftIsStockOverTheWeeklyLoss()
    {
        var (world, town, _) = Scenario.NewTown();
        town.Ledger = new Ledger();
        town.Stock.TryTake("stone", town.Stock.Get("stone"));
        town.Stock.Add("stone", 100);
        Book(town, "stone", -10, 7);
        Assert.Equal(10, Forecast.DaysLeft(world, town, "stone"), 6);

        Book(town, "stone", 0, 7); // steady: never runs out
        Assert.True(double.IsPositiveInfinity(Forecast.DaysLeft(world, town, "stone")));
    }

    [Fact]
    public void AWarningIsGivenOnceUntilTheGoodRecovers()
    {
        var (world, town, _) = Scenario.NewTown();
        town.Ledger = new Ledger();
        town.Stock.TryTake("stone", town.Stock.Get("stone"));
        town.Stock.Add("stone", 50);
        Book(town, "stone", -10, 7);

        int Warnings() => world.Events.Count(e => e.Kind == EventKind.Shortage && e.Message.StartsWith("Stone runs out"));
        Forecast.Daily(world, town);
        Forecast.Daily(world, town);
        Assert.Equal(1, Warnings());
        Assert.Contains("stone", town.ShortageWarnings);

        Book(town, "stone", 0, 7); // recovered
        Forecast.Daily(world, town);
        Assert.DoesNotContain("stone", town.ShortageWarnings);
        Book(town, "stone", -10, 7);
        Forecast.Daily(world, town);
        Assert.Equal(2, Warnings());
    }
}

public class FishingTests
{
    /// <summary>A seed whose starting town has water within its land, and a spot for a fishing hut.</summary>
    private static (GameWorld, Settlement, Simulator, (int X, int Y)) TownByWater()
    {
        for (ulong seed = 1; seed < 60; seed++)
        {
            var (world, town, sim) = Scenario.NewTown(seed);
            if (SiteFinder.Find(world, town, world.Data.Building("fishing_hut")) is { } site) return (world, town, sim, site);
        }
        throw new InvalidOperationException("No town by the water");
    }

    [Fact]
    public void FishingHutsMustBeNearWater()
    {
        var (world, town, _) = Scenario.NewTown();
        var def = world.Data.Building("fishing_hut");
        var dry = town.Territory.First(t =>
            world.Map.CountTerrainInRadius(t.X + 1, t.Y + 1, def.NearTerrain!.Radius, Terrain.Water) == 0 &&
            Placement.Check(world, town, world.Data.Building("well"), t.X, t.Y) == null &&
            Placement.Check(world, town, world.Data.Building("woodcutter"), t.X, t.Y) is null or "Needs Forest nearby");
        Assert.Equal("Needs Water nearby", Placement.Check(world, town, def, dry.X, dry.Y));
    }

    [Fact]
    public void FishIsCaughtEatenAndAddsVariety()
    {
        var (world, town, sim, site) = TownByWater();
        var result = world.Execute(new PlaceBuildingCommand(town.Id, "fishing_hut", site.X, site.Y));
        Assert.True(result.Ok, result.Error);
        Scenario.Finish(world, town, town.FindBuilding(result.CreatedId!.Value)!);
        town.Rules.JobPriority["fisher"] = 10;
        sim.RunDays(8);

        Assert.True(town.Ledger.Average("fish", k => k == LedgerKeys.Made, 5) > 0, "The hut catches fish");
        Assert.True(town.Ledger.Average("fish", k => k == LedgerKeys.Eaten, 5) < 0, "People eat fish");
        Assert.Contains(town.Citizens, c => c.RecentFoods.ContainsKey("fish"));
        Assert.True(SettlementGovernor.EstimatedFoodSupply(world, town) > 0);
    }
}

public class DisasterTests
{
    [Fact]
    public void WithDisastersOffNothingHappens()
    {
        var (world, town, sim) = Scenario.NewTown(7, autoBuild: true);
        town.Rules.Disasters = false;
        sim.RunDays(TimeScale.DaysPerYear * 2);
        Assert.DoesNotContain(world.Events, e => e.Kind == EventKind.Disaster);
        Assert.DoesNotContain(town.Citizens, c => c.IsSick);
    }

    [Fact]
    public void ABurnedBuildingIsRepairedWithLabourOnly()
    {
        var (world, town, sim) = Scenario.NewTown();
        var farm = town.Buildings.First(b => b.Def.Id == "farm" && b.IsOperational);
        var wood = town.Stock.Get("wood");
        DisasterSystem.Burn(world, town, farm, world.Data.Disasters);

        Assert.Equal(BuildingState.UnderConstruction, farm.State);
        Assert.Empty(farm.WorkerIds);
        Assert.Contains(world.Events, e => e.Kind == EventKind.Disaster && e.Message.Contains("Fire"));
        sim.RunDays(3);
        Assert.True(farm.IsOperational, "Builders repair it");
        Assert.True(town.Stock.Get("wood") >= wood - 1, "Repairs cost no materials");
    }

    [Fact]
    public void AStockpileFireBurnsSomeGoodsAndBooksThem()
    {
        var (world, town, _) = Scenario.NewTown();
        var stockpile = town.Buildings.First(b => b.Def.IsStockpile && b.IsOperational);
        int grain = town.Stock.Get("grain");
        DisasterSystem.Burn(world, town, stockpile, world.Data.Disasters);
        int lost = grain - town.Stock.Get("grain");
        Assert.True(lost > 0);
        Assert.Equal(-lost, town.Ledger.Today.Get("grain", LedgerKeys.Disaster_("fire")));
    }

    [Fact]
    public void SickPeopleDontWorkAndGetBetter()
    {
        var (world, town, sim) = Scenario.NewTown();
        sim.RunDays(1);
        var worker = town.Citizens.First(c => c.JobBuildingId != null);
        worker.SickDays = 3;
        sim.RunTicks(TimeScale.TicksPerHour);
        Assert.Null(worker.JobBuildingId);
        Assert.Contains(Needs.MoodFactors(world, town, worker), f => f.Label == "Sick");
        sim.RunTicks(TimeScale.TicksPerHour * 12);
        Assert.Null(worker.JobBuildingId); // no job is handed to someone sick
        sim.RunDays(4);
        Assert.False(worker.IsSick);
        Assert.True(worker.CanWork);
        Assert.DoesNotContain(Needs.MoodFactors(world, town, worker), f => f.Label == "Sick");
    }

    [Fact]
    public void APoorHarvestLowersFarmOutput()
    {
        double Grain(bool poor)
        {
            var (world, town, sim) = Scenario.NewTown();
            town.Rules.Disasters = false; // keep both runs identical apart from the harvest
            town.PoorHarvestDays = poor ? 100 : 0;
            sim.RunDays(6);
            return town.Ledger.Average("grain", k => k == LedgerKeys.Made, 5);
        }
        Assert.True(Grain(true) < Grain(false) * 0.85);
    }

    [Fact]
    public void DisastersAreDeterministicAndSurviveSaveAndLoad()
    {
        GameWorld Run()
        {
            var (world, town, sim) = Scenario.NewTown(1234, autoBuild: true);
            sim.RunDays(200);
            return world;
        }
        var a = Run();
        Assert.Equal(a.ComputeStateHash(), Run().ComputeStateHash());

        var town = a.Settlements[0];
        town.Citizens[0].SickDays = 4;
        town.PoorHarvestDays = 12;
        town.Rules.Disasters = false;
        town.ShortageWarnings.Add("wood");
        var copy = SaveGame.FromJson(SaveGame.ToJson(a));
        var copyTown = copy.Settlements[0];
        Assert.Equal(4, copyTown.Citizens[0].SickDays);
        Assert.Equal(12, copyTown.PoorHarvestDays);
        Assert.False(copyTown.Rules.Disasters);
        Assert.Contains("wood", copyTown.ShortageWarnings);
        Assert.Equal(a.ComputeStateHash(), copy.ComputeStateHash());

        new Simulator(a).RunDays(20);
        new Simulator(copy).RunDays(20);
        Assert.Equal(a.ComputeStateHash(), copy.ComputeStateHash());
    }

    [Fact]
    public void TheDisasterSwitchIsACommand()
    {
        var (world, town, _) = Scenario.NewTown();
        var command = CommandCodec.Decode(CommandCodec.Encode(new SetDisastersCommand(town.Id, false)));
        Assert.True(world.Execute(command).Ok);
        Assert.False(town.Rules.Disasters);
    }
}
