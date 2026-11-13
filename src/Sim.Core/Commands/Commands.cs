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

/// <summary>A command acting on one settlement; servers only accept it from that settlement's owner.</summary>
public interface ISettlementCommand : ICommand
{
    int SettlementId { get; }
}

public sealed record PlaceBuildingCommand(int SettlementId, string BuildingDefId, int X, int Y) : ISettlementCommand
{
    public CommandResult Execute(GameWorld world)
    {
        var settlement = world.FindSettlement(SettlementId);
        if (settlement == null) return CommandResult.Fail("Unknown settlement");
        if (!world.Data.Buildings.TryGetValue(BuildingDefId, out var def)) return CommandResult.Fail("Unknown building type");
        if (def.IsTownCentre) return CommandResult.Fail("Each settlement has one town centre; upgrade it instead");
        if (def.Home is { Level: > 0 }) return CommandResult.Fail($"Build hovels; they become {def.Name}s when their people's needs are met");

        string? error = Placement.Check(world, settlement, def, X, Y);
        if (error != null) return CommandResult.Fail(error);

        var building = Placement.Place(world, settlement, def, X, Y);
        world.Log(EventKind.BuildingPlaced, settlement, $"{def.Name} planned at ({X},{Y})");
        return CommandResult.Success(building.Id);
    }
}

public sealed record SetJobPriorityCommand(int SettlementId, string JobKind, int Priority) : ISettlementCommand
{
    public CommandResult Execute(GameWorld world)
    {
        var settlement = world.FindSettlement(SettlementId);
        if (settlement == null) return CommandResult.Fail("Unknown settlement");
        settlement.Rules.JobPriority[JobKind] = Priority;
        return CommandResult.Success();
    }
}

public sealed record SetReserveCommand(int SettlementId, string Resource, int Amount) : ISettlementCommand
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
public sealed record SetProductionLimitCommand(int SettlementId, string Resource, int Amount) : ISettlementCommand
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

public sealed record SetAutoBuildCommand(int SettlementId, bool Enabled) : ISettlementCommand
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
            if (tile.RoadPlanned) Roads.Unplan(world, settlement, x + dx, y + dy);
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

    /// <summary>Roadworker-days to lay one tile of dirt road next to the town centre.</summary>
    public const double RoadWorkDays = 0.05;

    /// <summary>Roadworker-days to build one tile of bridge.</summary>
    public const double BridgeWorkDays = 0.5;

    /// <summary>Distance from the town centre at which roadworkers lose half their time walking to the site.</summary>
    public const double WalkHalfDistance = 60;

    /// <summary>Roads can reach this far from the town centre (in either axis), well past the territory.</summary>
    public const int MaxRoadDistance = 600;

    /// <summary>
    /// Returns null if a road can go on this tile, otherwise why not. Dirt roads are free and may go on unclaimed
    /// land outside the territory, but not into another settlement's territory.
    /// </summary>
    public static string? Check(GameWorld world, Settlement settlement, int x, int y)
    {
        var tile = world.Map.GetTile(x, y);
        if (tile.TerritoryId != settlement.Id && tile.TerritoryId != 0) return "Inside another settlement's territory";
        if (Math.Max(Math.Abs(x - settlement.CenterX), Math.Abs(y - settlement.CenterY)) > MaxRoadDistance) return "Too far from the town";
        if (tile.BuildingId != 0) return "Blocked by a building";
        if (!tile.IsBuildable && tile.Terrain != Terrain.Water) return $"Cannot build a road on {tile.Terrain}";
        return null;
    }

    /// <summary>Wood needed to build these tiles (only new bridge tiles cost anything).</summary>
    public static int BridgeCost(GameWorld world, IEnumerable<(int X, int Y)> tiles) =>
        tiles.Distinct().Count(t => world.Map.GetTile(t.X, t.Y) is { IsRoad: false, RoadPlanned: false, Terrain: Terrain.Water }) * BridgeWoodPerTile;

    public static double WorkDays(GameWorld world, in Tile tile, byte tier) =>
        tile.Terrain == Terrain.Water ? BridgeWorkDays : world.Data.RoadTiers[Math.Min(tier, world.Data.RoadTiers.Count - 1)].WorkDays;

    /// <summary>The tier a tile would get: bridges are always plain, land roads as requested.</summary>
    public static byte TierFor(in Tile tile, byte tier) => tile.Terrain == Terrain.Water ? (byte)0 : tier;

    /// <summary>Whether planning this tier here would do anything (a new road, or a better surface on our own road).</summary>
    public static bool NeedsWork(GameWorld world, Settlement settlement, int x, int y, byte tier)
    {
        var tile = world.Map.GetTile(x, y);
        if (tile.RoadPlanned) return false;
        if (!tile.IsRoad) return true;
        return settlement.Roads.Contains((x, y)) && TierFor(tile, tier) > tile.RoadTier;
    }

    /// <summary>Materials to plan these tiles at a tier: surface costs plus wood for new bridges.</summary>
    public static Dictionary<string, int> PlanCost(GameWorld world, Settlement settlement, IEnumerable<(int X, int Y)> tiles, byte tier)
    {
        var cost = new Dictionary<string, int>();
        foreach (var (x, y) in tiles.Distinct())
        {
            if (!NeedsWork(world, settlement, x, y, tier)) continue;
            var tile = world.Map.GetTile(x, y);
            if (tile.Terrain == Terrain.Water)
            {
                cost["wood"] = cost.GetValueOrDefault("wood") + BridgeWoodPerTile;
                continue;
            }
            foreach (var (r, n) in world.Data.RoadTiers[Math.Min(tier, world.Data.RoadTiers.Count - 1)].Cost)
                cost[r] = cost.GetValueOrDefault(r) + n;
        }
        return cost;
    }

    /// <summary>Marks a tile for roadworkers to build (or resurface).</summary>
    internal static void Plan(GameWorld world, Settlement settlement, int x, int y, byte tier = 0)
    {
        if (!NeedsWork(world, settlement, x, y, tier)) return;
        ref var tile = ref world.Map.GetTile(x, y);
        tile.RoadPlanned = true;
        settlement.RoadPlans.Add(new RoadPlan { X = x, Y = y, Tier = TierFor(tile, tier) });
        settlement.RoadVersion++;
    }

    /// <summary>Drops a planned tile (a building was placed on it, or the plan was cancelled).</summary>
    internal static bool Unplan(GameWorld world, Settlement settlement, int x, int y)
    {
        ref var tile = ref world.Map.GetTile(x, y);
        int index = settlement.RoadPlans.FindIndex(p => p.X == x && p.Y == y);
        if (index < 0) return false;
        var plan = settlement.RoadPlans[index];
        tile.RoadPlanned = false;
        settlement.RoadPlans.RemoveAt(index);
        settlement.RoadVersion++;
        // Materials paid for the surface come back; bridge wood was already used for the pilings.
        if (tile.Terrain != Terrain.Water)
            foreach (var (r, n) in world.Data.RoadTiers[Math.Min(plan.Tier, world.Data.RoadTiers.Count - 1)].Cost)
            {
                settlement.Stock.Add(r, n);
                settlement.Ledger.Record(r, n, Economy.LedgerKeys.Refunds);
            }
        return true;
    }

    /// <summary>Turns a plan into a finished road.</summary>
    internal static void Finish(GameWorld world, Settlement settlement, RoadPlan plan)
    {
        ref var tile = ref world.Map.GetTile(plan.X, plan.Y);
        tile.RoadPlanned = false;
        settlement.RoadPlans.Remove(plan);
        Add(world, settlement, plan.X, plan.Y);
        tile.RoadTier = plan.Tier;
        settlement.RoadVersion++;
    }

    /// <summary>Builds every planned tile at once (founding a town, or a town with nobody to build roads).</summary>
    public static void FinishAllPlans(GameWorld world, Settlement settlement)
    {
        if (settlement.RoadPlans.Count == 0) return;
        foreach (var plan in settlement.RoadPlans.ToList()) Finish(world, settlement, plan);
        Logistics.UpdateHaulFactors(world, settlement);
    }

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
        tile.RoadTier = 0;
        settlement.Roads.Remove((x, y));
        settlement.RoadVersion++;
    }
}

/// <summary>Lays road on every listed tile. All tiles must be valid, or nothing is built.</summary>
/// <summary>
/// Plans a road (tier 0, dirt: free) or a better surface (gravel, stone: stone per tile) along the tiles. Existing
/// roads of ours are resurfaced; roadworkers do the work over time. Bridges need wood up front.
/// </summary>
public sealed record PlaceRoadCommand(int SettlementId, IReadOnlyList<(int X, int Y)> Tiles, byte Tier = 0) : ISettlementCommand
{
    public CommandResult Execute(GameWorld world)
    {
        var settlement = world.FindSettlement(SettlementId);
        if (settlement == null) return CommandResult.Fail("Unknown settlement");
        if (Tiles.Count == 0) return CommandResult.Fail("No tiles");

        if (Tier >= world.Data.RoadTiers.Count) return CommandResult.Fail("Unknown road surface");
        foreach (var (x, y) in Tiles)
        {
            var tile = world.Map.GetTile(x, y);
            if (tile.IsRoad || tile.RoadPlanned) continue;
            if (Roads.Check(world, settlement, x, y) is { } error) return CommandResult.Fail($"({x},{y}): {error}");
        }
        if (!Tiles.Any(t => Roads.NeedsWork(world, settlement, t.X, t.Y, Tier)))
            return CommandResult.Fail("Nothing to build: the road is already there (or planned)");

        // Dirt roads are free: roadworkers build them over time. Better surfaces and bridges are paid up front.
        var cost = Roads.PlanCost(world, settlement, Tiles, Tier);
        if (!settlement.Stock.TryTakeAll(cost))
            return CommandResult.Fail($"Needs {string.Join(", ", cost.Select(kv => $"{kv.Value} {kv.Key}"))}");
        settlement.Ledger.RecordAll(cost, -1, Economy.LedgerKeys.Building);

        foreach (var (x, y) in Tiles) Roads.Plan(world, settlement, x, y, Tier);
        return CommandResult.Success();
    }
}

public sealed record RemoveRoadCommand(int SettlementId, IReadOnlyList<(int X, int Y)> Tiles) : ISettlementCommand
{
    public CommandResult Execute(GameWorld world)
    {
        var settlement = world.FindSettlement(SettlementId);
        if (settlement == null) return CommandResult.Fail("Unknown settlement");

        int before = settlement.Roads.Count, plansBefore = settlement.RoadPlans.Count;
        foreach (var (x, y) in Tiles)
        {
            Roads.Unplan(world, settlement, x, y); // a pending road or resurfacing goes too, with its materials refunded
            if (settlement.Roads.Contains((x, y))) Roads.Remove(world, settlement, x, y);
        }
        if (settlement.Roads.Count == before && settlement.RoadPlans.Count == plansBefore) return CommandResult.Fail("No road there");
        if (settlement.Roads.Count != before) Logistics.UpdateHaulFactors(world, settlement);
        return CommandResult.Success();
    }
}

/// <summary>
/// Tears a building down. Workers and residents are released, and part of the cost comes back:
/// nothing was spent yet while awaiting materials, half while under construction, a quarter once finished.
/// </summary>
public sealed record DemolishBuildingCommand(int SettlementId, int BuildingId) : ISettlementCommand
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
            settlement.Ledger.Record(resource, back, Economy.LedgerKeys.Refunds);
            returned.Add($"{back} {resource}");
        }

        Logistics.UpdateHaulFactors(world, settlement);
        world.Log(EventKind.BuildingDemolished, settlement,
            $"{b.Def.Name} demolished" + (returned.Count > 0 ? $" ({string.Join(", ", returned)} recovered)" : ""));
        return CommandResult.Success();
    }
}

/// <summary>
/// Founds a settlement for a player joining a shared world, well away from existing ones. Applied in the same
/// order on the server and every client, so they all get the same town.
/// </summary>
public sealed record FoundSettlementCommand(string OwnerId) : ICommand
{
    /// <summary>Distance between neighbouring founding spots, comfortably more than two maximum territories.</summary>
    public const int Spacing = 260;

    public CommandResult Execute(GameWorld world)
    {
        if (string.IsNullOrWhiteSpace(OwnerId)) return CommandResult.Fail("A player name is needed");
        if (world.Settlements.Any(s => s.OwnerId == OwnerId)) return CommandResult.Fail($"{OwnerId} already has a settlement");

        // The n-th new settlement goes to the n-th spot on square rings around the first one.
        var origin = world.Settlements.FirstOrDefault() is { } first ? (first.CenterX, first.CenterY) : (0, 0);
        var (dx, dy) = SpotOffset(world.Settlements.Count);
        var town = Simulation.SettlementFounder.Found(world, nearX: origin.Item1 + dx * Spacing, nearY: origin.Item2 + dy * Spacing);
        town.OwnerId = OwnerId;
        return CommandResult.Success(town.Id);
    }

    /// <summary>Offsets (in spacings) in ring order: (0,0), then the 8 around it, then the 16 around those...</summary>
    public static (int Dx, int Dy) SpotOffset(int n)
    {
        int index = 0;
        for (int r = 0; ; r++)
        for (int dy = -r; dy <= r; dy++)
        for (int dx = -r; dx <= r; dx++)
        {
            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
            if (index++ == n) return (dx, dy);
        }
    }
}

/// <summary>Switches fires, sickness and poor harvests on or off for a settlement.</summary>
public sealed record SetDisastersCommand(int SettlementId, bool Enabled) : ISettlementCommand
{
    public CommandResult Execute(GameWorld world)
    {
        var settlement = world.FindSettlement(SettlementId);
        if (settlement == null) return CommandResult.Fail("Unknown settlement");
        settlement.Rules.Disasters = Enabled;
        return CommandResult.Success();
    }
}

public sealed record SetAutoUpgradeHomesCommand(int SettlementId, bool Enabled) : ISettlementCommand
{
    public CommandResult Execute(GameWorld world)
    {
        var settlement = world.FindSettlement(SettlementId);
        if (settlement == null) return CommandResult.Fail("Unknown settlement");
        settlement.Rules.AutoUpgradeHomes = Enabled;
        return CommandResult.Success();
    }
}
