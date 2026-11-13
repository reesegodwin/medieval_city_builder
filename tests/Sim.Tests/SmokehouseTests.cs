using Sim.Core.Economy;
using Sim.Core.Settlements;
using Sim.Core.Systems;

namespace Sim.Tests;

public class SmokehouseTests
{
    private static (Sim.Core.Simulation.GameWorld, Settlement, Sim.Core.Simulation.Simulator, Building) TownWithSmokehouse()
    {
        var (world, town, sim) = Scenario.NewTown();
        town.Rules.Disasters = false;
        var smokehouse = Scenario.Place(world, town, "smokehouse");
        Scenario.Finish(world, town, smokehouse);
        town.Rules.JobPriority["smoker"] = 20;
        return (world, town, sim, smokehouse);
    }

    [Fact]
    public void SmokesWhicheverOfMuttonOrFishThereIsMoreOf()
    {
        var (world, town, sim, _) = TownWithSmokehouse();
        town.Stock.Add("firewood", 50);
        town.Stock.Add("fish", 300);
        town.Stock.Add("mutton", 10);
        sim.RunDays(2);
        Assert.True(town.Ledger.History.Sum(d => d.Get("smoked_meat", LedgerKeys.Made)) > 0, "Smoked meat is made");
        Assert.True(town.Ledger.History.Sum(d => d.Get("fish", LedgerKeys.Inputs)) < 0, "From the fish");
        Assert.Equal(0, town.Ledger.History.Sum(d => d.Get("mutton", LedgerKeys.Inputs)));
        Assert.True(town.Ledger.History.Sum(d => d.Get("firewood", LedgerKeys.Inputs)) < 0, "Burning firewood");
    }

    [Fact]
    public void WithoutMeatOrFishItWaits()
    {
        var (world, town, sim, smokehouse) = TownWithSmokehouse();
        foreach (var food in new[] { "fish", "mutton" }) town.Stock.TryTake(food, town.Stock.Get(food));
        town.Stock.Add("firewood", 50);
        sim.RunTicks(Sim.Core.Time.TimeScale.TicksPerHour * 3);
        Assert.Equal("Needs mutton or fish", smokehouse.Problem);
    }

    [Fact]
    public void SmokedMeatIsTastyAndKeeps()
    {
        var (world, _, _) = Scenario.NewTown();
        var smoked = world.Data.Resources["smoked_meat"];
        Assert.True(smoked.IsFood);
        Assert.True(smoked.MealMood > world.Data.Resources["mutton"].MealMood);
        Assert.True(smoked.SpoilPerDay < world.Data.Resources["mutton"].SpoilPerDay / 10);
    }

    [Fact]
    public void BigTownsBuildSmokehouses()
    {
        var (world, town, sim) = Scenario.NewTown(42, autoBuild: true);
        sim.RunDays(300);
        Assert.Contains(town.Buildings, b => b.Def.Id == "smokehouse");
    }
}
