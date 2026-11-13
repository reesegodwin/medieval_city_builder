using Sim.Core.Citizens;
using Sim.Core.Settlements;
using Sim.Core.Simulation;
using Sim.Core.Systems;

namespace Sim.Tests;

/// <summary>Herbalists, bathhouses, the town watch and crime.</summary>
public class TownCareTests
{
    private static void Grow(GameWorld world, Settlement town, int people)
    {
        while (people > 0)
            foreach (var c in CitizenFactory.CreateHousehold(world, Math.Min(4, people)))
            {
                town.AddCitizen(c);
                people--;
            }
    }

    [Fact]
    public void HerbalistsHelpTheSickRecover()
    {
        var (world, town, _) = Scenario.NewTown();
        town.Rules.Disasters = false;
        var treated = town.Citizens[0];
        var untreated = town.Citizens[1];
        treated.SickDays = untreated.SickDays = 8;
        treated.CoveredNeeds.Add("healing");
        untreated.CoveredNeeds.Remove("healing");
        DisasterSystem.Daily(world, town);
        Assert.Equal(8 - world.Data.Disasters.HealingRecovery, treated.SickDays);
        Assert.Equal(7, untreated.SickDays);
    }

    [Fact]
    public void BathhousesServeHygieneAndBurnFirewood()
    {
        var (world, town, sim) = Scenario.NewTown();
        town.Rules.Disasters = false;
        town.Stock.Add("wood", 100);
        town.Stock.Add("stone", 100);
        town.Stock.Add("firewood", 200);
        town.Stock.Add("coin", 100);
        var bath = Scenario.Place(world, town, "bathhouse");
        Scenario.Finish(world, town, bath);
        town.Rules.JobPriority["bath_keeper"] = 20;
        sim.RunDays(2);
        Assert.True(ServiceSystem.Coverage(town, "hygiene") > 0);
        Assert.True(town.Ledger.History.Sum(d => d.Get("firewood", Sim.Core.Economy.LedgerKeys.Used)) < 0 ||
                    town.Ledger.History.Any(d => d.Flows.TryGetValue("firewood", out var f) && f.Values.Any(v => v < 0)));
    }

    [Fact]
    public void PennilessUnhappyPeopleStealUnlessTheWatchIsNear()
    {
        var (world, town, _) = Scenario.NewTown();
        Grow(world, town, 200);
        foreach (var c in town.Citizens)
        {
            c.Coin = 0;
            c.CoveredNeeds.Remove("law");
        }
        var rich = town.Buildings.First(b => b.Def.Housing > 0 && b.ResidentIds.Any(id => town.FindCitizen(id)!.IsAdult));
        var richAdult = town.FindCitizen(rich.ResidentIds.First(id => town.FindCitizen(id)!.IsAdult))!;
        richAdult.Coin = 40;

        for (int day = 0; day < 120 && town.Crimes == 0; day++) CrimeSystem.Daily(world, town);
        Assert.True(town.Crimes > 0);
        Assert.True(richAdult.Coin < 40);
        Assert.True(town.CrimeDays > 0);
        Assert.Contains(town.Citizens, c => c.Coin > 0 && c != richAdult); // the loot went somewhere

        // The watch reaches everyone: no more thefts.
        int crimes = town.Crimes;
        foreach (var c in town.Citizens) c.CoveredNeeds.Add("law");
        for (int day = 0; day < 120; day++) CrimeSystem.Daily(world, town);
        Assert.Equal(crimes, town.Crimes);

        // And crime can be switched off.
        foreach (var c in town.Citizens) c.CoveredNeeds.Remove("law");
        Assert.True(world.Execute(new SetCrimeCommand(town.Id, false)).Ok);
        for (int day = 0; day < 120; day++) CrimeSystem.Daily(world, town);
        Assert.Equal(crimes, town.Crimes);
    }
}
