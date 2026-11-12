using Sim.Core.AI;
using Sim.Core.Commands;
using Sim.Core.Events;
using Sim.Core.Settlements;
using Sim.Core.Simulation;
using Sim.Core.Systems;
using Sim.Core.World;

namespace Sim.Tests;

public class PathfinderTests
{
    [Fact]
    public void PathsGoAroundWaterAndBuildings()
    {
        var (world, town, _) = Scenario.NewTown();
        var stockpile = town.Buildings.First(b => b.Def.Id == "stockpile");
        var from = (stockpile.X - 3, stockpile.CenterY);
        var to = (stockpile.X + stockpile.Def.Width + 2, stockpile.CenterY);

        var path = Pathfinder.FindPath(world.Map, from, to);

        Assert.NotNull(path);
        Assert.Equal(from, path![0]);
        Assert.Equal(to, path[^1]);
        foreach (var (x, y) in path.Skip(1).SkipLast(1))
        {
            var tile = world.Map.GetTile(x, y);
            Assert.Equal(0, tile.BuildingId);
            Assert.NotEqual(Terrain.Water, tile.Terrain);
        }
        // Consecutive steps are neighbours.
        for (int i = 1; i < path.Count; i++)
            Assert.Equal(1, Math.Abs(path[i].X - path[i - 1].X) + Math.Abs(path[i].Y - path[i - 1].Y));
    }

    [Fact]
    public void RoadsShortenTravelAndImproveHauling()
    {
        var (world, town, _) = Scenario.NewTown();
        var farm = Scenario.Place(world, town, "farm");
        Scenario.Finish(world, town, farm);
        double before = farm.HaulFactor;

        Assert.True(RoadPlanner.Connect(world, town, farm));
        Roads.FinishAllPlans(world, town); // roadworkers would take a little while

        Assert.True(farm.HaulFactor > before, $"haul factor {farm.HaulFactor} should beat {before}");
    }
}

public class RoadTests
{
    [Fact]
    public void RoadworkersBuildPlannedRoadsOverTime()
    {
        var (world, town, sim) = Scenario.NewTown();
        var grass = FindFreeBuildable(world, town);
        Assert.True(world.Execute(new PlaceRoadCommand(town.Id, new[] { grass })).Ok);
        Assert.Single(town.RoadPlans);

        sim.RunDays(1);

        Assert.Empty(town.RoadPlans);
        Assert.True(world.Map.GetTile(grass.X, grass.Y).IsRoad);
        Assert.Contains(town.TownCentre!.Def.JobKind, new[] { "roadworker" });
    }

    [Fact]
    public void RoadsCanLeaveTheTerritoryButNotEnterAnotherTown()
    {
        var world = new GameWorld(42);
        world.Execute(new FoundSettlementCommand("a"));
        world.Execute(new FoundSettlementCommand("b"));
        var (a, b) = (world.Settlements[0], world.Settlements[1]);

        var outside = a.Territory
            .SelectMany(t => new[] { (t.X + 1, t.Y), (t.X - 1, t.Y), (t.X, t.Y + 1), (t.X, t.Y - 1) })
            .First(t => world.Map.GetTile(t.Item1, t.Item2) is { TerritoryId: 0 } tile && tile.IsBuildable);
        Assert.True(world.Execute(new PlaceRoadCommand(a.Id, new[] { outside })).Ok);

        var theirs = b.Territory.First(t => world.Map.GetTile(t.X, t.Y) is { IsBuildable: true, IsRoad: false });
        var refused = world.Execute(new PlaceRoadCommand(a.Id, new[] { theirs }));
        Assert.False(refused.Ok);
        Assert.Contains("another settlement", refused.Error);
    }

    [Fact]
    public void RoadsAreValidatedPlacedAndRemoved()
    {
        var (world, town, _) = Scenario.NewTown();

        var rock = Scenario.FindTile(world, town, Terrain.Rock);
        Assert.False(world.Execute(new PlaceRoadCommand(town.Id, new[] { rock })).Ok);

        var stockpile = town.Buildings.First(b => b.Def.Id == "stockpile");
        Assert.False(world.Execute(new PlaceRoadCommand(town.Id, new[] { (stockpile.X, stockpile.Y) })).Ok);

        var grass = FindFreeBuildable(world, town);
        int version = town.RoadVersion;
        Assert.True(world.Execute(new PlaceRoadCommand(town.Id, new[] { grass })).Ok);
        Assert.True(world.Map.GetTile(grass.X, grass.Y).RoadPlanned); // planned: roadworkers build it
        Assert.False(world.Map.GetTile(grass.X, grass.Y).IsRoad);
        Roads.FinishAllPlans(world, town);
        Assert.True(world.Map.GetTile(grass.X, grass.Y).IsRoad);
        Assert.Contains(grass, town.Roads);
        Assert.True(town.RoadVersion > version);

        Assert.True(world.Execute(new RemoveRoadCommand(town.Id, new[] { grass })).Ok);
        Assert.False(world.Map.GetTile(grass.X, grass.Y).IsRoad);
        Assert.DoesNotContain(grass, town.Roads);
    }

    [Fact]
    public void BridgesCostWoodAndCanBeWalkedOver()
    {
        var (world, town, _) = Scenario.NewTown();
        var water = Scenario.FindTile(world, town, Terrain.Water);
        Assert.Null(Pathfinder.StepCost(world.Map.GetTile(water.X, water.Y), Pathfinder.Mode.Walk));

        town.Stock.TryTake("wood", town.Stock.Get("wood"));
        var noWood = world.Execute(new PlaceRoadCommand(town.Id, new[] { water }));
        Assert.False(noWood.Ok);
        Assert.Contains("wood", noWood.Error);

        town.Stock.Add("wood", Roads.BridgeWoodPerTile);
        Assert.True(world.Execute(new PlaceRoadCommand(town.Id, new[] { water })).Ok);
        Assert.Equal(0, town.Stock.Get("wood"));
        Roads.FinishAllPlans(world, town);
        Assert.Equal(Pathfinder.RoadCost, Pathfinder.StepCost(world.Map.GetTile(water.X, water.Y), Pathfinder.Mode.Walk));
    }

    [Fact]
    public void TerritoryGrowsWithPopulationUpToTheTownCentreCap()
    {
        var (world, town, _) = Scenario.NewTown();
        Assert.Equal(TerritoryRules.PopulationAllowance(town.Citizens.Count), town.TerritoryAllowance);
        Assert.Equal(town.TerritoryAllowance, town.Territory.Count);
        int start = town.Territory.Count;

        foreach (var c in Enumerable.Range(0, 60).SelectMany(_ => CitizenFactory.CreateHousehold(world, 4)))
            town.AddCitizen(c);
        TerritorySystem.Daily(world, town);

        Assert.True(town.Territory.Count > start);
        Assert.Equal(world.Data.Building("keep").TownCentre!.TerritoryCap, town.TerritoryAllowance);
        Assert.Equal(town.TerritoryAllowance, town.Territory.Count);
    }

    [Fact]
    public void BuildingOverARoadReplacesIt()
    {
        var (world, town, _) = Scenario.NewTown();
        var def = world.Data.Building("well");
        var spot = FindFreeBuildable(world, town);
        Assert.True(world.Execute(new PlaceRoadCommand(town.Id, new[] { spot })).Ok);

        Assert.True(world.Execute(new PlaceBuildingCommand(town.Id, def.Id, spot.X, spot.Y)).Ok);

        Assert.False(world.Map.GetTile(spot.X, spot.Y).IsRoad);
        Assert.DoesNotContain(spot, town.Roads);
    }

    [Fact]
    public void FoundedTownHasConnectingRoads()
    {
        var (_, town, _) = Scenario.NewTown();
        Assert.NotEmpty(town.Roads);
    }

    internal static (int X, int Y) FindFreeBuildable(GameWorld world, Settlement town)
    {
        for (int r = 4; r < TerritoryRules.BaseRadius; r++)
        for (int dx = -r; dx <= r; dx++)
        {
            int x = town.CenterX + dx, y = town.CenterY + r;
            var tile = world.Map.GetTile(x, y);
            if (tile.IsBuildable && !tile.IsRoad) return (x, y);
        }
        throw new InvalidOperationException("No free ground");
    }
}

public class DemolitionTests
{
    [Fact]
    public void DemolishingAHomeFreesTilesResidentsAndRefundsAQuarter()
    {
        var (world, town, _) = Scenario.NewTown();
        var hovel = town.Buildings.First(b => b.Def.Id == "hovel" && b.ResidentIds.Count > 0);
        var residents = hovel.ResidentIds.ToList();
        int wood = town.Stock.Get("wood");

        Assert.True(world.Execute(new DemolishBuildingCommand(town.Id, hovel.Id)).Ok);

        Assert.Null(town.FindBuilding(hovel.Id));
        Assert.Equal(0, world.Map.GetTile(hovel.X, hovel.Y).BuildingId);
        Assert.All(residents, id => Assert.Null(town.FindCitizen(id)!.HomeBuildingId));
        Assert.Equal(wood + (int)(hovel.Def.Cost["wood"] * DemolishBuildingCommand.OperationalRefund), town.Stock.Get("wood"));
        Assert.Contains(world.Events, e => e.Kind == EventKind.BuildingDemolished);
    }

    [Fact]
    public void DemolishingAWorkplaceReleasesWorkers()
    {
        var (world, town, _) = Scenario.NewTown();
        var farm = town.Buildings.First(b => b.Def.Id == "farm" && b.WorkerIds.Count > 0);
        var workers = farm.WorkerIds.ToList();

        Assert.True(world.Execute(new DemolishBuildingCommand(town.Id, farm.Id)).Ok);

        Assert.All(workers, id => Assert.Null(town.FindCitizen(id)!.JobBuildingId));
    }

    [Fact]
    public void RefundDependsOnConstructionState()
    {
        var (world, town, _) = Scenario.NewTown();
        town.Stock.TryTake("wood", town.Stock.Get("wood"));

        var waiting = Scenario.Place(world, town, "hovel");
        Assert.True(world.Execute(new DemolishBuildingCommand(town.Id, waiting.Id)).Ok);
        Assert.Equal(0, town.Stock.Get("wood"));

        town.Stock.Add("wood", 12);
        var site = Scenario.Place(world, town, "hovel");
        ConstructionSystem.Tick(world, town); // takes the materials
        Assert.Equal(BuildingState.UnderConstruction, site.State);
        int wood = town.Stock.Get("wood");
        Assert.True(world.Execute(new DemolishBuildingCommand(town.Id, site.Id)).Ok);
        Assert.Equal(wood + 6, town.Stock.Get("wood"));
    }
}

public class ServiceTests
{
    [Fact]
    public void WellCoversNearbyHomesUpToCapacity()
    {
        var (world, town, _) = Scenario.NewTown();
        var well = town.Buildings.Single(b => b.Def.Id == "well");

        ServiceSystem.Daily(world, town);
        foreach (var c in town.Citizens)
        {
            var home = town.FindBuilding(c.HomeBuildingId!.Value)!;
            if (home.DistanceTo(well) <= well.Def.Service!.Radius) Assert.Contains("water", c.CoveredNeeds);
            else Assert.DoesNotContain("water", c.CoveredNeeds);
        }

        well.Def.Service!.Capacity = 3; // this world's data only
        ServiceSystem.Daily(world, town);
        Assert.Equal(3, town.Citizens.Count(c => c.CoveredNeeds.Contains("water")));
        Assert.Equal(3, well.ServedCount);
    }

    [Fact]
    public void TavernNeedsStaffAndAle()
    {
        var (world, town, _) = Scenario.NewTown();
        var tavern = Scenario.Place(world, town, "tavern");
        Scenario.Finish(world, town, tavern);
        town.Stock.TryTake("ale", town.Stock.Get("ale"));

        ServiceSystem.Daily(world, town);
        Assert.Equal("No staff", tavern.Problem);

        town.Rules.JobPriority["barkeep"] = 100;
        JobSystem.Tick(world, town);
        Assert.Single(tavern.WorkerIds);

        ServiceSystem.Daily(world, town);
        Assert.DoesNotContain(town.Citizens, c => c.CoveredNeeds.Contains("drink"));
        Assert.Equal("Needs ale", tavern.Problem);

        town.Stock.Add("ale", 50);
        ServiceSystem.Daily(world, town);
        int served = town.Citizens.Count(c => c.CoveredNeeds.Contains("drink"));
        Assert.True(served > 0);
        Assert.Equal(50 - (int)Math.Ceiling(served * 0.2), town.Stock.Get("ale"));
    }

    [Fact]
    public void MarketLetsPeopleVaryTheirDiet()
    {
        var (worldA, withMarket, simA) = Scenario.NewTown();
        var market = Scenario.Place(worldA, withMarket, "market");
        Scenario.Finish(worldA, withMarket, market);
        withMarket.Rules.JobPriority["trader"] = 100;

        var (worldB, noMarket, simB) = Scenario.NewTown();

        foreach (var town in new[] { withMarket, noMarket })
        {
            town.Stock.TryTake("grain", town.Stock.Get("grain"));
            town.Stock.Add("bread", 200);
            town.Stock.Add("vegetables", 200);
        }

        simA.RunDays(4);
        simB.RunDays(4);

        double varietyA = withMarket.Citizens.Average(c => Needs.RecentFoodCount(worldA, c));
        double varietyB = noMarket.Citizens.Average(c => Needs.RecentFoodCount(worldB, c));
        Assert.True(varietyA > varietyB, $"market town variety {varietyA} vs {varietyB}");
        Assert.True(withMarket.Citizens.Average(c => c.Mood) > noMarket.Citizens.Average(c => c.Mood));
    }

    [Fact]
    public void MoodFactorsExplainTheMood()
    {
        var (world, town, _) = Scenario.NewTown();
        ServiceSystem.Daily(world, town);
        var c = town.Citizens.First(c => c.CoveredNeeds.Contains("water"));

        var factors = Needs.MoodFactors(world, town, c);

        Assert.Contains(factors, f => f.Label == "Water" && f.Mood > 0);
        Assert.Equal(Needs.MoodSum(world, town, c), factors.Sum(f => f.Mood), 6);
    }
}

public class GovernorServiceTests
{
    [Fact]
    public void GovernedTownBuildsRoadsAndServices()
    {
        var (world, town, sim) = Scenario.NewTown(seed: 42, autoBuild: true);
        sim.RunDays(240);

        Assert.True(town.Roads.Count > 20);
        Assert.True(ServiceSystem.Coverage(town, "water") > 0.7, $"water {ServiceSystem.Coverage(town, "water"):P0}");
        Assert.Contains(town.Buildings, b => b.Def.Id == "chapel" && b.IsOperational);
        Assert.Contains(town.Buildings, b => b.Def.Id == "garden" && b.IsOperational);
        // No runaway service spam.
        Assert.True(town.Buildings.Count(b => b.Def.Id == "well") < town.Citizens.Count / 8);
    }
}

public class RuleCommandTests
{
    [Fact]
    public void RulesCanBeChangedByCommand()
    {
        var (world, town, _) = Scenario.NewTown();

        Assert.True(world.Execute(new SetProductionLimitCommand(town.Id, "wood", 20)).Ok);
        Assert.True(world.Execute(new SetReserveCommand(town.Id, "grain", 55)).Ok);
        Assert.True(world.Execute(new SetJobPriorityCommand(town.Id, "quarrier", 9)).Ok);

        Assert.Equal(20, town.Rules.ProductionLimits["wood"]);
        Assert.Equal(55, town.Rules.ReserveOf("grain"));
        Assert.Equal(9, town.Rules.PriorityOf("quarrier"));

        Assert.False(world.Execute(new SetProductionLimitCommand(town.Id, "gold", 5)).Ok);
        Assert.False(world.Execute(new SetProductionLimitCommand(town.Id, "wood", -1)).Ok);
    }

    [Fact]
    public void LoweringAStockLimitStopsProduction()
    {
        var (world, town, sim) = Scenario.NewTown();
        world.Execute(new SetProductionLimitCommand(town.Id, "wood", 0));
        sim.RunDays(1);
        var woodcutter = town.Buildings.First(b => b.Def.Id == "woodcutter");
        Assert.True(town.IsOnStandby(woodcutter));
        Assert.Equal("Stock limit reached", woodcutter.Problem);
    }
}
