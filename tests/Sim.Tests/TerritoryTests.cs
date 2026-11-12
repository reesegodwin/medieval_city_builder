using Sim.Core.Systems;
using Sim.Core.Commands;
using Sim.Core.Persistence;
using Sim.Core.Settlements;
using Sim.Core.Simulation;
using Sim.Core.World;

namespace Sim.Tests;

public class TerritoryTests
{
    private static void Grow(GameWorld world, Settlement town, int people)
    {
        while (people > 0)
        {
            foreach (var c in CitizenFactory.CreateHousehold(world, Math.Min(4, people)))
            {
                town.AddCitizen(c);
                people--;
            }
        }
    }

    [Fact]
    public void NewTownHasAKeepAndAnEvenTerritoryHoldingAllItsBuildings()
    {
        var (world, town, _) = Scenario.NewTown();

        Assert.Equal("keep", town.TownCentre!.Def.Id);
        Assert.True(town.TownCentre.IsOperational);
        Assert.Equal(world.Data.Building("keep").TownCentre!.TerritoryCap, town.TerritoryCap);
        Assert.Contains((town.CenterX, town.CenterY), town.Territory);
        foreach (var b in town.Buildings)
            for (int y = b.Y; y < b.Y + b.Def.Height; y++)
            for (int x = b.X; x < b.X + b.Def.Width; x++)
                Assert.True(town.OwnsTile(world.Map, x, y));
        foreach (var r in town.Roads) Assert.True(town.OwnsTile(world.Map, r.X, r.Y));
    }

    [Fact]
    public void PaintedTerritoryCanGrowInOneDirectionOnly()
    {
        var (world, town, _) = Scenario.NewTown();
        Assert.True(world.Execute(new SetAutoExpandTerritoryCommand(town.Id, false)).Ok);
        Grow(world, town, 40);
        TerritorySystem.Daily(world, town); // auto-expansion is off: nothing claimed
        int before = town.Territory.Count;
        Assert.True(TerritoryRules.SpareAllowance(town) > 0);

        // A strip running east from the current border.
        int edge = town.Territory.Where(t => t.Y == town.CenterY).Max(t => t.X);
        var strip = Enumerable.Range(edge + 1, 30).Select(x => (x, town.CenterY)).ToList();
        Assert.True(world.Execute(new ClaimTerritoryCommand(town.Id, strip)).Ok);

        Assert.Equal(before + 30, town.Territory.Count);
        Assert.All(strip, t => Assert.True(town.OwnsTile(world.Map, t.Item1, t.Item2)));
        Assert.True(town.Territory.Where(t => !strip.Contains(t)).All(t => t.X <= edge),
            "Nothing else should have been claimed");
    }

    [Fact]
    public void ClaimsMustTouchTheTerritoryAndFitTheAllowance()
    {
        var (world, town, _) = Scenario.NewTown();
        town.Rules.AutoExpandTerritory = false;

        int edge = town.Territory.Where(t => t.Y == town.CenterY).Max(t => t.X);
        var detached = new List<(int, int)> { (edge + 5, town.CenterY) };
        Grow(world, town, 16);
        var result = world.Execute(new ClaimTerritoryCommand(town.Id, detached));
        Assert.False(result.Ok);
        Assert.False(town.OwnsTile(world.Map, edge + 5, town.CenterY));

        // A band bigger than the spare allowance is cut off at the allowance.
        int spare = TerritoryRules.SpareAllowance(town);
        var band = Enumerable.Range(town.CenterY - 10, 21)
            .SelectMany(y => Enumerable.Range(edge + 1, 60).Select(x => (x, y))).ToList();
        Assert.True(band.Count > spare);
        Assert.True(world.Execute(new ClaimTerritoryCommand(town.Id, band)).Ok);
        Assert.Equal(town.TerritoryAllowance, town.Territory.Count);
        Assert.False(world.Execute(new ClaimTerritoryCommand(town.Id, band)).Ok);
    }

    [Fact]
    public void LandHeldByAnotherSettlementCannotBeClaimed()
    {
        var (world, town, _) = Scenario.NewTown();
        town.Rules.AutoExpandTerritory = false;
        Grow(world, town, 16);
        int edge = town.Territory.Where(t => t.Y == town.CenterY).Max(t => t.X);
        world.Map.GetTile(edge + 1, town.CenterY).TerritoryId = 999;

        var result = world.Execute(new ClaimTerritoryCommand(town.Id, new[] { (edge + 1, town.CenterY) }));
        Assert.False(result.Ok);
        Assert.Equal("Claimed by another settlement", result.Error);
    }

    [Fact]
    public void BuildingsAndRoadsNeedTerritory()
    {
        var (world, town, _) = Scenario.NewTown();
        town.Rules.AutoExpandTerritory = false;
        var def = world.Data.Building("well");

        // Find a buildable tile just outside the border.
        var outside = town.Territory
            .SelectMany(t => new[] { (t.X + 1, t.Y), (t.X, t.Y + 1), (t.X - 1, t.Y), (t.X, t.Y - 1) })
            .First(t => !town.OwnsTile(world.Map, t.Item1, t.Item2) && world.Map.GetTile(t.Item1, t.Item2).IsBuildable);
        Assert.Equal("Outside the settlement's territory", Placement.Check(world, town, def, outside.Item1, outside.Item2));
        Assert.False(world.Execute(new PlaceRoadCommand(town.Id, new[] { outside })).Ok);

        Grow(world, town, 16);
        Assert.True(world.Execute(new ClaimTerritoryCommand(town.Id, new[] { outside })).Ok);
        Assert.True(world.Execute(new PlaceRoadCommand(town.Id, new[] { outside })).Ok);
    }

    [Fact]
    public void OnlyEmptyTerritoryCanBeReleased()
    {
        var (world, town, _) = Scenario.NewTown();
        town.Rules.AutoExpandTerritory = false;
        var road = town.Roads.First();
        var building = town.TownCentre!;

        Assert.False(world.Execute(new ReleaseTerritoryCommand(town.Id, new[] { road })).Ok);
        Assert.False(world.Execute(new ReleaseTerritoryCommand(town.Id, new[] { (building.X, building.Y) })).Ok);

        var empty = town.Territory.First(t =>
            world.Map.GetTile(t.X, t.Y) is { BuildingId: 0, IsRoad: false });
        int before = town.Territory.Count;
        Assert.True(world.Execute(new ReleaseTerritoryCommand(town.Id, new[] { empty })).Ok);
        Assert.Equal(before - 1, town.Territory.Count);
        Assert.False(town.OwnsTile(world.Map, empty.X, empty.Y));
    }

    [Fact]
    public void TownCentreUpgradesRaiseTheCapAndNeedPeople()
    {
        var (world, town, _) = Scenario.NewTown();
        var hall = world.Data.Building("town_hall");
        int keepCap = town.TerritoryCap;

        var early = world.Execute(new UpgradeTownCentreCommand(town.Id));
        Assert.False(early.Ok);
        Assert.Contains("Needs", early.Error);

        Grow(world, town, hall.TownCentre!.MinPopulation);
        var result = world.Execute(new UpgradeTownCentreCommand(town.Id));
        Assert.True(result.Ok, result.Error);

        var site = town.TownCentre!;
        Assert.Equal("town_hall", site.Def.Id);
        Assert.True(site.IsConstructionSite);
        Assert.Equal(keepCap, town.TerritoryCap); // not until it's built

        Scenario.Finish(world, town, site);
        Assert.Equal(hall.TownCentre.TerritoryCap, town.TerritoryCap);
        Grow(world, town, 200);
        Assert.True(town.TerritoryAllowance > keepCap);
    }

    [Fact]
    public void TownCentreCanOnlyBeUpgraded()
    {
        var (world, town, _) = Scenario.NewTown();
        Assert.False(world.Execute(new DemolishBuildingCommand(town.Id, town.TownCentre!.Id)).Ok);

        var site = SiteFinderSpot(world, town, "keep");
        Assert.False(world.Execute(new PlaceBuildingCommand(town.Id, "keep", site.X, site.Y)).Ok);
    }

    [Fact]
    public void TerritorySurvivesSaveAndLoad()
    {
        var (world, town, sim) = Scenario.NewTown(autoBuild: true);
        town.Rules.AutoExpandTerritory = false;
        Grow(world, town, 16);
        int edge = town.Territory.Where(t => t.Y == town.CenterY).Max(t => t.X);
        world.Execute(new ClaimTerritoryCommand(town.Id, Enumerable.Range(edge + 1, 10).Select(x => (x, town.CenterY)).ToList()));
        sim.RunDays(3);

        var copy = SaveGame.FromJson(SaveGame.ToJson(world));
        var copyTown = copy.Settlements[0];
        Assert.Equal(town.Territory, copyTown.Territory);
        Assert.Equal(town.TerritoryCap, copyTown.TerritoryCap);
        Assert.False(copyTown.Rules.AutoExpandTerritory);
        Assert.True(copyTown.OwnsTile(copy.Map, edge + 10, town.CenterY));
        Assert.Equal(world.ComputeStateHash(), copy.ComputeStateHash());

        sim.RunDays(5);
        new Simulator(copy).RunDays(5);
        Assert.Equal(world.ComputeStateHash(), copy.ComputeStateHash());
    }

    private static (int X, int Y) SiteFinderSpot(GameWorld world, Settlement town, string defId) =>
        Sim.Core.AI.SiteFinder.Find(world, town, world.Data.Building(defId)) ?? throw new InvalidOperationException();
}
