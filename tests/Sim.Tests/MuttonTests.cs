using Sim.Core.Economy;
using Sim.Core.Systems;

namespace Sim.Tests;

public class MuttonTests
{
    [Fact]
    public void PasturesRaiseSheepForWoolAndMutton()
    {
        var (world, town, sim) = Scenario.NewTown();
        town.Rules.Disasters = false;
        var pasture = Scenario.Place(world, town, "pasture");
        Scenario.Finish(world, town, pasture);
        town.Rules.JobPriority["shepherd"] = 10;
        sim.RunDays(5);

        Assert.True(town.Ledger.Average("mutton", k => k == LedgerKeys.Made, 4) > 0, "Mutton is made");
        Assert.True(town.Ledger.Average("wool", k => k == LedgerKeys.Made, 4) > 0, "Wool is made");
        Assert.True(town.Ledger.Average("mutton", k => k == LedgerKeys.Eaten, 4) < 0, "Mutton is eaten");
        Assert.True(world.Data.Resources["mutton"].IsFood);
    }

    [Fact]
    public void AFullWoolStoreDoesNotStopTheMutton()
    {
        var (world, town, sim) = Scenario.NewTown();
        town.Rules.Disasters = false;
        var pasture = Scenario.Place(world, town, "pasture");
        Scenario.Finish(world, town, pasture);
        town.Rules.JobPriority["shepherd"] = 10;
        town.Stock.Add("wool", town.LimitOf("wool"));
        int wool = town.Stock.Get("wool");
        sim.RunDays(5);

        Assert.Equal(0, town.Ledger.Recent(4).Sum(d => d.Get("wool", LedgerKeys.Made)));
        Assert.True(town.Ledger.Average("mutton", k => k == LedgerKeys.Made, 4) > 0);
        Assert.True(town.Stock.Get("wool") <= wool, "No wool beyond the limit");
    }

    [Fact]
    public void TheGovernorKeepsSheepAndCountsTheMutton()
    {
        var (world, town, sim) = Scenario.NewTown(42, autoBuild: true);
        sim.RunDays(160);
        Assert.Contains(town.Buildings, b => b.Def.Id == "pasture");
        double withSheep = Sim.Core.AI.SettlementGovernor.EstimatedFoodSupply(world, town);
        foreach (var b in town.Buildings.Where(b => b.Def.Id == "pasture").ToList()) town.RemoveBuilding(b);
        Assert.True(Sim.Core.AI.SettlementGovernor.EstimatedFoodSupply(world, town) < withSheep);
    }
}
