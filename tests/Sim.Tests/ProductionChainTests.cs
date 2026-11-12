using Sim.Core.Persistence;
using Sim.Core.Settlements;
using Sim.Core.Simulation;
using Sim.Core.Systems;

namespace Sim.Tests;

public class ProductionChainTests
{
    [Fact]
    public void SmithyTurnsOreAndWoodIntoTools()
    {
        var (world, town, sim) = Scenario.NewTown();
        var smithy = Scenario.Place(world, town, "smithy");
        Scenario.Finish(world, town, smithy);
        town.Stock.Add("iron_ore", 20);
        town.Stock.Add("wood", 40);

        sim.RunDays(3);

        Assert.NotEmpty(smithy.WorkerIds);
        Assert.True(town.Stock.Get("tools") > 0, "The smith should have made tools");
        Assert.True(town.Stock.Get("iron_ore") < 20);
    }

    [Fact]
    public void ToolsMakeWorkFasterAndWearOut()
    {
        var (world, town, sim) = Scenario.NewTown();
        Assert.Equal(1.0, Tools.Multiplier(world, town));

        town.Stock.Add("tools", 5);
        Assert.Equal(1.25, Tools.Multiplier(world, town), 6);

        sim.RunDays(30); // a dozen workers at 0.01 tools per worker-day wear out a few
        Assert.True(town.Stock.Get("tools") < 5, "Tools should wear out with use");
    }

    [Fact]
    public void ClothingComesThroughMarketsWhileClothLasts()
    {
        var (world, town, _) = Scenario.NewTown();
        var market = Scenario.Place(world, town, "market");
        Scenario.Finish(world, town, market);
        JobSystem.Tick(world, town);
        Assert.NotEmpty(market.WorkerIds);

        // No cloth: market access, but no clothing.
        ServiceSystem.Daily(world, town);
        Assert.True(ServiceSystem.Coverage(town, "market") > 0);
        Assert.Equal(0, ServiceSystem.Coverage(town, "clothing"));

        town.Stock.Add("cloth", 3);
        ServiceSystem.Daily(world, town);
        Assert.Equal(ServiceSystem.Coverage(town, "market"), ServiceSystem.Coverage(town, "clothing"), 6);
        Assert.All(town.Citizens.Where(c => c.CoveredNeeds.Contains("clothing")),
            c => Assert.Contains("market", c.CoveredNeeds));

        // About 20 people use 0.01 cloth a day each, so 3 cloth lasts roughly two weeks.
        for (int day = 0; day < 40; day++) ServiceSystem.Daily(world, town);
        Assert.Equal(0, town.Stock.Get("cloth"));
        Assert.Equal(0, ServiceSystem.Coverage(town, "clothing"));
    }

    [Fact]
    public void GovernedTownBuildsItsProductionChains()
    {
        var (world, town, sim) = Scenario.NewTown(42, autoBuild: true);
        sim.RunDays(300);

        foreach (string id in new[] { "pasture", "weaver", "smithy" })
            Assert.Contains(town.Buildings, b => b.Def.Id == id);
        Assert.True(ServiceSystem.Coverage(town, "clothing") > 0.5, "Most people should have clothing");

        var copy = SaveGame.FromJson(SaveGame.ToJson(world));
        Assert.Equal(town.ToolWear, copy.Settlements[0].ToolWear);
        Assert.Equal(town.GoodsUse, copy.Settlements[0].GoodsUse);
        sim.RunDays(3);
        new Simulator(copy).RunDays(3);
        Assert.Equal(world.ComputeStateHash(), copy.ComputeStateHash());
    }
}
