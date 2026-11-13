using Sim.Core.Commands;
using Sim.Core.Systems;

namespace Sim.Tests;

public class EconomyTests
{
    [Fact]
    public void AdultsPayTaxesAndBuildingsCostUpkeep()
    {
        var (world, town, _) = Scenario.NewTown();
        var market = Scenario.Place(world, town, "market");
        Scenario.Finish(world, town, market);
        int coin = town.Stock.Get("coin");
        double tax = EconomySystem.DailyTax(world, town);
        double upkeep = EconomySystem.DailyUpkeep(town);
        Assert.True(tax > 0);
        Assert.Equal(world.Data.Building("market").Upkeep, upkeep, 6);

        for (int day = 0; day < 10; day++) EconomySystem.Daily(world, town);

        Assert.InRange(town.Stock.Get("coin"), coin + (int)(10 * (tax - upkeep)) - 2, coin + (int)(10 * (tax - upkeep)) + 2);
        Assert.False(town.UpkeepUnpaid);
    }

    [Fact]
    public void AnEmptyTreasuryHalvesOutputOfBuildingsWithUpkeep()
    {
        var (world, town, _) = Scenario.NewTown();
        var market = Scenario.Place(world, town, "market");
        Scenario.Finish(world, town, market);
        var farm = town.Buildings.First(b => b.Def.Id == "farm");
        town.Stock.TryTake("coin", town.Stock.Get("coin"));
        foreach (var c in town.Citizens.ToList()) town.RemoveCitizen(c); // no taxes either

        // Upkeep is fractional (0.4 coin a day for a market): a whole coin falls due after a few days.
        for (int day = 0; day < 3; day++) EconomySystem.Daily(world, town);

        Assert.True(town.UpkeepUnpaid);
        Assert.Equal(world.Data.Trade.UnpaidOutput, EconomySystem.OutputFactor(world, town, market));
        // Farms have no upkeep: only the season changes their output.
        Assert.Equal(SeasonSystem.Current(world).OutputOf("farm"), EconomySystem.OutputFactor(world, town, farm));

        town.Stock.Add("coin", 100);
        EconomySystem.Daily(world, town);
        Assert.False(town.UpkeepUnpaid);
    }

    [Fact]
    public void GrandBuildingsCostCoin()
    {
        var (world, town, _) = Scenario.NewTown();
        Assert.True(world.Data.Building("castle").Cost["coin"] > 0);
        Assert.True(world.Data.Building("town_hall").Cost["coin"] > 0);

        // A Town Hall construction site waits for its coin like any other material.
        foreach (var c in Enumerable.Range(0, 40).SelectMany(_ => CitizenFactory.CreateHousehold(world, 4))) town.AddCitizen(c);
        Assert.True(world.Execute(new UpgradeTownCentreCommand(town.Id)).Ok);
        town.Stock.Add("wood", 500);
        town.Stock.Add("stone", 500);
        town.Stock.TryTake("coin", town.Stock.Get("coin"));
        ConstructionSystem.Tick(world, town);
        Assert.Equal("Awaiting materials", town.TownCentre!.Problem);

        town.Stock.Add("coin", world.Data.Building("town_hall").Cost["coin"]);
        ConstructionSystem.Tick(world, town);
        Assert.True(town.TownCentre.State == Sim.Core.Settlements.BuildingState.UnderConstruction);
    }
}
