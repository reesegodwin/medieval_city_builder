using Sim.Core.Economy;
using Sim.Core.Settlements;
using Sim.Core.Simulation;
using Sim.Core.Systems;
using Sim.Core.World;

namespace Sim.Core.Commands;

/// <summary>
/// Low-level wall tile changes (callers go through the wall commands). Walls are drawn like roads, paid when
/// planned and built by the town-centre crew. A finished wall stops everyone except at gates; a wall drawn across
/// a road becomes a gate there.
/// </summary>
public static class Walls
{
    /// <summary>Share of a wall's materials returned when a finished wall is torn down.</summary>
    public const double DemolishRefund = 0.25;

    public static Data.WallTierDef Def(GameWorld world, int tier) => world.Data.WallTiers[Math.Clamp(tier, 0, world.Data.WallTiers.Count - 1)];

    /// <summary>Returns null if a wall can go on this tile, otherwise why not.</summary>
    public static string? Check(GameWorld world, Settlement settlement, int x, int y)
    {
        var tile = world.Map.GetTile(x, y);
        if (tile.TerritoryId != settlement.Id) return "Walls go inside the settlement's territory";
        if (tile.BuildingId != 0) return "Blocked by a building";
        if (tile.Terrain == Terrain.Water) return "Cannot build a wall on water";
        return null;
    }

    /// <summary>Whether planning this tier (or a gate) here would do anything.</summary>
    public static bool NeedsWork(Settlement settlement, in Tile tile, int x, int y, byte tier, bool gate)
    {
        if (tile.WallPlanned) return false;
        if (!settlement.Walls.TryGetValue((x, y), out var wall)) return true;
        return tier > wall.Tier || (gate && !wall.Gate);
    }

    /// <summary>A wall laid across a road (or asked for as a gate) is a gate.</summary>
    private static bool IsGateTile(in Tile tile, bool gate) => gate || tile.IsRoad || tile.RoadPlanned;

    /// <summary>Materials to plan these tiles: a gate's cost where it's a gate, the wall's cost elsewhere.</summary>
    public static Dictionary<string, int> PlanCost(GameWorld world, Settlement settlement, IEnumerable<(int X, int Y)> tiles, byte tier,
        IReadOnlyCollection<(int X, int Y)>? gates = null)
    {
        var cost = new Dictionary<string, int>();
        foreach (var (x, y) in tiles.Distinct())
        {
            var tile = world.Map.GetTile(x, y);
            byte target = settlement.Walls.TryGetValue((x, y), out var existing) ? Math.Max(tier, existing.Tier) : tier;
            bool gate = IsGateTile(tile, gates?.Contains((x, y)) == true) || existing?.Gate == true;
            if (!NeedsWork(settlement, tile, x, y, target, gate)) continue;
            var def = Def(world, target);
            foreach (var (r, n) in gate ? def.GateCost : def.Cost) cost[r] = cost.GetValueOrDefault(r) + n;
        }
        return cost;
    }

    internal static void Plan(GameWorld world, Settlement settlement, int x, int y, byte tier, bool gate)
    {
        ref var tile = ref world.Map.GetTile(x, y);
        byte target = settlement.Walls.TryGetValue((x, y), out var existing) ? Math.Max(tier, existing.Tier) : tier;
        bool isGate = IsGateTile(tile, gate) || existing?.Gate == true;
        if (!NeedsWork(settlement, tile, x, y, target, isGate)) return;
        tile.WallPlanned = true;
        // A new gate is open from the start (nothing stands there yet); an existing wall opens once the gate is built.
        if (isGate && existing == null) tile.IsGate = true;
        settlement.WallPlans.Add(new WallPlan { X = x, Y = y, Tier = target, Gate = isGate });
        settlement.WallVersion++;
    }

    /// <summary>Drops a planned tile and refunds its materials.</summary>
    internal static bool Unplan(GameWorld world, Settlement settlement, int x, int y)
    {
        int index = settlement.WallPlans.FindIndex(p => p.X == x && p.Y == y);
        if (index < 0) return false;
        var plan = settlement.WallPlans[index];
        ref var tile = ref world.Map.GetTile(x, y);
        tile.WallPlanned = false;
        if (!settlement.Walls.TryGetValue((x, y), out var existing)) tile.IsGate = false;
        else tile.IsGate = existing.Gate;
        settlement.WallPlans.RemoveAt(index);
        var def = Def(world, plan.Tier);
        foreach (var (r, n) in plan.Gate ? def.GateCost : def.Cost)
        {
            settlement.Stock.Add(r, n);
            settlement.Ledger.Record(r, n, LedgerKeys.Refunds);
        }
        settlement.WallVersion++;
        return true;
    }

    internal static void Finish(GameWorld world, Settlement settlement, WallPlan plan)
    {
        ref var tile = ref world.Map.GetTile(plan.X, plan.Y);
        tile.WallPlanned = false;
        bool gate = plan.Gate || tile.IsRoad;
        tile.WallTier = (byte)(plan.Tier + 1);
        tile.IsGate = gate;
        settlement.Walls[(plan.X, plan.Y)] = new WallSegment { Tier = plan.Tier, Gate = gate, Hp = Def(world, plan.Tier).HitPoints };
        settlement.WallPlans.Remove(plan);
        settlement.WallVersion++;
    }

    /// <summary>Builds every planned tile at once (a town with nobody to build).</summary>
    public static void FinishAllPlans(GameWorld world, Settlement settlement)
    {
        if (settlement.WallPlans.Count == 0) return;
        foreach (var plan in settlement.WallPlans.ToList()) Finish(world, settlement, plan);
        Logistics.UpdateHaulFactors(world, settlement);
    }

    internal static void Remove(GameWorld world, Settlement settlement, int x, int y)
    {
        if (!settlement.Walls.TryGetValue((x, y), out var wall)) return;
        ref var tile = ref world.Map.GetTile(x, y);
        tile.WallTier = 0;
        tile.IsGate = false;
        settlement.Walls.Remove((x, y));
        var def = Def(world, wall.Tier);
        foreach (var (r, n) in wall.Gate ? def.GateCost : def.Cost)
        {
            int back = (int)(n * DemolishRefund);
            if (back <= 0) continue;
            settlement.Stock.Add(r, back);
            settlement.Ledger.Record(r, back, LedgerKeys.Refunds);
        }
        settlement.WallVersion++;
    }
}

/// <summary>
/// Plans a wall of a tier along the tiles (paid up front; the crew builds it over time). Existing walls of ours are
/// upgraded; road tiles become gates. All tiles must be valid, or nothing is planned.
/// </summary>
public sealed record PlaceWallCommand(int SettlementId, IReadOnlyList<(int X, int Y)> Tiles, byte Tier = 0,
    IReadOnlyList<(int X, int Y)>? Gates = null) : ISettlementCommand
{
    public CommandResult Execute(GameWorld world)
    {
        var settlement = world.FindSettlement(SettlementId);
        if (settlement == null) return CommandResult.Fail("Unknown settlement");
        if (Tiles.Count == 0) return CommandResult.Fail("No tiles");
        if (Tier >= world.Data.WallTiers.Count) return CommandResult.Fail("Unknown kind of wall");
        foreach (var (x, y) in Tiles)
        {
            if (settlement.Walls.ContainsKey((x, y)) || world.Map.GetTile(x, y).WallPlanned) continue;
            if (Walls.Check(world, settlement, x, y) is { } error) return CommandResult.Fail($"({x},{y}): {error}");
        }
        var gates = (IReadOnlyCollection<(int X, int Y)>)(Gates ?? Array.Empty<(int X, int Y)>()).ToHashSet();
        if (!Tiles.Any(t => Walls.NeedsWork(settlement, world.Map.GetTile(t.X, t.Y), t.X, t.Y, Tier, gates.Contains(t))))
            return CommandResult.Fail("Nothing to build: that wall is already there (or planned)");
        var cost = Walls.PlanCost(world, settlement, Tiles, Tier, gates);
        if (!settlement.Stock.TryTakeAll(cost))
            return CommandResult.Fail($"Needs {string.Join(", ", cost.Select(kv => $"{kv.Value} {kv.Key}"))}");
        settlement.Ledger.RecordAll(cost, -1, LedgerKeys.Building);
        foreach (var (x, y) in Tiles) Walls.Plan(world, settlement, x, y, Tier, gates.Contains((x, y)));
        return CommandResult.Success();
    }
}

/// <summary>Turns a wall tile (finished or planned) into a gate, for the gate's cost.</summary>
public sealed record PlaceGateCommand(int SettlementId, int X, int Y) : ISettlementCommand
{
    public CommandResult Execute(GameWorld world)
    {
        var settlement = world.FindSettlement(SettlementId);
        if (settlement == null) return CommandResult.Fail("Unknown settlement");
        ref var tile = ref world.Map.GetTile(X, Y);
        int planIndex = settlement.WallPlans.FindIndex(p => p.X == X && p.Y == Y);
        bool finished = settlement.Walls.TryGetValue((X, Y), out var wall);
        if (!finished && planIndex < 0) return CommandResult.Fail("Gates go in a wall");
        if ((finished && wall!.Gate) || (planIndex >= 0 && settlement.WallPlans[planIndex].Gate)) return CommandResult.Fail("Already a gate");

        if (planIndex >= 0)
        {
            // A planned wall becomes a planned gate: the wall's materials go towards the gate's.
            var plan = settlement.WallPlans[planIndex];
            var def = Walls.Def(world, plan.Tier);
            foreach (var (r, n) in def.Cost) settlement.Stock.Add(r, n);
            if (!settlement.Stock.TryTakeAll(def.GateCost))
            {
                settlement.Stock.TryTakeAll(def.Cost);
                return CommandResult.Fail($"Needs {string.Join(", ", def.GateCost.Select(kv => $"{kv.Value} {kv.Key}"))}");
            }
            settlement.Ledger.RecordAll(def.Cost, 1, LedgerKeys.Refunds);
            settlement.Ledger.RecordAll(def.GateCost, -1, LedgerKeys.Building);
            plan.Gate = true;
            if (!finished) tile.IsGate = true;
            settlement.WallVersion++;
            return CommandResult.Success();
        }

        var cost = Walls.Def(world, wall!.Tier).GateCost;
        if (!settlement.Stock.TryTakeAll(cost)) return CommandResult.Fail($"Needs {string.Join(", ", cost.Select(kv => $"{kv.Value} {kv.Key}"))}");
        settlement.Ledger.RecordAll(cost, -1, LedgerKeys.Building);
        tile.WallPlanned = true;
        settlement.WallPlans.Add(new WallPlan { X = X, Y = Y, Tier = wall.Tier, Gate = true });
        settlement.WallVersion++;
        return CommandResult.Success();
    }
}

/// <summary>Cancels planned walls (materials back) and tears down finished ones (a quarter back).</summary>
public sealed record RemoveWallCommand(int SettlementId, IReadOnlyList<(int X, int Y)> Tiles) : ISettlementCommand
{
    public CommandResult Execute(GameWorld world)
    {
        var settlement = world.FindSettlement(SettlementId);
        if (settlement == null) return CommandResult.Fail("Unknown settlement");
        int walls = settlement.Walls.Count, plans = settlement.WallPlans.Count;
        foreach (var (x, y) in Tiles)
        {
            Walls.Unplan(world, settlement, x, y);
            Walls.Remove(world, settlement, x, y);
        }
        if (settlement.Walls.Count == walls && settlement.WallPlans.Count == plans) return CommandResult.Fail("No wall there");
        if (settlement.Walls.Count != walls) Logistics.UpdateHaulFactors(world, settlement);
        return CommandResult.Success();
    }
}
