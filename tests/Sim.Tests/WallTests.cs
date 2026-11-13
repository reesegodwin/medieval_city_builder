using Sim.Core.Commands;
using Sim.Core.Persistence;
using Sim.Core.Settlements;
using Sim.Core.Simulation;
using Sim.Core.Systems;
using Sim.Core.World;

namespace Sim.Tests;

public class WallTests
{
    /// <summary>A straight run of free land inside the town's territory.</summary>
    private static List<(int X, int Y)> FreeLine(GameWorld world, Settlement town, int length)
    {
        for (int dy = 4; dy < 30; dy++)
        for (int dx = -20; dx < 20; dx++)
        {
            var line = Enumerable.Range(0, length).Select(i => (town.CenterX + dx + i, town.CenterY + dy)).ToList();
            if (line.All(t => Walls.Check(world, town, t.Item1, t.Item2) == null &&
                              world.Map.GetTile(t.Item1, t.Item2) is { IsRoad: false, RoadPlanned: false }))
                return line;
        }
        throw new InvalidOperationException("No free line");
    }

    /// <summary>The ring of tiles around a building (one tile out), leaving out tiles a wall can't go on.</summary>
    private static List<(int X, int Y)> RingAround(GameWorld world, Settlement town, Building b, int margin = 2)
    {
        int x0 = b.X - margin, x1 = b.X + b.Def.Width - 1 + margin, y0 = b.Y - margin, y1 = b.Y + b.Def.Height - 1 + margin;
        var ring = new List<(int, int)>();
        for (int x = x0; x <= x1; x++) { ring.Add((x, y0)); ring.Add((x, y1)); }
        for (int y = y0 + 1; y < y1; y++) { ring.Add((x0, y)); ring.Add((x1, y)); }
        return ring.Where(t => Walls.Check(world, town, t.Item1, t.Item2) == null).Distinct().ToList();
    }

    [Fact]
    public void WallsArePaidPlannedAndBuiltByTheCrew()
    {
        var (world, town, sim) = Scenario.NewTown();
        town.Rules.Disasters = false;
        town.Stock.Add("wood", 100);
        var line = FreeLine(world, town, 5);
        int wood = town.Stock.Get("wood");
        Assert.True(world.Execute(new PlaceWallCommand(town.Id, line)).Ok);
        Assert.Equal(wood - 5 * world.Data.WallTiers[0].Cost["wood"], town.Stock.Get("wood"));
        Assert.Equal(5, town.WallPlans.Count);
        Assert.False(world.Map.GetTile(line[0].X, line[0].Y).BlocksPassage); // planned walls don't block yet

        sim.RunDays(4);
        Assert.Empty(town.WallPlans);
        Assert.All(line, t => Assert.Equal(0, town.Walls[t].Tier));
        Assert.True(world.Map.GetTile(line[0].X, line[0].Y).BlocksPassage);
        Assert.Equal(world.Data.WallTiers[0].HitPoints, town.Walls[line[0]].Hp);

        // Upgrading in place to a stone wall.
        town.Stock.Add("stone", 100);
        Assert.True(world.Execute(new PlaceWallCommand(town.Id, line, 1)).Ok);
        sim.RunDays(8);
        Assert.All(line, t => Assert.Equal(1, town.Walls[t].Tier));
        Assert.Equal(world.Data.WallTiers[1].HitPoints, town.Walls[line[0]].Hp);
    }

    [Fact]
    public void WallsBlockEveryoneExceptAtGates()
    {
        var (world, town, _) = Scenario.NewTown();
        town.Stock.Add("wood", 100);
        var line = FreeLine(world, town, 3);
        Assert.True(world.Execute(new PlaceWallCommand(town.Id, line)).Ok);
        Walls.FinishAllPlans(world, town);
        foreach (var mode in new[] { Pathfinder.Mode.Walk, Pathfinder.Mode.BuildRoad, Pathfinder.Mode.RoadOnly })
            Assert.Null(Pathfinder.StepCost(world.Map.GetTile(line[1].X, line[1].Y), mode));

        Assert.True(world.Execute(new PlaceGateCommand(town.Id, line[1].X, line[1].Y)).Ok);
        Assert.Null(Pathfinder.StepCost(world.Map.GetTile(line[1].X, line[1].Y), Pathfinder.Mode.Walk)); // not open until built
        Walls.FinishAllPlans(world, town);
        Assert.NotNull(Pathfinder.StepCost(world.Map.GetTile(line[1].X, line[1].Y), Pathfinder.Mode.Walk));
        Assert.True(town.Walls[line[1]].Gate);
    }

    [Fact]
    public void AWallAcrossARoadIsAGateThatCaravansUse()
    {
        var (world, town, _) = Scenario.NewTown();
        town.Stock.Add("wood", 100);
        var road = town.Roads.First(t => world.Map.GetTile(t.X, t.Y).BuildingId == 0);
        Assert.True(world.Execute(new PlaceWallCommand(town.Id, new[] { road })).Ok);
        Walls.FinishAllPlans(world, town);
        var tile = world.Map.GetTile(road.X, road.Y);
        Assert.True(tile.IsGate && tile.WallTier > 0);
        Assert.NotNull(Pathfinder.StepCost(tile, Pathfinder.Mode.RoadOnly));
    }

    [Fact]
    public void BuildingsAndRoadsCannotGoOnWalls()
    {
        var (world, town, _) = Scenario.NewTown();
        town.Stock.Add("wood", 100);
        var line = FreeLine(world, town, 3);
        Assert.True(world.Execute(new PlaceWallCommand(town.Id, line)).Ok);
        Assert.Equal("Blocked by a wall", Placement.Check(world, town, world.Data.Building("well"), line[0].X, line[0].Y));
        Walls.FinishAllPlans(world, town);
        Assert.NotNull(Roads.Check(world, town, line[0].X, line[0].Y));
    }

    [Fact]
    public void HomesInsideAClosedRingFeelSafe()
    {
        var (world, town, _) = Scenario.NewTown();
        town.Stock.Add("wood", 1000);
        town.Stock.Add("stone", 1000);
        town.Rules.AutoExpandTerritory = true;

        // An empty 8x8 patch (plus a free tile around it), with a hovel in the middle and walls on its edge.
        const int size = 8;
        bool Free(int x, int y) => world.Map.GetTile(x, y) is { BuildingId: 0, IsRoad: false, RoadPlanned: false } t &&
                                   t.Terrain is Terrain.Grass or Terrain.Fertile && town.OwnsTile(world.Map, x, y);
        (int X, int Y) corner = default;
        bool found = false;
        foreach (var (tx, ty) in town.Territory.OrderBy(t => Math.Abs(t.X - town.CenterX) + Math.Abs(t.Y - town.CenterY)))
        {
            found = true;
            for (int y = ty - 1; y <= ty + size && found; y++)
            for (int x = tx - 1; x <= tx + size && found; x++)
                found = Free(x, y);
            if (found) { corner = (tx, ty); break; }
        }
        Assert.True(found, "an empty patch");
        var placed = world.Execute(new PlaceBuildingCommand(town.Id, "hovel", corner.X + 3, corner.Y + 3));
        Assert.True(placed.Ok, placed.Error);
        var home = town.FindBuilding(placed.CreatedId!.Value)!;
        Scenario.Finish(world, town, home);
        var citizen = town.Citizens.First();
        if (citizen.HomeBuildingId is int old) town.FindBuilding(old)!.ResidentIds.Remove(citizen.Id);
        citizen.HomeBuildingId = home.Id;
        home.ResidentIds.Add(citizen.Id);

        var ring = new List<(int X, int Y)>();
        for (int k = 0; k < size; k++)
        {
            ring.Add((corner.X + k, corner.Y));
            ring.Add((corner.X + k, corner.Y + size - 1));
            if (k > 0 && k < size - 1) { ring.Add((corner.X, corner.Y + k)); ring.Add((corner.X + size - 1, corner.Y + k)); }
        }
        Assert.Equal(0, WallSystem.SafetyMood(world, town, home));
        Assert.True(world.Execute(new PlaceWallCommand(town.Id, ring)).Ok);
        Walls.FinishAllPlans(world, town);
        Assert.Equal(world.Data.WallTiers[0].SafetyMood, WallSystem.SafetyMood(world, town, home));
        Assert.Contains(Needs.MoodFactors(world, town, citizen), f => f.Label == "Safe behind walls" && f.Mood > 0);

        Assert.True(world.Execute(new PlaceWallCommand(town.Id, ring, 1)).Ok);
        Walls.FinishAllPlans(world, town);
        Assert.Equal(world.Data.WallTiers[1].SafetyMood, WallSystem.SafetyMood(world, town, home));

        // A gate keeps it closed; a gap in the middle of a side lets the outside in.
        Assert.True(world.Execute(new PlaceGateCommand(town.Id, corner.X + 3, corner.Y)).Ok);
        Walls.FinishAllPlans(world, town);
        Assert.Equal(world.Data.WallTiers[1].SafetyMood, WallSystem.SafetyMood(world, town, home));
        Assert.True(world.Execute(new RemoveWallCommand(town.Id, new[] { (corner.X, corner.Y + 4) })).Ok);
        Assert.Equal(0, WallSystem.SafetyMood(world, town, home));
    }

    [Fact]
    public void RemovingPlannedWallsRefundsThemAndFinishedOnesGiveAQuarterBack()
    {
        var (world, town, _) = Scenario.NewTown();
        town.Stock.Add("wood", 100);
        var line = FreeLine(world, town, 4);
        int wood = town.Stock.Get("wood");
        Assert.True(world.Execute(new PlaceWallCommand(town.Id, line)).Ok);
        Assert.True(world.Execute(new RemoveWallCommand(town.Id, line)).Ok);
        Assert.Equal(wood, town.Stock.Get("wood"));

        Assert.True(world.Execute(new PlaceWallCommand(town.Id, line)).Ok);
        Walls.FinishAllPlans(world, town);
        Assert.True(world.Execute(new RemoveWallCommand(town.Id, line)).Ok);
        Assert.Equal(wood - 16 + 4, town.Stock.Get("wood"));
        Assert.False(world.Map.GetTile(line[0].X, line[0].Y).BlocksPassage);
    }

    [Fact]
    public void GrowingTownsRingThemselvesWithWalls()
    {
        var (world, town, sim) = Scenario.NewTown(42, autoBuild: true);
        sim.RunDays(320);
        Assert.NotEmpty(town.WallRing);
        Assert.True(town.Walls.Count > 50, $"{town.Walls.Count} wall tiles");
        Assert.Contains(town.Walls.Values, w => w.Gate);
        Assert.DoesNotContain(town.Buildings, b => b.NoRoute);
        Assert.True(town.Buildings.Count(b => b.Def.Housing > 0 && WallSystem.IsInside(world, town, b)) > 0);
    }

    [Fact]
    public void WallsSurviveSaveAndLoad()
    {
        var (world, town, sim) = Scenario.NewTown(42, autoBuild: true);
        sim.RunDays(260);
        Assert.NotEmpty(town.Walls);
        var copy = SaveGame.FromJson(SaveGame.ToJson(world));
        var copyTown = copy.Settlements[0];
        Assert.Equal(town.Walls.Count, copyTown.Walls.Count);
        Assert.Equal(town.WallRing.Count, copyTown.WallRing.Count);
        var (x, y) = town.Walls.Keys.First();
        Assert.Equal(world.Map.GetTile(x, y).WallTier, copy.Map.GetTile(x, y).WallTier);
        Assert.Equal(world.ComputeStateHash(), copy.ComputeStateHash());
        sim.RunDays(20);
        new Simulator(copy).RunDays(20);
        Assert.Equal(world.ComputeStateHash(), copy.ComputeStateHash());
    }

    [Fact]
    public void SoldiersCanWalkAlongTheTopOfStoneWallsButNotPalisades()
    {
        var (world, town, _) = Scenario.NewTown();
        town.Stock.Add("wood", 500);
        town.Stock.Add("stone", 500);
        var line = FreeLine(world, town, 6);
        Assert.True(world.Execute(new PlaceWallCommand(town.Id, line)).Ok);
        Walls.FinishAllPlans(world, town);
        Assert.Null(WallSystem.WalkPath(world, town, line[0], line[^1])); // a palisade has no walk on top
        Assert.Equal(0, WallSystem.DefenderCapacity(world, town));

        Assert.True(world.Execute(new PlaceWallCommand(town.Id, line, 1)).Ok);
        Walls.FinishAllPlans(world, town);
        var path = WallSystem.WalkPath(world, town, line[0], line[^1]);
        Assert.NotNull(path);
        Assert.Equal(line.Count, path!.Count);
        Assert.Equal(line.Count * world.Data.WallTiers[1].DefendersPerTile, WallSystem.DefenderCapacity(world, town));

        // A gate keeps the walk going (over the gatehouse); a gap breaks it.
        Assert.True(world.Execute(new PlaceGateCommand(town.Id, line[2].X, line[2].Y)).Ok);
        Walls.FinishAllPlans(world, town);
        Assert.True(WallSystem.IsStair(world, town, line[2].X, line[2].Y), "Gatehouses lead up to the walk");
        Assert.NotNull(WallSystem.WalkPath(world, town, line[0], line[^1]));
        Assert.True(world.Execute(new RemoveWallCommand(town.Id, new[] { line[3] })).Ok);
        Assert.Null(WallSystem.WalkPath(world, town, line[0], line[^1]));
    }
}
