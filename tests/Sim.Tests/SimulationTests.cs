using Sim.Core.AI;
using Sim.Core.Commands;
using Sim.Core.Settlements;
using Sim.Core.Simulation;
using Sim.Core.Systems;
using Sim.Core.Time;
using Sim.Core.World;

namespace Sim.Tests;

/// <summary>Helpers for building small, controlled scenarios.</summary>
internal static class Scenario
{
    public static (GameWorld World, Settlement Town, Simulator Sim) NewTown(ulong seed = 42, bool autoBuild = false)
    {
        var world = new GameWorld(seed);
        var town = SettlementFounder.Found(world);
        town.Rules.AutoBuild = autoBuild;
        return (world, town, new Simulator(world));
    }

    public static Building Place(GameWorld world, Settlement town, string defId)
    {
        var def = world.Data.Building(defId);
        var site = SiteFinder.Find(world, town, def) ?? throw new InvalidOperationException($"No site for {defId}");
        var result = world.Execute(new PlaceBuildingCommand(town.Id, defId, site.X, site.Y));
        Assert.True(result.Ok, result.Error);
        return town.FindBuilding(result.CreatedId!.Value)!;
    }

    public static void Finish(GameWorld world, Settlement town, Building b)
    {
        b.State = BuildingState.UnderConstruction;
        ConstructionSystem.Complete(world, town, b);
    }

    public static (int X, int Y) FindTile(GameWorld world, Settlement town, Terrain terrain)
    {
        for (int r = 0; r < Placement.SettlementRadius; r++)
        for (int dy = -r; dy <= r; dy++)
        for (int dx = -r; dx <= r; dx++)
        {
            int x = town.CenterX + dx, y = town.CenterY + dy;
            if (world.Map.GetTile(x, y).Terrain == terrain && world.Map.GetTile(x, y).BuildingId == 0) return (x, y);
        }
        throw new InvalidOperationException($"No {terrain} near town");
    }
}

public class FoundingTests
{
    [Fact]
    public void NewSettlementHasSettlersHomesAndJobs()
    {
        var (_, town, _) = Scenario.NewTown();
        Assert.Equal(SettlementFounder.StartingPopulation, town.Citizens.Count);
        Assert.All(town.Citizens, c => Assert.NotNull(c.HomeBuildingId));
        Assert.Contains(town.Buildings, b => b.Def.Id == "stockpile" && b.IsOperational);
        Assert.Contains(town.Citizens, c => c.JobBuildingId != null);
    }
}

public class PlacementTests
{
    [Fact]
    public void CannotBuildOnWaterOrOnTopOfAnotherBuilding()
    {
        var (world, town, _) = Scenario.NewTown();

        var (wx, wy) = Scenario.FindTile(world, town, Terrain.Water);
        var water = world.Execute(new PlaceBuildingCommand(town.Id, "hovel", wx, wy));
        Assert.False(water.Ok);

        var stockpile = town.Buildings.First(b => b.Def.Id == "stockpile");
        var overlap = world.Execute(new PlaceBuildingCommand(town.Id, "hovel", stockpile.X, stockpile.Y));
        Assert.False(overlap.Ok);
        Assert.Equal("Blocked by another building", overlap.Error);
    }

    [Fact]
    public void WoodcutterNeedsForestNearby()
    {
        var (world, town, _) = Scenario.NewTown();
        var def = world.Data.Building("woodcutter");

        // Find buildable ground far from any forest.
        for (int y = town.CenterY - 20; y < town.CenterY + 20; y++)
        for (int x = town.CenterX - 20; x < town.CenterX + 20; x++)
        {
            if (world.Map.CountTerrainInRadius(x + 1, y + 1, def.NearTerrain!.Radius, Terrain.Forest) > 0) continue;
            if (!world.Map.GetTile(x, y).IsBuildable || !world.Map.GetTile(x + 1, y + 1).IsBuildable ||
                !world.Map.GetTile(x + 1, y).IsBuildable || !world.Map.GetTile(x, y + 1).IsBuildable) continue;

            Assert.Equal("Needs Forest nearby", Placement.Check(world, town, def, x, y));
            return;
        }
        Assert.Fail("Test map had no forest-free ground");
    }
}

public class ConstructionTests
{
    [Fact]
    public void BuildingWaitsForMaterialsThenGetsBuilt()
    {
        var (world, town, sim) = Scenario.NewTown();
        town.Stock.TryTake("wood", town.Stock.Get("wood"));

        var hovel = Scenario.Place(world, town, "hovel");
        sim.RunDays(1);
        Assert.Equal(BuildingState.AwaitingMaterials, hovel.State);

        town.Stock.Add("wood", 12);
        sim.RunDays(1);
        Assert.Equal(BuildingState.Operational, hovel.State);
        Assert.Equal(hovel.Def.BuildDays, hovel.ConstructionProgress);
    }
}

public class ProductionTests
{
    [Fact]
    public void FarmsGrowGrainAndBakeriesTurnItIntoBread()
    {
        var (world, town, sim) = Scenario.NewTown();
        var bakery = Scenario.Place(world, town, "bakery");
        Scenario.Finish(world, town, bakery);
        town.Stock.Add("grain", 200);

        sim.RunDays(5);

        Assert.True(town.Stock.Get("bread") > 0 || town.Citizens.Any(c => c.LastMealMood > 0),
            "Bakery should have produced bread that was stored or eaten");
    }

    [Fact]
    public void BakeryRespectsGrainReserve()
    {
        var (world, town, sim) = Scenario.NewTown();
        foreach (var farm in town.Buildings.Where(b => b.Def.Id == "farm")) farm.State = BuildingState.AwaitingMaterials;
        var bakery = Scenario.Place(world, town, "bakery");
        Scenario.Finish(world, town, bakery);
        town.Stock.TryTake("grain", town.Stock.Get("grain"));
        town.Stock.Add("grain", town.Rules.ReserveOf("grain") + 2);
        town.Stock.Add("bread", 250); // keep everyone fed with bread so nobody eats the grain
        town.Stock.TryTake("wood", town.Stock.Get("wood")); // so the disabled farms can't be rebuilt

        sim.RunDays(1);

        Assert.False(bakery.InputsLoaded);
        Assert.Equal("Needs grain", bakery.Problem);
    }

    [Fact]
    public void ProductionStopsAtStockLimitAndFreesWorkers()
    {
        var (world, town, sim) = Scenario.NewTown();
        town.Stock.Add("wood", town.LimitOf("wood"));

        sim.RunDays(1);

        var woodcutter = town.Buildings.First(b => b.Def.Id == "woodcutter");
        Assert.True(town.IsAtProductionLimit(woodcutter));
        Assert.Empty(woodcutter.WorkerIds);
    }

    [Fact]
    public void WorkersImproveTheirSkill()
    {
        var (_, town, sim) = Scenario.NewTown();
        var farmer = town.Citizens.First(c =>
            c.JobBuildingId is int j && town.FindBuilding(j)!.Def.Id == "farm");
        double before = farmer.Skill("farmer");

        sim.RunDays(3);

        Assert.True(farmer.Skill("farmer") > before);
    }
}

public class NeedsTests
{
    [Fact]
    public void CitizensStarveWithoutFood()
    {
        var (world, town, sim) = Scenario.NewTown();
        foreach (var b in town.Buildings.Where(b => b.Def.Id == "farm")) b.State = BuildingState.AwaitingMaterials;
        town.Stock.TryTake("grain", town.Stock.Get("grain"));
        town.Stock.TryTake("bread", town.Stock.Get("bread"));
        town.Stock.TryTake("wood", town.Stock.Get("wood")); // keep the farms from being rebuilt

        sim.RunDays(12);

        Assert.True(world.TotalDeaths > 0);
        Assert.Contains(world.Events, e => e.Kind == Sim.Core.Events.EventKind.Starvation);
    }

    [Fact]
    public void BreadMakesPeopleHappierThanRawGrain()
    {
        var (_, breadTown, breadSim) = Scenario.NewTown();
        breadTown.Stock.TryTake("grain", breadTown.Stock.Get("grain"));
        breadTown.Stock.Add("bread", 400);

        var (_, grainTown, grainSim) = Scenario.NewTown();
        grainTown.Stock.TryTake("bread", grainTown.Stock.Get("bread"));
        grainTown.Stock.Add("grain", 400);

        breadSim.RunDays(3);
        grainSim.RunDays(3);

        Assert.True(breadTown.Citizens.Average(c => c.Mood) > grainTown.Citizens.Average(c => c.Mood) + 5);
    }
}

public class WholeSimulationTests
{
    [Fact]
    public void SameSeedProducesIdenticalWorlds()
    {
        var (a, _, simA) = Scenario.NewTown(seed: 11, autoBuild: true);
        var (b, _, simB) = Scenario.NewTown(seed: 11, autoBuild: true);
        simA.RunDays(30);
        simB.RunDays(30);
        Assert.Equal(a.ComputeStateHash(), b.ComputeStateHash());
    }

    [Fact]
    public void DifferentSeedsDiverge()
    {
        var (a, _, simA) = Scenario.NewTown(seed: 11, autoBuild: true);
        var (b, _, simB) = Scenario.NewTown(seed: 12, autoBuild: true);
        simA.RunDays(10);
        simB.RunDays(10);
        Assert.NotEqual(a.ComputeStateHash(), b.ComputeStateHash());
    }

    [Theory]
    [InlineData(42UL)]
    [InlineData(7UL)]
    [InlineData(1234UL)]
    public void GovernedTownGrowsWithoutStarvingOverAYear(ulong seed)
    {
        var (world, town, sim) = Scenario.NewTown(seed, autoBuild: true);
        sim.RunDays(TimeScale.DaysPerYear);

        Assert.True(town.Citizens.Count > SettlementFounder.StartingPopulation * 2,
            $"Expected growth, got {town.Citizens.Count}");
        Assert.DoesNotContain(world.Events, e => e.Kind == Sim.Core.Events.EventKind.Starvation);
        Assert.Contains(town.Buildings, b => b.Def.Id == "bakery" && b.IsOperational);
        Assert.Contains(town.Buildings, b => b.Def.Id == "quarry" && b.IsOperational);

        // Food production is sized to need (bread and gardens counted), not built to twice what's eaten.
        double need = town.Citizens.Sum(Needs.DailyFoodNeed);
        double supply = SettlementGovernor.EstimatedFoodSupply(world, town);
        Assert.True(supply >= need, $"Food supply {supply:0} below need {need:0}");
        Assert.True(supply <= need * SettlementGovernor.FoodSupplyMargin * 1.15,
            $"Food overbuilt: supply {supply:0} for need {need:0}");
        Assert.True(Needs.FoodDays(world, town) > 3, $"Only {Needs.FoodDays(world, town):0.0} days of food");
    }

    [Fact]
    public void GovernedTownKeepsBuildingPastUnconnectableLand()
    {
        // On seed 42 a pocket of land boxed in by forest and a wide river sits closest to the centre. Site
        // searches used to spend every route check on it (and on sites whose road would leave the territory),
        // so the town stopped building homes around day 400.
        var (world, town, sim) = Scenario.NewTown(42, autoBuild: true);
        sim.RunDays(420);
        int housing = town.HousingCapacity;
        sim.RunDays(40);

        Assert.True(town.HousingCapacity > housing, $"Housing stuck at {housing}");
        Assert.True(town.Citizens.Count(c => c.HomeBuildingId == null) < 15, "Too many homeless");
        Assert.DoesNotContain(world.Events, e => e.Kind == Sim.Core.Events.EventKind.BuildingDemolished);
    }
}

public class CommandTests
{
    [Fact]
    public void AutoBuildCanBeToggledByCommand()
    {
        var (world, town, sim) = Scenario.NewTown(autoBuild: true);
        Assert.True(world.Execute(new SetAutoBuildCommand(town.Id, false)).Ok);
        Assert.False(town.Rules.AutoBuild);

        int buildings = town.Buildings.Count;
        sim.RunDays(10);
        Assert.Equal(buildings, town.Buildings.Count);

        Assert.False(world.Execute(new SetAutoBuildCommand(-1, true)).Ok);
    }

    [Fact]
    public void NearestStockpileIsFound()
    {
        var (_, town, _) = Scenario.NewTown();
        var farm = town.Buildings.First(b => b.Def.Id == "farm");
        var nearest = Logistics.NearestStockpile(town, farm);
        Assert.NotNull(nearest);
        Assert.True(nearest!.Def.IsStockpile);
    }
}
