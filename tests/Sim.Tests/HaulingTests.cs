using Sim.Core.Citizens;
using Sim.Core.Commands;
using Sim.Core.Persistence;
using Sim.Core.Settlements;
using Sim.Core.Simulation;
using Sim.Core.Systems;
using Sim.Core.Time;

namespace Sim.Tests;

public class HaulingTests
{
    private static void AddAdults(GameWorld world, Settlement town, int count)
    {
        for (int i = 0; i < count; i++)
            town.AddCitizen(CitizenFactory.Create(world, "Spare", 25L * TimeScale.DaysPerYear));
    }

    private static List<Citizen> Haulers(Settlement town) => town.Citizens
        .Where(c => c.JobBuildingId is int j && town.FindBuilding(j)?.CurrentJobKind == JobKinds.Hauler)
        .ToList();

    [Fact]
    public void SpareAdultsHaulOnlyAsMuchAsThereIsToCarry()
    {
        var (world, town, _) = Scenario.NewTown();
        AddAdults(world, town, 30);
        JobSystem.Tick(world, town);

        int haulers = Haulers(town).Count;
        int needed = (int)Math.Ceiling(Logistics.HaulingDemand(town) - 1e-9);
        Assert.True(haulers > 0, "Spare adults should take up hauling");
        Assert.True(haulers <= needed, $"{haulers} haulers for {needed} worker-days of carrying");
        Assert.True(town.Citizens.Any(c => c.IsAdult && c.JobBuildingId == null), "The rest stay jobless");
        Assert.True(town.HaulCoverage > 0);
    }

    [Fact]
    public void HaulersWinBackTimeLostToCarrying()
    {
        var (_, town, _) = Scenario.NewTown();
        var farm = town.Buildings.First(b => b.Def.Id == "farm");
        farm.HaulFactor = 0.6;

        town.HaulCoverage = 0;
        Assert.Equal(0.6, Logistics.EffectiveHaulFactor(town, farm), 6);
        town.HaulCoverage = 0.5;
        Assert.Equal(0.8, Logistics.EffectiveHaulFactor(town, farm), 6);
        town.HaulCoverage = 1;
        Assert.Equal(1.0, Logistics.EffectiveHaulFactor(town, farm), 6);

        farm.NoRoute = true; // haulers can't reach it either
        Assert.Equal(0.6, Logistics.EffectiveHaulFactor(town, farm), 6);
    }

    [Fact]
    public void HaulersAreTheFirstToTakeANewJob()
    {
        var (world, town, _) = Scenario.NewTown();
        AddAdults(world, town, 4);
        JobSystem.Tick(world, town);
        var spare = Haulers(town).Concat(town.Citizens.Where(c => c.IsAdult && c.JobBuildingId == null))
            .Select(c => c.Id).ToHashSet();
        Assert.NotEmpty(Haulers(town));

        var farm = Scenario.Place(world, town, "farm");
        Scenario.Finish(world, town, farm);
        JobSystem.Tick(world, town);

        Assert.Equal(farm.JobSlots, farm.WorkerIds.Count);
        Assert.All(farm.WorkerIds, id => Assert.Contains(id, spare));
    }

    [Fact]
    public void NewcomersStayAwayWhenThereIsNoWork()
    {
        var (world, town, _) = Scenario.NewTown();
        int adults = town.Citizens.Count(c => c.IsAdult);
        AddAdults(world, town, adults); // far more people than jobs
        JobSystem.Tick(world, town);
        Assert.False(MigrationSystem.HasWorkForNewcomers(town));

        // Plenty of room and food, but no work: nobody arrives.
        for (int i = 0; i < 6; i++) Scenario.Finish(world, town, Scenario.Place(world, town, "hovel"));
        town.Stock.Add("bread", 2000);
        int before = town.Citizens.Count;
        for (int day = 0; day < 30; day++) MigrationSystem.Daily(world, town);
        Assert.True(town.Citizens.Count <= before);
        Assert.DoesNotContain(world.Events, e => e.Kind == Sim.Core.Events.EventKind.Arrival);
    }

    [Fact]
    public void HaulingSurvivesSaveAndLoad()
    {
        var (world, town, sim) = Scenario.NewTown(autoBuild: true);
        AddAdults(world, town, 10);
        sim.RunDays(10);
        Assert.NotEmpty(Haulers(town));

        var copy = SaveGame.FromJson(SaveGame.ToJson(world));
        Assert.Equal(town.HaulCoverage, copy.Settlements[0].HaulCoverage);
        Assert.Equal(world.ComputeStateHash(), copy.ComputeStateHash());

        sim.RunDays(5);
        new Simulator(copy).RunDays(5);
        Assert.Equal(world.ComputeStateHash(), copy.ComputeStateHash());
    }
}
