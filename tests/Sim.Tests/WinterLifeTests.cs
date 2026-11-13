using Sim.Core.Economy;
using Sim.Core.Settlements;
using Sim.Core.Simulation;
using Sim.Core.Systems;
using Sim.Core.Time;

namespace Sim.Tests;

public class WinterLifeTests
{
    private static void RunToSeason(GameWorld world, Simulator sim, Season season)
    {
        while (world.Time.Season != season || !world.Time.IsStartOfDay) sim.Step();
        sim.RunDays(2);
    }

    [Fact]
    public void ShortWinterDaysSlowOutdoorWorkOnly()
    {
        var (world, town, sim) = Scenario.NewTown();
        town.Rules.Disasters = false;
        var woodcutter = town.Buildings.First(b => b.Def.Id == "woodcutter");
        var bakery = world.Data.Building("bakery");
        Assert.True(woodcutter.Def.Outdoor);
        Assert.False(bakery.Outdoor);

        RunToSeason(world, sim, Season.Summer);
        double summer = EconomySystem.OutputFactor(world, town, woodcutter);
        RunToSeason(world, sim, Season.Winter);
        double winter = EconomySystem.OutputFactor(world, town, woodcutter);
        Assert.True(winter < summer);
        Assert.Equal(world.Data.Seasons.Of(Season.Winter).Daylight, winter, 6);
    }

    [Fact]
    public void WinterEveningsAtTheTavernAndChapelLiftTheMood()
    {
        var (world, town, sim) = Scenario.NewTown();
        town.Rules.Disasters = false;
        RunToSeason(world, sim, Season.Winter);
        var adults = town.Citizens.Where(c => c.IsAdult).ToList();
        var withTavern = adults[0];
        var lonely = adults[1];
        withTavern.CoveredNeeds.Add("drink");
        lonely.CoveredNeeds.Remove("drink");
        lonely.CoveredNeeds.Remove("faith");

        Assert.Contains(Needs.MoodFactors(world, town, withTavern), f => f.Label == "Tavern on winter evenings" && f.Mood > 0);
        Assert.Contains(Needs.MoodFactors(world, town, lonely), f => f.Label == "Long winter nights" && f.Mood < 0);
        Assert.Equal(Needs.MoodSum(world, town, lonely), Needs.MoodFactors(world, town, lonely).Sum(f => f.Mood), 6);
    }

    [Fact]
    public void TavernsPourMoreAleInWinter()
    {
        double AlePerPerson(Season season)
        {
            var (world, town, sim) = Scenario.NewTown();
            town.Rules.Disasters = false;
            var tavern = Scenario.Place(world, town, "tavern");
            Scenario.Finish(world, town, tavern);
            town.Rules.JobPriority["barkeep"] = 20;
            RunToSeason(world, sim, season);
            town.Stock.Add("ale", 500);
            sim.RunDays(1);
            int served = town.Citizens.Count(c => c.CoveredNeeds.Contains("drink"));
            Assert.True(served > 0);
            return -town.Ledger.Today.Get("ale", LedgerKeys.Used) / served; // today's service ran with the ale in stock
        }
        Assert.True(AlePerPerson(Season.Winter) > AlePerPerson(Season.Summer) * 1.3);
    }
}
