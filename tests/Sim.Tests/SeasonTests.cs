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
        Assert.True(town.Ledger.Recent(5).Sum(d => d.Get("firewood", LedgerKeys.Heating)) < 0, "Firewood is burned");
        Assert.Equal(0, town.Ledger.Recent(5).Sum(d => d.Get("wood", LedgerKeys.Heating))); // timber is left for building
        Assert.DoesNotContain(town.Buildings, b => b.Cold);

        town.Stock.TryTake("firewood", town.Stock.Get("firewood"));
        town.Rules.ProductionLimits["firewood"] = 0; // firewood huts stop
        town.Rules.PerCitizenLimits["firewood"] = 0;
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

    // ---------- spoilage ----------

    private static void OnlyGrain(Settlement town, int amount)
    {
        foreach (var food in new[] { "grain", "bread", "vegetables", "fish" }) town.Stock.TryTake(food, town.Stock.Get(food));
        town.Stock.Add("grain", amount);
        town.SpoilageOwed.Clear();
    }

    [Fact]
    public void FoodOutsideGranariesRotsButNotInWinter()
    {
        var (world, town, _) = Scenario.NewTown();
        OnlyGrain(town, 1000);
        for (int day = 0; day < 10; day++) SeasonSystem.Spoil(world, town, 1.0);
        int rotted = 1000 - town.Stock.Get("grain");
        Assert.InRange(rotted, 25, 35); // 0.3% a day
        Assert.Equal(-rotted, town.Ledger.Today.Get("grain", LedgerKeys.Spoiled));

        OnlyGrain(town, 1000);
        for (int day = 0; day < 10; day++) SeasonSystem.Spoil(world, town, world.Data.Seasons.Of(Season.Winter).Spoilage);
        Assert.Equal(1000, town.Stock.Get("grain")); // frozen
    }

    [Fact]
    public void GranariesKeepFoodFromRotting()
    {
        var (world, town, _) = Scenario.NewTown();
        var granary = Scenario.Place(world, town, "granary");
        Scenario.Finish(world, town, granary);
        OnlyGrain(town, 1000); // fits in the granary
        for (int day = 0; day < 10; day++) SeasonSystem.Spoil(world, town, 1.5);
        Assert.Equal(1000, town.Stock.Get("grain"));

        OnlyGrain(town, 3000); // half of it outside: rots at half the rate
        for (int day = 0; day < 10; day++) SeasonSystem.Spoil(world, town, 1.0);
        Assert.InRange(3000 - town.Stock.Get("grain"), 40, 50);
    }

    // ---------- firewood ----------

    [Fact]
    public void FirewoodHutsGatherFirewoodNearForest()
    {
        var (world, town, sim) = Scenario.NewTown();
        var hut = Scenario.Place(world, town, "firewood_hut");
        Scenario.Finish(world, town, hut);
        town.Rules.JobPriority["firewood"] = 10;
        sim.RunDays(4);
        Assert.True(town.Ledger.Average("firewood", k => k == LedgerKeys.Made, 3) > 0);
    }

    // ---------- autumn food sales ----------

    [Fact]
    public void FoodAboveTheWinterReserveIsSoldInAutumnOnly()
    {
        var (world, town, sim) = Scenario.NewTown();
        town.Rules.Disasters = false;
        double reserve = SeasonSystem.WinterReserve(world, town);
        OnlyGrain(town, (int)(reserve * SeasonSystem.ReserveMargin) + 500);
        Assert.Equal(0, SeasonSystem.SellableFood(world, town, "grain")); // spring

        RunToDayOfYear(world, sim, (int)Season.Autumn * TimeScale.DaysPerSeason + 2);
        OnlyGrain(town, (int)(SeasonSystem.WinterReserve(world, town) * SeasonSystem.ReserveMargin) + 500);
        Assert.InRange(SeasonSystem.SellableFood(world, town, "grain"), 450, 500);
        Assert.Equal(0, SeasonSystem.SellableFood(world, town, "wood"));

        OnlyGrain(town, (int)SeasonSystem.WinterReserve(world, town)); // only the reserve: keep it all
        Assert.Equal(0, SeasonSystem.SellableFood(world, town, "grain"));
    }
}
