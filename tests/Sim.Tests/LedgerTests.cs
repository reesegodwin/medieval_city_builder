using Sim.Core.Economy;
using Sim.Core.Persistence;
using Sim.Core.Settlements;
using Sim.Core.Systems;

namespace Sim.Tests;

public class LedgerTests
{
    private static double TodaySum(Settlement s, string resource) => s.Ledger.Today.Sum(resource, _ => true);

    [Fact]
    public void TheBooksAccountForEveryChangeInStock()
    {
        var (world, town, sim) = Scenario.NewTown(42, autoBuild: true);
        sim.RunDays(120); // a busy town: building, trading, upgrading homes, taxes and upkeep
        long startDay = world.Time.Day;
        var resources = world.Data.Resources.Keys.ToList();
        var before = resources.ToDictionary(r => r, r => town.Stock.Get(r) - TodaySum(town, r));

        sim.RunDays(30);

        foreach (string r in resources)
        {
            double booked = town.Ledger.History.Where(d => d.Day >= startDay).Sum(d => d.Sum(r, _ => true)) + TodaySum(town, r);
            Assert.True(Math.Abs(town.Stock.Get(r) - before[r] - booked) < 1e-6,
                $"{r}: stock changed by {town.Stock.Get(r) - before[r]:0.##} but the books show {booked:0.##}");
        }
    }

    [Fact]
    public void ProductionAndEatingAreBooked()
    {
        var (world, town, sim) = Scenario.NewTown();
        sim.RunDays(5);
        Assert.True(town.Ledger.Average("grain", k => k == LedgerKeys.Made, 4) > 0, "Farms make grain");
        Assert.True(town.Ledger.Average("grain", k => k == LedgerKeys.Eaten, 4) + town.Ledger.Average("bread", k => k == LedgerKeys.Eaten, 4) < 0,
            "People eat");
    }

    [Fact]
    public void TaxesAreSplitByClassAndUpkeepByBuilding()
    {
        var (world, town, sim) = Scenario.NewTown();
        var market = Scenario.Place(world, town, "market");
        Scenario.Finish(world, town, market);
        sim.RunDays(6);

        var coin = town.Ledger.Breakdown(Inventory.Coin, 5);
        Assert.Contains(coin.Keys, k => k == LedgerKeys.Taxes_("Peasants"));
        Assert.Contains(coin.Keys, k => k == LedgerKeys.Upkeep_("Market"));
        Assert.True(coin[LedgerKeys.Taxes_("Peasants")] > 0);
        Assert.True(coin[LedgerKeys.Upkeep_("Market")] < 0);
    }

    [Fact]
    public void HistoryKeepsTheLastThirtyDays()
    {
        var (_, town, sim) = Scenario.NewTown();
        sim.RunDays(Ledger.HistoryDays + 10);
        Assert.Equal(Ledger.HistoryDays, town.Ledger.History.Count);
        Assert.True(town.Ledger.History.Zip(town.Ledger.History.Skip(1)).All(p => p.Second.Day == p.First.Day + 1));
    }

    [Fact]
    public void TheLedgerSurvivesSaveAndLoad()
    {
        var (world, town, sim) = Scenario.NewTown();
        sim.RunDays(10);
        var copy = SaveGame.FromJson(SaveGame.ToJson(world));
        var copyTown = copy.Settlements[0];
        Assert.Equal(town.Ledger.History.Count, copyTown.Ledger.History.Count);
        Assert.Equal(town.Ledger.Average("grain", k => k == LedgerKeys.Made), copyTown.Ledger.Average("grain", k => k == LedgerKeys.Made), 9);
        Assert.Equal(world.ComputeStateHash(), copy.ComputeStateHash());
    }
}
