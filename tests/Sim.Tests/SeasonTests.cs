using Sim.Core.AI;
using Sim.Core.Economy;
using Sim.Core.Events;
using Sim.Core.Persistence;
using Sim.Core.Settlements;
using Sim.Core.Simulation;
using Sim.Core.Systems;
using Sim.Core.Time;

namespace Sim.Tests;

public class SeasonTests
{
    /// <summary>Runs the world forward to the given day of the year (1-based).</summary>
    private static void RunToDayOfYear(GameWorld world, Simulator sim, int dayOfYear)
    {
        while (world.Time.DayOfYear != dayOfYear || !world.Time.IsStartOfDay) sim.Step();
    }

    private static double FarmFactor(GameWorld world, Settlement town) =>
        EconomySystem.OutputFactor(world, town, town.Buildings.First(b => b.Def.Id == "farm"));

    [Fact]
    public void FarmsYieldLittleInWinterAndMostAtHarvest()
    {
        var (world, town, sim) = Scenario.NewTown();
        town.Rules.Disasters = false;
        var factors = new Dictionary<Season, double>();
        foreach (var season in new[] { Season.Spring, Season.Summer, Season.Autumn, Season.Winter })
        {
            RunToDayOfYear(world, sim, (int)season * TimeScale.DaysPerSeason + 2);
            factors[season] = FarmFactor(world, town);
        }
        Assert.True(factors[Season.Winter] < factors[Season.Spring]);
        Assert.True(factors[Season.Autumn] > factors[Season.Summer]);
        // Over the year farms make what they always did, so planning uses the average.
        Assert.Equal(1.0, factors.Values.Average(), 2);
        Assert.Equal(1.0, world.Data.Seasons.AverageOutputOf("farm"), 2);
    }

    [Fact]
    public void HomesBurnFirewoodInWinterAndGoColdWithout()
    {
        var (world, town, sim) = Scenario.NewTown();
        town.Rules.Disasters = false;
        RunToDayOfYear(world, sim, (int)Season.Winter * TimeScale.DaysPerSeason + 6);
        // A small town burns less than a unit a day, so look at the first days of winter together.
        Assert.True(town.Ledger.Recent(5).Sum(d => d.Get("wood", LedgerKeys.Heating)) < 0, "Firewood is burned");
        Assert.DoesNotContain(town.Buildings, b => b.Cold);

        town.Stock.TryTake("wood", town.Stock.Get("wood"));
        town.Rules.ProductionLimits["wood"] = 0; // woodcutters stop
        town.Rules.PerCitizenLimits["wood"] = 0;
        sim.RunDays(1);
        var cold = town.Buildings.Where(b => b.Cold).ToList();
        Assert.NotEmpty(cold);
        var resident = town.FindCitizen(cold[0].ResidentIds[0])!;
        Assert.Contains(Needs.MoodFactors(world, town, resident), f => f.Label == "Cold");

        RunToDayOfYear(world, sim, 2); // spring
        Assert.DoesNotContain(town.Buildings, b => b.Cold);
        Assert.DoesNotContain(Needs.MoodFactors(world, town, resident), f => f.Label == "Cold");
    }

    [Fact]
    public void EachSeasonIsAnnounced()
    {
        var (world, town, sim) = Scenario.NewTown();
        town.Rules.Disasters = false;
        sim.RunDays(TimeScale.DaysPerYear + 1);
        var seasons = world.Events.Where(e => e.Kind == EventKind.Season).Select(e => e.Message).ToList();
        Assert.Equal(4, seasons.Count);
        Assert.Contains(seasons, m => m.StartsWith("Winter has come"));
    }

    [Fact]
    public void TheGovernorBuildsGranariesBeforeWinter()
    {
        var (world, town, sim) = Scenario.NewTown(42, autoBuild: true);
        sim.RunDays(TimeScale.DaysPerSeason * 3 + 5);
        Assert.Contains(town.Buildings, b => b.Def.Id == "granary");
    }

    [Fact]
    public void HeatingSurvivesSaveAndLoad()
    {
        var (world, town, sim) = Scenario.NewTown(7, autoBuild: true);
        RunToDayOfYear(world, sim, (int)Season.Winter * TimeScale.DaysPerSeason + 10);
        town.Buildings.First(b => b.Def.Housing > 0).Cold = true;
        town.HeatingOwed = 0.4;
        var copy = SaveGame.FromJson(SaveGame.ToJson(world));
        Assert.True(copy.Settlements[0].Buildings.First(b => b.Def.Housing > 0).Cold);
        Assert.Equal(0.4, copy.Settlements[0].HeatingOwed);
        Assert.Equal(world.ComputeStateHash(), copy.ComputeStateHash());
        sim.RunDays(5);
        new Simulator(copy).RunDays(5);
        Assert.Equal(world.ComputeStateHash(), copy.ComputeStateHash());
    }
}
