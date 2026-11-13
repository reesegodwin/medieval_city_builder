using System.Diagnostics;
using Sim.Core.Citizens;
using Sim.Core.Economy;
using Sim.Core.Persistence;
using Sim.Core.Settlements;
using Sim.Core.Simulation;
using Sim.Core.Systems;
using Sim.Core.War;

namespace Sim.Tests;

public class HouseholdTests
{
    /// <summary>A working adult of the town, moved to a job of the given building kind (placed and finished).</summary>
    private static Citizen Worker(GameWorld world, Settlement town, string buildingId, double skill)
    {
        town.Stock.Add("wood", 200);
        town.Stock.Add("stone", 200);
        town.Stock.Add("coin", 200);
        var b = Scenario.Place(world, town, buildingId);
        Scenario.Finish(world, town, b);
        var c = town.Citizens.First(x => x.CanWork && x.JobBuildingId == null || x.CanWork && x.JobBuildingId != b.Id);
        if (c.JobBuildingId is int old) town.FindBuilding(old)!.WorkerIds.Remove(c.Id);
        c.JobBuildingId = b.Id;
        b.WorkerIds.Add(c.Id);
        c.Skills[b.CurrentJobKind!] = skill;
        return c;
    }

    [Fact]
    public void SkilledJobsAndSkilledHandsPayMore()
    {
        var (world, town, _) = Scenario.NewTown();
        var carpenter = Worker(world, town, "carpenter", 90);
        var (masterPay, city) = HouseholdSystem.Wage(world, town, carpenter);
        Assert.False(city);

        carpenter.Skills["carpenter"] = 0;
        Assert.True(HouseholdSystem.Wage(world, town, carpenter).Pay < masterPay);

        var farmer = town.Citizens.First(c => c.JobBuildingId is int j && town.FindBuilding(j)!.CurrentJobKind == "farmer");
        farmer.Skills["farmer"] = 90;
        Assert.True(HouseholdSystem.Wage(world, town, farmer).Pay < masterPay);

        // A commune pays everyone alike.
        Assert.True(world.Execute(new SetGovernmentCommand(town.Id, "commune")).Ok);
        Assert.Equal(HouseholdSystem.Wage(world, town, farmer).Pay, HouseholdSystem.Wage(world, town, carpenter).Pay, 6);
    }

    [Fact]
    public void PaydayFillsPursesAndTaxesTheTreasury()
    {
        var (world, town, _) = Scenario.NewTown();
        var worker = town.Citizens.First(c => c.CanWork && c.JobBuildingId != null);
        double before = worker.Coin;
        var (pay, _) = HouseholdSystem.Wage(world, town, worker);
        foreach (var c in town.Citizens) c.CoveredNeeds.Clear(); // nothing to buy but food
        HouseholdSystem.Daily(world, town);
        Assert.True(worker.Coin > before);
        Assert.True(EconomySystem.DailyTax(world, town) > 0);
        Assert.Equal(pay * world.Data.Households.Government(town.Rules.Government).TaxRate, HouseholdSystem.TaxOn(world, town, worker, pay), 9);
    }

    [Fact]
    public void FamiliesGoWithoutWhatTheyCantAfford()
    {
        var (world, town, _) = Scenario.NewTown();
        var home = town.Buildings.First(b => b.Def.Housing > 0 && b.ResidentIds.Count > 0);
        var family = home.ResidentIds.Select(id => town.FindCitizen(id)!).ToList();
        foreach (var c in town.Citizens) { c.Coin = 0; c.JobBuildingId = null; }
        foreach (var b in town.Buildings) b.WorkerIds.Clear();
        foreach (var c in town.Citizens) c.SickDays = 30; // nobody earns anything today
        foreach (var c in family) { c.CoveredNeeds.Add("drink"); c.CoveredNeeds.Add("faith"); }

        HouseholdSystem.Daily(world, town);
        Assert.All(family, c => Assert.DoesNotContain("drink", c.CoveredNeeds));
        Assert.All(family, c => Assert.DoesNotContain("faith", c.CoveredNeeds));
        Assert.True(town.PoorHouseholds > 0);
        Assert.True(town.ReliefHouseholds > 0); // bread is given to those who can't pay

        // The common weal gives faith (not ale) to the poor.
        Assert.True(world.Execute(new SetGovernmentCommand(town.Id, "common_weal")).Ok);
        foreach (var c in family) { c.CoveredNeeds.Add("drink"); c.CoveredNeeds.Add("faith"); }
        HouseholdSystem.Daily(world, town);
        Assert.All(family, c => Assert.DoesNotContain("drink", c.CoveredNeeds));
        Assert.All(family, c => Assert.Contains("faith", c.CoveredNeeds));

        // With savings, they pay for it all.
        foreach (var c in family) c.Coin = 10;
        foreach (var c in family) c.CoveredNeeds.Add("drink");
        int treasury = town.Stock.Get("coin");
        HouseholdSystem.Daily(world, town);
        Assert.All(family, c => Assert.Contains("drink", c.CoveredNeeds));
        Assert.True(family.Where(c => c.IsAdult).Sum(c => c.Coin) < 10 * family.Count(c => c.IsAdult));
    }

    [Fact]
    public void TheTreasuryPaysBuildersAndSoldiers()
    {
        var (world, town, _) = Scenario.NewTown();
        double baseUpkeep = EconomySystem.DailyUpkeep(world, town);
        town.Stock.Add("wood", 100);
        Scenario.Place(world, town, "chapel"); // a construction site: builders are hired
        town.Stock.Add("stone", 100);
        Sim.Core.Systems.JobSystem.Tick(world, town);
        Assert.True(HouseholdSystem.CityWages(world, town) > 0);
        Assert.True(EconomySystem.DailyUpkeep(world, town) > baseUpkeep);
    }

    [Fact]
    public void GearGoesToThoseWhoCanAffordIt()
    {
        var (world, town, _) = Scenario.NewTown();
        town.Stock.Add("wood", 100);
        town.Stock.Add("stone", 100);
        Scenario.Finish(world, town, Scenario.Place(world, town, "barracks"));
        town.Stock.Add("weapons", 20);
        town.Stock.Add("armour", 20);
        foreach (var c in town.Citizens) c.Coin = 0;
        var refused = world.Execute(new RecruitSquadCommand(town.Id, "men_at_arms", 3));
        Assert.False(refused.Ok);
        Assert.Contains("afford", refused.Error);
        Assert.True(world.Execute(new RecruitSquadCommand(town.Id, "militia", 3)).Ok); // militia need no gear

        double price = HouseholdSystem.GearPrice(world, world.Data.War.Unit("men_at_arms")!);
        var rich = town.Citizens.Where(c => c.CanWork).Take(3).ToList();
        foreach (var c in rich) c.Coin = price + 1;
        var raised = world.Execute(new RecruitSquadCommand(town.Id, "men_at_arms", 3));
        Assert.True(raised.Ok, raised.Error);
    }

    [Fact]
    public void SavingsGoToTheSpouse()
    {
        var (world, town, _) = Scenario.NewTown();
        var c = town.Citizens.First(x => x.SpouseId != null);
        var spouse = town.FindCitizen(c.SpouseId!.Value)!;
        c.Coin = 12;
        double before = spouse.Coin;
        LifeSystem.Remove(town, c);
        Assert.Equal(before + 12, spouse.Coin, 9);
    }

    [Fact]
    public void PursesAndGovernmentSurviveSaveAndLoad()
    {
        var (world, town, sim) = Scenario.NewTown();
        Assert.True(world.Execute(new SetGovernmentCommand(town.Id, "iron_rule")).Ok);
        sim.RunDays(3);
        var loaded = SaveGame.FromJson(SaveGame.ToJson(world));
        Assert.Equal(world.ComputeStateHash(), loaded.ComputeStateHash());
        Assert.Equal("iron_rule", loaded.Settlements[0].Rules.Government);
        Assert.Equal(town.Citizens.Sum(c => c.Coin), loaded.Settlements[0].Citizens.Sum(c => c.Coin), 9);
    }

    [Fact]
    public void ThousandsOfPeopleCostLittle()
    {
        var (world, town, _) = Scenario.NewTown();
        while (town.Citizens.Count < 5000)
            foreach (var c in CitizenFactory.CreateHousehold(world, 4))
                town.AddCitizen(c);
        foreach (var c in town.Citizens) c.CoveredNeeds.Add("drink");
        HouseholdSystem.Daily(world, town); // warm up
        var watch = Stopwatch.StartNew();
        for (int day = 0; day < 20; day++) HouseholdSystem.Daily(world, town);
        watch.Stop();
        // A game day is at least 20 real seconds even on the fastest worlds; this is far under a frame.
        Assert.True(watch.Elapsed.TotalMilliseconds / 20 < 25, $"{watch.Elapsed.TotalMilliseconds / 20:0.##} ms per day for 5,000 people");
    }
}
