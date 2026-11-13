using Sim.Core.Commands;
using Sim.Core.Economy;
using Sim.Core.Persistence;
using Sim.Core.Settlements;
using Sim.Core.Simulation;
using Sim.Core.Systems;
using Sim.Core.Time;

namespace Sim.Tests;

public class OddJobTests
{
    /// <summary>Takes everyone off their jobs and keeps them off (no free job slots).</summary>
    private static void NoJobs(Settlement town)
    {
        foreach (var b in town.Buildings) Work.ReleaseWorkers(town, b);
        foreach (var kind in town.Rules.JobPriority.Keys.ToList()) town.Rules.JobPriority[kind] = -100;
    }

    [Fact]
    public void PeopleWithoutAJobHelpAtBuildingSites()
    {
        double Progress(bool helpers)
        {
            var (world, town, _) = Scenario.NewTown();
            var site = Scenario.Place(world, town, "chapel");
            site.State = BuildingState.UnderConstruction;
            Work.ReleaseWorkers(town, site);
            if (!helpers) foreach (var c in town.Citizens) c.JobBuildingId ??= -1; // nobody free
            Assert.Equal(helpers ? Math.Min(site.Def.BuilderSlots, OddJobs.People(town)) : 0, OddJobs.Allocate(world, town).HelpersAt(site));
            for (int i = 0; i < TimeScale.TicksPerHour * 6; i++) ConstructionSystem.Tick(world, town);
            return site.ConstructionProgress;
        }
        Assert.Equal(0, Progress(false));
        Assert.True(Progress(true) > 0, "Helpers build");
    }

    [Fact]
    public void HelpersLayPlannedRoads()
    {
        var (world, town, _) = Scenario.NewTown();
        var crew = town.TownCentre!;
        Work.ReleaseWorkers(town, crew); // no roadworkers
        int edge = town.Territory.Where(t => t.Y == town.CenterY).Max(t => t.X);
        var tiles = Enumerable.Range(edge - 6, 4).Select(x => (x, town.CenterY + 3))
            .Where(t => Roads.Check(world, town, t.Item1, t.Item2) == null).ToList();
        Assert.True(world.Execute(new PlaceRoadCommand(town.Id, tiles)).Ok);
        int planned = town.RoadPlans.Count;
        Assert.True(planned > 0);
        Assert.True(OddJobs.Allocate(world, town).Roads > 0);
        for (int i = 0; i < TimeScale.TicksPerDay * 3; i++) RoadSystem.Tick(world, town);
        Assert.True(town.RoadPlans.Count < planned, "Helpers finish road tiles without roadworkers");
    }

    [Fact]
    public void GatherersBringInWhatIsWantedAndNoBerriesInWinter()
    {
        var (world, town, _) = Scenario.NewTown();
        NoJobs(town);
        town.Stock.TryTake("firewood", town.Stock.Get("firewood"));
        int stone = town.Stock.Get("stone");
        Assert.True(OddJobs.Allocate(world, town).Gatherers > 0);

        OddJobs.Daily(world, town);
        Assert.True(town.Ledger.Today.Get("berries", LedgerKeys.Gathered) > 0);
        Assert.True(town.Ledger.Today.Get("firewood", LedgerKeys.Gathered) > 0);
        Assert.True(town.Stock.Get("stone") > stone);

        // Never above the limit.
        town.Stock.Add("firewood", town.LimitOf("firewood") - town.Stock.Get("firewood"));
        int firewood = town.Stock.Get("firewood");
        OddJobs.Daily(world, town);
        Assert.Equal(firewood, town.Stock.Get("firewood"));

        var sim = new Simulator(world);
        while (world.Time.Season != Season.Winter) sim.RunDays(10);
        NoJobs(town);
        Assert.DoesNotContain(OddJobs.GatherTasks(world, town), g => g.Resource == "berries");
    }

    [Fact]
    public void OddJobsAreNeitherIdleNorWork()
    {
        var (world, town, sim) = Scenario.NewTown();
        NoJobs(town);
        sim.RunTicks(TimeScale.TicksPerHour);
        var adult = town.Citizens.First(c => c.CanWork && c.JobBuildingId == null);
        var factors = Needs.MoodFactors(world, town, adult);
        Assert.Contains(factors, f => f.Label == "Odd jobs" && f.Mood == 0);
        Assert.DoesNotContain(factors, f => f.Label == "Idle");
    }

    [Fact]
    public void OddJobsStayInStepAfterSaveAndLoad()
    {
        var (world, town, sim) = Scenario.NewTown(7, autoBuild: true);
        sim.RunDays(120);
        foreach (var kind in new[] { "farmer", "baker" }) town.Rules.JobPriority[kind] = -100;
        var copy = SaveGame.FromJson(SaveGame.ToJson(world));
        Assert.Equal(world.ComputeStateHash(), copy.ComputeStateHash());
        sim.RunDays(10);
        new Simulator(copy).RunDays(10);
        Assert.Equal(world.ComputeStateHash(), copy.ComputeStateHash());
    }

    [Fact]
    public void GatherersLeaveRoomForWhatWorkplacesMake()
    {
        var (world, town, _) = Scenario.NewTown();
        NoJobs(town);
        for (int day = 0; day < 60; day++) OddJobs.Daily(world, town);
        Assert.True(town.FreeStorage >= OddJobs.KeepFree(town), $"free {town.FreeStorage} of {town.StorageCapacity}");
    }

    [Fact]
    public void UnpaidUpkeepDoesNotBuildMoreServicesWithUpkeep()
    {
        var (world, town, sim) = Scenario.NewTown(42, autoBuild: true);
        sim.RunDays(120);
        town.UpkeepUnpaid = true;
        var wishes = Sim.Core.AI.SettlementGovernor.Wishes(world, town).Select(w => w.DefId).ToList();
        Assert.DoesNotContain("tavern", wishes);
        Assert.DoesNotContain("market", wishes);
        Assert.DoesNotContain("chapel", wishes);
    }
}
