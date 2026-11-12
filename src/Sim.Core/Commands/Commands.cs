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

public static class Placement
{
    /// <summary>How far from the settlement centre buildings may be placed.</summary>
    public const int SettlementRadius = 48;

    /// <summary>Returns null if the building can go here, otherwise the reason it can't.</summary>
    public static string? Check(GameWorld world, Settlement settlement, Data.BuildingDef def, int x, int y)
    {
        int cx = x + def.Width / 2, cy = y + def.Height / 2;
        if (Math.Abs(cx - settlement.CenterX) + Math.Abs(cy - settlement.CenterY) > SettlementRadius)
            return "Too far from the settlement";

        for (int dy = 0; dy < def.Height; dy++)
        for (int dx = 0; dx < def.Width; dx++)
        {
            var tile = world.Map.GetTile(x + dx, y + dy);
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
        }

        building.TerrainMultiplier = 1.0 + def.FertileBonus * fertile / (def.Width * def.Height);
        settlement.AddBuilding(building);
        Logistics.UpdateHaulFactors(settlement);
        return building;
    }
}
