using Sim.Core.Commands;
using Sim.Core.Persistence;
using Sim.Core.Settlements;
using Sim.Core.Simulation;
using Sim.Core.Systems;
using Sim.Core.World;

namespace Sim.Tests;

public class RoadTierTests
{
    [Fact]
    public void BetterRoadsCostStoneAndWalkFaster()
    {
        var (world, town, sim) = Scenario.NewTown();
        var road = town.Roads.First(r => world.Map.GetTile(r.X, r.Y).Terrain != Terrain.Water);
        double dirt = Pathfinder.StepCost(world.Map.GetTile(road.X, road.Y), Pathfinder.Mode.Walk)!.Value;
        town.Stock.Add("stone", 10);
        int stone = town.Stock.Get("stone");

        Assert.True(world.Execute(new PlaceRoadCommand(town.Id, new[] { road }, Tier: 2)).Ok);
        Assert.Equal(stone - world.Data.RoadTiers[2].Cost["stone"], town.Stock.Get("stone")); // paid up front
        Assert.Equal(0, world.Map.GetTile(road.X, road.Y).RoadTier);                         // not built yet

        sim.RunDays(1);
        Assert.Equal(2, world.Map.GetTile(road.X, road.Y).RoadTier);
        Assert.True(Pathfinder.StepCost(world.Map.GetTile(road.X, road.Y), Pathfinder.Mode.Walk) < dirt);
        Assert.True(Pathfinder.StepCost(world.Map.GetTile(road.X, road.Y), Pathfinder.Mode.RoadOnly) < 1);
    }

    [Fact]
    public void RemovingARoadDropsItsPendingUpgradeAndRefundsIt()
    {
        var (world, town, _) = Scenario.NewTown();
        var road = town.Roads.First(r => world.Map.GetTile(r.X, r.Y).Terrain != Terrain.Water);
        town.Stock.Add("stone", 10);
        int stone = town.Stock.Get("stone");
        world.Execute(new PlaceRoadCommand(town.Id, new[] { road }, Tier: 1));
        Assert.Equal(stone - world.Data.RoadTiers[1].Cost["stone"], town.Stock.Get("stone"));

        Assert.True(world.Execute(new RemoveRoadCommand(town.Id, new[] { road })).Ok);
        Assert.Equal(stone, town.Stock.Get("stone")); // the pending resurfacing is refunded
        Assert.False(world.Map.GetTile(road.X, road.Y).RoadPlanned);
        Assert.False(world.Map.GetTile(road.X, road.Y).IsRoad);
    }

    [Fact]
    public void RoadTiersSurviveSaveAndLoad()
    {
        var (world, town, sim) = Scenario.NewTown();
        var road = town.Roads.First(r => world.Map.GetTile(r.X, r.Y).Terrain != Terrain.Water);
        town.Stock.Add("stone", 10);
        world.Execute(new PlaceRoadCommand(town.Id, new[] { road }, Tier: 1));
        sim.RunDays(1);
        var copy = SaveGame.FromJson(SaveGame.ToJson(world));
        Assert.Equal(1, copy.Map.GetTile(road.X, road.Y).RoadTier);
        Assert.Equal(world.ComputeStateHash(), copy.ComputeStateHash());
    }
}

public class HousingTests
{
    /// <summary>A finished hovel full of adults, all with the given needs covered.</summary>
    private static Building Hovel(GameWorld world, Settlement town)
    {
        var hovel = town.Buildings.First(b => b.Def.Id == "hovel");
        return hovel;
    }

    private static void Cover(Settlement town, Building home, IEnumerable<string> needs)
    {
        foreach (int id in home.ResidentIds)
            foreach (string need in needs)
                town.FindCitizen(id)!.CoveredNeeds.Add(need);
    }

    [Fact]
    public void HomesUpgradeWhenNeedsAreMetAndPayTheCost()
    {
        var (world, town, _) = Scenario.NewTown();
        var home = Hovel(world, town);
        Assert.NotEmpty(home.ResidentIds);
        var cottage = world.Data.Building("cottage");
        town.Stock.Add("wood", 200);
        town.Stock.Add("stone", 100);

        for (int day = 0; day < HomeSystem.UpgradeAfterDays; day++)
        {
            Cover(town, home, cottage.Home!.Requires);
            Assert.Equal("hovel", home.Def.Id);
            HomeSystem.Daily(world, town);
        }

        Assert.Equal("cottage", home.Def.Id);
        Assert.Equal(6, home.Def.Housing);
        Assert.All(home.ResidentIds, id => Assert.Equal("craftsman", HomeSystem.ClassOf(world, town, town.FindCitizen(id)!).Id));
    }

    [Fact]
    public void HomesSlipBackWhenNeedsGoUnmet()
    {
        var (world, town, _) = Scenario.NewTown();
        var home = Hovel(world, town);
        town.Stock.Add("wood", 200);
        town.Stock.Add("stone", 100);
        for (int day = 0; day < HomeSystem.UpgradeAfterDays; day++)
        {
            Cover(town, home, world.Data.Building("cottage").Home!.Requires);
            HomeSystem.Daily(world, town);
        }
        Assert.Equal("cottage", home.Def.Id);

        foreach (int id in home.ResidentIds) town.FindCitizen(id)!.CoveredNeeds.Clear();
        for (int day = 0; day < HomeSystem.DowngradeAfterDays; day++) HomeSystem.Daily(world, town);
        Assert.Equal("hovel", home.Def.Id);
    }

    [Fact]
    public void UpgradedHomesCantBePlacedDirectly()
    {
        var (world, town, _) = Scenario.NewTown();
        var spot = Sim.Core.AI.SiteFinder.Find(world, town, world.Data.Building("hovel"))!.Value;
        Assert.False(world.Execute(new PlaceBuildingCommand(town.Id, "townhouse", spot.X, spot.Y)).Ok);
    }

    [Fact]
    public void AutoUpgradesCanBeTurnedOff()
    {
        var (world, town, _) = Scenario.NewTown();
        Assert.True(world.Execute(new SetAutoUpgradeHomesCommand(town.Id, false)).Ok);
        var home = Hovel(world, town);
        town.Stock.Add("wood", 200);
        town.Stock.Add("stone", 100);
        for (int day = 0; day < HomeSystem.UpgradeAfterDays * 2; day++)
        {
            Cover(town, home, world.Data.Building("cottage").Home!.Requires);
            HomeSystem.Daily(world, town);
        }
        Assert.Equal("hovel", home.Def.Id);
        Assert.False(SaveGame.FromJson(SaveGame.ToJson(world)).Settlements[0].Rules.AutoUpgradeHomes);
    }

    [Fact]
    public void GovernedTownsClimbTheHousingLadder()
    {
        var (world, town, sim) = Scenario.NewTown(42, autoBuild: true);
        sim.RunDays(300);
        Assert.Contains(town.Buildings, b => b.Def.Id is "cottage" or "townhouse");
    }
}
