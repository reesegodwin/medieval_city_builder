using Sim.Core.Events;
using Sim.Core.Settlements;
using Sim.Core.Simulation;
using Sim.Core.Systems;
using Sim.Core.World;

namespace Sim.Core.Commands;

public readonly record struct CommandResult(bool Ok, string? Error = null, int? CreatedId = null)
{
    public static CommandResult Success(int? createdId = null) => new(true, null, createdId);
    public static CommandResult Fail(string error) => new(false, error);
}

/// <summary>
/// Every change requested by a player or an AI goes through a command. The server validates and
/// applies them; the client never decides outcomes. Commands are also what gets logged for replays.
/// </summary>
public interface ICommand
{
    CommandResult Execute(GameWorld world);
}

public sealed record PlaceBuildingCommand(int SettlementId, string BuildingDefId, int X, int Y) : ICommand
{
    public CommandResult Execute(GameWorld world)
    {
        var settlement = world.FindSettlement(SettlementId);
        if (settlement == null) return CommandResult.Fail("Unknown settlement");
        if (!world.Data.Buildings.TryGetValue(BuildingDefId, out var def)) return CommandResult.Fail("Unknown building type");
        if (def.IsTownCentre) return CommandResult.Fail("Each settlement has one town centre; upgrade it instead");

        string? error = Placement.Check(world, settlement, def, X, Y);
        if (error != null) return CommandResult.Fail(error);

        var building = Placement.Place(world, settlement, def, X, Y);
        world.Log(EventKind.BuildingPlaced, settlement, $"{def.Name} planned at ({X},{Y})");
        return CommandResult.Success(building.Id);
    }
}

public sealed record SetJobPriorityCommand(int SettlementId, string JobKind, int Priority) : ICommand
{
    public CommandResult Execute(GameWorld world)
    {
        var settlement = world.FindSettlement(SettlementId);
        if (settlement == null) return CommandResult.Fail("Unknown settlement");
        settlement.Rules.JobPriority[JobKind] = Priority;
        return CommandResult.Success();
    }
}

public sealed record SetReserveCommand(int SettlementId, string Resource, int Amount) : ICommand
{
    public CommandResult Execute(GameWorld world)
    {
        var settlement = world.FindSettlement(SettlementId);
        if (settlement == null) return CommandResult.Fail("Unknown settlement");
        if (!world.Data.Resources.ContainsKey(Resource)) return CommandResult.Fail("Unknown resource");
        if (Amount < 0) return CommandResult.Fail("Reserve cannot be negative");
        settlement.Rules.Reserves[Resource] = Amount;
        return CommandResult.Success();
    }
}

/// <summary>Sets the flat stock limit for a resource; producers stop once stock reaches it.</summary>
public sealed record SetProductionLimitCommand(int SettlementId, string Resource, int Amount) : ICommand
{
    public CommandResult Execute(GameWorld world)
    {
        var settlement = world.FindSettlement(SettlementId);
        if (settlement == null) return CommandResult.Fail("Unknown settlement");
        if (!world.Data.Resources.ContainsKey(Resource)) return CommandResult.Fail("Unknown resource");
        if (Amount < 0) return CommandResult.Fail("Limit cannot be negative");
        settlement.Rules.ProductionLimits[Resource] = Amount;
        return CommandResult.Success();
    }
}

public sealed record SetAutoBuildCommand(int SettlementId, bool Enabled) : ICommand
{
    public CommandResult Execute(GameWorld world)
    {
        var settlement = world.FindSettlement(SettlementId);
        if (settlement == null) return CommandResult.Fail("Unknown settlement");
        settlement.Rules.AutoBuild = Enabled;
        return CommandResult.Success();
    }
}

public static class Placement
{
    /// <summary>Returns null if the building can go here, otherwise the reason it can't.</summary>
    public static string? Check(GameWorld world, Settlement settlement, Data.BuildingDef def, int x, int y)
    {
        int cx = x + def.Width / 2, cy = y + def.Height / 2;
        for (int dy = 0; dy < def.Height; dy++)
        for (int dx = 0; dx < def.Width; dx++)
        {
            var tile = world.Map.GetTile(x + dx, y + dy);
            if (tile.TerritoryId != settlement.Id) return "Outside the settlement's territory";
            if (tile.BuildingId != 0) return "Blocked by another building";
            if (!tile.IsBuildable) return $"Cannot build on {tile.Terrain}";
        }

        if (def.NearTerrain is { } req &&
            world.Map.CountTerrainInRadius(cx, cy, req.Radius, req.Terrain) < req.Min)
        {
            return $"Needs {req.Terrain} nearby";
        }

        return null;
    }

    internal static Building Place(GameWorld world, Settlement settlement, Data.BuildingDef def, int x, int y)
    {
        var building = new Building(world.NextId(), def, x, y) { State = BuildingState.AwaitingMaterials };

        int fertile = 0;
        for (int dy = 0; dy < def.Height; dy++)
        for (int dx = 0; dx < def.Width; dx++)
        {
            ref var tile = ref world.Map.GetTile(x + dx, y + dy);
            tile.BuildingId = building.Id;
            if (tile.Terrain == Terrain.Fertile) fertile++;
            if (tile.IsRoad) Roads.Remove(world, settlement, x + dx, y + dy);
        }

        building.TerrainMultiplier = 1.0 + def.FertileBonus * fertile / (def.Width * def.Height);
        settlement.AddBuilding(building);
        Logistics.UpdateHaulFactors(world, settlement);
        return building;
    }
}

/// <summary>Low-level road tile changes. Callers go through the road commands.</summary>
public static class Roads
{
    /// <summary>Wood per tile for a road over water (a bridge).</summary>
    public const int BridgeWoodPerTile = 4;

    /// <summary>Returns null if a road can go on this tile, otherwise why not.</summary>
    public static string? Check(GameWorld world, Settlement settlement, int x, int y)
    {
        var tile = world.Map.GetTile(x, y);
        if (tile.TerritoryId != settlement.Id) return "Outside the settlement's territory";
        if (tile.BuildingId != 0) return "Blocked by a building";
        if (!tile.IsBuildable && tile.Terrain != Terrain.Water) return $"Cannot build a road on {tile.Terrain}";
        return null;
    }

    /// <summary>Wood needed to build these tiles (only new bridge tiles cost anything).</summary>
    public static int BridgeCost(GameWorld world, IEnumerable<(int X, int Y)> tiles) =>
        tiles.Distinct().Count(t => world.Map.GetTile(t.X, t.Y) is { IsRoad: false, Terrain: Terrain.Water }) * BridgeWoodPerTile;

    internal static void Add(GameWorld world, Settlement settlement, int x, int y)
    {
        ref var tile = ref world.Map.GetTile(x, y);
        if (tile.IsRoad) return;
        tile.IsRoad = true;
        settlement.Roads.Add((x, y));
        settlement.RoadVersion++;
    }

    internal static void Remove(GameWorld world, Settlement settlement, int x, int y)
    {
        ref var tile = ref world.Map.GetTile(x, y);
        if (!tile.IsRoad) return;
        tile.IsRoad = false;
        settlement.Roads.Remove((x, y));
        settlement.RoadVersion++;
    }
}

/// <summary>Lays road on every listed tile. All tiles must be valid, or nothing is built.</summary>
public sealed record PlaceRoadCommand(int SettlementId, IReadOnlyList<(int X, int Y)> Tiles) : ICommand
{
    public CommandResult Execute(GameWorld world)
    {
        var settlement = world.FindSettlement(SettlementId);
        if (settlement == null) return CommandResult.Fail("Unknown settlement");
        if (Tiles.Count == 0) return CommandResult.Fail("No tiles");

        foreach (var (x, y) in Tiles)
        {
            if (world.Map.GetTile(x, y).IsRoad) continue;
            if (Roads.Check(world, settlement, x, y) is { } error) return CommandResult.Fail($"({x},{y}): {error}");
        }

        int wood = Roads.BridgeCost(world, Tiles);
        if (wood > 0 && !settlement.Stock.TryTake("wood", wood))
            return CommandResult.Fail($"Bridge needs {wood} wood");

        int before = settlement.Roads.Count;
        foreach (var (x, y) in Tiles) Roads.Add(world, settlement, x, y);
        if (settlement.Roads.Count != before) Logistics.UpdateHaulFactors(world, settlement);
        return CommandResult.Success();
    }
}

public sealed record RemoveRoadCommand(int SettlementId, IReadOnlyList<(int X, int Y)> Tiles) : ICommand
{
    public CommandResult Execute(GameWorld world)
    {
        var settlement = world.FindSettlement(SettlementId);
        if (settlement == null) return CommandResult.Fail("Unknown settlement");

        int before = settlement.Roads.Count;
        foreach (var (x, y) in Tiles)
        {
            if (settlement.Roads.Contains((x, y))) Roads.Remove(world, settlement, x, y);
        }
        if (settlement.Roads.Count == before) return CommandResult.Fail("No road there");
        Logistics.UpdateHaulFactors(world, settlement);
        return CommandResult.Success();
    }
}

/// <summary>
/// Tears a building down. Workers and residents are released, and part of the cost comes back:
/// nothing was spent yet while awaiting materials, half while under construction, a quarter once finished.
/// </summary>
public sealed record DemolishBuildingCommand(int SettlementId, int BuildingId) : ICommand
{
    public const double ConstructionRefund = 0.5;
    public const double OperationalRefund = 0.25;

    public CommandResult Execute(GameWorld world)
    {
        var settlement = world.FindSettlement(SettlementId);
        if (settlement == null) return CommandResult.Fail("Unknown settlement");
        var b = settlement.FindBuilding(BuildingId);
        if (b == null) return CommandResult.Fail("Unknown building");
        if (b.Def.IsTownCentre) return CommandResult.Fail("The town centre can't be demolished");

        Work.ReleaseWorkers(settlement, b);
        foreach (int id in b.ResidentIds)
        {
            if (settlement.FindCitizen(id) is { } resident) resident.HomeBuildingId = null;
        }
        b.ResidentIds.Clear();

        for (int y = b.Y; y < b.Y + b.Def.Height; y++)
        for (int x = b.X; x < b.X + b.Def.Width; x++)
        {
            world.Map.GetTile(x, y).BuildingId = 0;
        }
        settlement.RemoveBuilding(b);

        double refund = b.State switch
        {
            BuildingState.UnderConstruction => ConstructionRefund,
            BuildingState.Operational => OperationalRefund,
            _ => 0,
        };
        var returned = new List<string>();
        foreach (var (resource, amount) in b.Def.Cost)
        {
            int back = Math.Min((int)(amount * refund), settlement.FreeStorage);
            if (back <= 0) continue;
            settlement.Stock.Add(resource, back);
            returned.Add($"{back} {resource}");
        }

        Logistics.UpdateHaulFactors(world, settlement);
        world.Log(EventKind.BuildingDemolished, settlement,
            $"{b.Def.Name} demolished" + (returned.Count > 0 ? $" ({string.Join(", ", returned)} recovered)" : ""));
        return CommandResult.Success();
    }
}
