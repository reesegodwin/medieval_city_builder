using Sim.Core.Economy;
using Sim.Core.Settlements;
using Sim.Core.Simulation;
using Sim.Core.Systems;
using Sim.Core.World;

namespace Sim.Core.Commands;

/// <summary>
/// Low-level wall tile changes (callers go through the wall commands). Walls are drawn like roads, paid when
/// planned and built by the town-centre crew. A finished wall stops everyone except at gates (and gatehouses); a
/// wall drawn across a road becomes a gate there. Towers and gatehouses are placed on walls and gates.
/// </summary>
public static class Walls
{
    /// <summary>Share of a wall's materials returned when a finished wall is torn down.</summary>
    public const double DemolishRefund = 0.25;

    public static Data.WallTierDef Def(GameWorld world, int tier) => world.Data.WallTiers[Math.Clamp(tier, 0, world.Data.WallTiers.Count - 1)];

    /// <summary>What a piece of this kind of wall costs.</summary>
    public static Dictionary<string, int> PieceCost(GameWorld world, int tier, WallPiece piece)
    {
        var def = Def(world, tier);
        return piece switch
        {
            WallPiece.Gate => def.GateCost,
            WallPiece.Tower => def.Tower.Cost,
            WallPiece.Gatehouse => def.Gatehouse.Cost,
            _ => def.Cost,
        };
    }

    public static int PieceHitPoints(GameWorld world, int tier, WallPiece piece)
    {
        var def = Def(world, tier);
        return piece switch { WallPiece.Tower => def.Tower.HitPoints, WallPiece.Gatehouse => def.Gatehouse.HitPoints, _ => def.HitPoints };
    }

    public static double PieceWorkDays(GameWorld world, int tier, WallPiece piece)
    {
        var def = Def(world, tier);
        return piece switch { WallPiece.Tower => def.Tower.WorkDays, WallPiece.Gatehouse => def.Gatehouse.WorkDays, _ => def.WorkDays };
    }

    public static string PieceName(GameWorld world, int tier, WallPiece piece)
    {
        var def = Def(world, tier);
        return piece switch
        {
            WallPiece.Gate => $"{def.Name} gate",
            WallPiece.Tower => def.Tower.Name,
            WallPiece.Gatehouse => def.Gatehouse.Name,
            _ => def.Name,
        };
    }

    /// <summary>Whether a piece can become another: walls take gates and towers, gates take gatehouses.</summary>
    public static bool CanBecome(WallPiece from, WallPiece to) =>
        from == to || (from == WallPiece.Wall && to is WallPiece.Gate or WallPiece.Tower) ||
        (from == WallPiece.Gate && to == WallPiece.Gatehouse);

    /// <summary>
    /// The piece a tile ends up as: what's there (a plain wall if nothing), turned into the wanted piece where it can
    /// be; a plain wall across a road is a gate.
    /// </summary>
    public static WallPiece Resolve(in Tile tile, WallPiece? existing, WallPiece? want)
    {
        var piece = existing ?? WallPiece.Wall;
        if (want is { } w && CanBecome(piece, w)) piece = w;
        if (piece == WallPiece.Wall && (tile.IsRoad || tile.RoadPlanned)) piece = WallPiece.Gate;
        return piece;
    }

    /// <summary>Returns null if a wall can go on this tile, otherwise why not.</summary>
    public static string? Check(GameWorld world, Settlement settlement, int x, int y)
    {
        var tile = world.Map.GetTile(x, y);
        if (tile.TerritoryId != settlement.Id) return "Walls go inside the settlement's territory";
        if (tile.BuildingId != 0) return "Blocked by a building";
        if (tile.Terrain == Terrain.Water) return "Cannot build a wall on water";
        return null;
    }

    /// <summary>Whether planning this tier (and piece) here would do anything.</summary>
    public static bool NeedsWork(Settlement settlement, in Tile tile, int x, int y, byte tier, WallPiece? want = null)
    {
        if (tile.WallPlanned) return false;
        if (!settlement.Walls.TryGetValue((x, y), out var wall)) return true;
        return tier > wall.Tier || Resolve(tile, wall.Piece, want) != wall.Piece;
    }

    /// <summary>Materials to plan these tiles at a tier (each tile at the price of the piece it will be).</summary>
    public static Dictionary<string, int> PlanCost(GameWorld world, Settlement settlement, IEnumerable<(int X, int Y)> tiles, byte tier,
        IReadOnlyCollection<(int X, int Y)>? gates = null)
    {
        var cost = new Dictionary<string, int>();
        foreach (var (x, y) in tiles.Distinct())
        {
            var tile = world.Map.GetTile(x, y);
            WallPiece? want = gates?.Contains((x, y)) == true ? WallPiece.Gate : null;
            if (!NeedsWork(settlement, tile, x, y, tier, want)) continue;
            settlement.Walls.TryGetValue((x, y), out var existing);
            byte target = existing != null ? Math.Max(tier, existing.Tier) : tier;
            foreach (var (r, n) in PieceCost(world, target, Resolve(tile, existing?.Piece, want))) cost[r] = cost.GetValueOrDefault(r) + n;
        }
        return cost;
    }

    internal static void Plan(GameWorld world, Settlement settlement, int x, int y, byte tier, WallPiece? want)
    {
        ref var tile = ref world.Map.GetTile(x, y);
        if (!NeedsWork(settlement, tile, x, y, tier, want)) return;
        settlement.Walls.TryGetValue((x, y), out var existing);
        byte target = existing != null ? Math.Max(tier, existing.Tier) : tier;
        var piece = Resolve(tile, existing?.Piece, want);
        tile.WallPlanned = true;
        // A new opening is open from the start (nothing stands there yet); an existing wall opens once it's built.
        if (existing == null) tile.IsGate = piece.IsOpening();
        settlement.WallPlans.Add(new WallPlan { X = x, Y = y, Tier = target, Piece = piece });
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
        tile.IsGate = settlement.Walls.TryGetValue((x, y), out var existing) && existing.Gate;
        settlement.WallPlans.RemoveAt(index);
        foreach (var (r, n) in PieceCost(world, plan.Tier, plan.Piece))
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
        var piece = plan.Piece == WallPiece.Wall && tile.IsRoad ? WallPiece.Gate : plan.Piece;
        tile.WallTier = (byte)(plan.Tier + 1);
        tile.IsGate = piece.IsOpening();
        settlement.Walls[(plan.X, plan.Y)] = new WallSegment
            { Tier = plan.Tier, Piece = piece, Hp = PieceHitPoints(world, plan.Tier, piece) };
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

    /// <summary>A wall tile knocked down in a siege: gone, with nothing to salvage. The ring opens until rebuilt.</summary>
    public static void Breach(GameWorld world, Settlement settlement, int x, int y)
    {
        if (!settlement.Walls.Remove((x, y))) return;
        ref var tile = ref world.Map.GetTile(x, y);
        tile.WallTier = 0;
        tile.IsGate = false;
        settlement.WallVersion++;
    }

    internal static void Remove(GameWorld world, Settlement settlement, int x, int y)
    {
        if (!settlement.Walls.TryGetValue((x, y), out var wall)) return;
        ref var tile = ref world.Map.GetTile(x, y);
        tile.WallTier = 0;
        tile.IsGate = false;
        settlement.Walls.Remove((x, y));
        foreach (var (r, n) in PieceCost(world, wall.Tier, wall.Piece))
        {
            int back = (int)(n * DemolishRefund);
            if (back <= 0) continue;
            settlement.Stock.Add(r, back);
            settlement.Ledger.Record(r, back, LedgerKeys.Refunds);
        }
        settlement.WallVersion++;
    }

    /// <summary>
    /// Turns a wall (finished or planned) into a gate or tower, or a gate into a gatehouse. A planned piece is
    /// swapped (its materials go towards the new one); a finished one gets a plan and changes once built.
    /// </summary>
    public static CommandResult PlacePiece(GameWorld world, Settlement settlement, int x, int y, WallPiece piece)
    {
        ref var tile = ref world.Map.GetTile(x, y);
        int planIndex = settlement.WallPlans.FindIndex(p => p.X == x && p.Y == y);
        bool finished = settlement.Walls.TryGetValue((x, y), out var wall);
        string where = piece == WallPiece.Gatehouse ? "Gatehouses go on a gate" : $"{piece}s go in a wall";
        if (!finished && planIndex < 0) return CommandResult.Fail(where);

        if (planIndex >= 0)
        {
            var plan = settlement.WallPlans[planIndex];
            if (plan.Piece == piece) return CommandResult.Fail($"Already a {piece.ToString().ToLowerInvariant()}");
            if (!CanBecome(plan.Piece, piece)) return CommandResult.Fail(where);
            var old = PieceCost(world, plan.Tier, plan.Piece);
            var cost = PieceCost(world, plan.Tier, piece);
            foreach (var (r, n) in old) settlement.Stock.Add(r, n);
            if (!settlement.Stock.TryTakeAll(cost))
            {
                settlement.Stock.TryTakeAll(old);
                return CommandResult.Fail($"Needs {string.Join(", ", cost.Select(kv => $"{kv.Value} {kv.Key}"))}");
            }
            settlement.Ledger.RecordAll(old, 1, LedgerKeys.Refunds);
            settlement.Ledger.RecordAll(cost, -1, LedgerKeys.Building);
            plan.Piece = piece;
            plan.Progress = 0;
            if (!finished) tile.IsGate = piece.IsOpening();
            settlement.WallVersion++;
            return CommandResult.Success();
        }

        if (wall!.Piece == piece) return CommandResult.Fail($"Already a {piece.ToString().ToLowerInvariant()}");
        if (!CanBecome(wall.Piece, piece)) return CommandResult.Fail(where);
        var price = PieceCost(world, wall.Tier, piece);
        if (!settlement.Stock.TryTakeAll(price)) return CommandResult.Fail($"Needs {string.Join(", ", price.Select(kv => $"{kv.Value} {kv.Key}"))}");
        settlement.Ledger.RecordAll(price, -1, LedgerKeys.Building);
        tile.WallPlanned = true;
        settlement.WallPlans.Add(new WallPlan { X = x, Y = y, Tier = wall.Tier, Piece = piece });
        settlement.WallVersion++;
        return CommandResult.Success();
    }
}

/// <summary>
/// Plans a wall of a tier along the tiles (paid up front; the crew builds it over time). Existing walls of ours are
/// upgraded, keeping their gates, towers and gatehouses; road tiles become gates. All tiles must be valid, or nothing
/// is planned.
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
        WallPiece? Want((int X, int Y) t) => gates.Contains(t) ? WallPiece.Gate : null;
        if (!Tiles.Any(t => Walls.NeedsWork(settlement, world.Map.GetTile(t.X, t.Y), t.X, t.Y, Tier, Want(t))))
            return CommandResult.Fail("Nothing to build: that wall is already there (or planned)");
        var cost = Walls.PlanCost(world, settlement, Tiles, Tier, gates);
        if (!settlement.Stock.TryTakeAll(cost))
            return CommandResult.Fail($"Needs {string.Join(", ", cost.Select(kv => $"{kv.Value} {kv.Key}"))}");
        settlement.Ledger.RecordAll(cost, -1, LedgerKeys.Building);
        foreach (var t in Tiles) Walls.Plan(world, settlement, t.X, t.Y, Tier, Want(t));
        return CommandResult.Success();
    }
}

/// <summary>Turns a wall tile (finished or planned) into a gate, for the gate's cost.</summary>
public sealed record PlaceGateCommand(int SettlementId, int X, int Y) : ISettlementCommand
{
    public CommandResult Execute(GameWorld world) =>
        world.FindSettlement(SettlementId) is { } s ? Walls.PlacePiece(world, s, X, Y, WallPiece.Gate) : CommandResult.Fail("Unknown settlement");
}

/// <summary>Raises a tower on a wall tile: taller than the wall, holding more defenders.</summary>
public sealed record PlaceTowerCommand(int SettlementId, int X, int Y) : ISettlementCommand
{
    public CommandResult Execute(GameWorld world) =>
        world.FindSettlement(SettlementId) is { } s ? Walls.PlacePiece(world, s, X, Y, WallPiece.Tower) : CommandResult.Fail("Unknown settlement");
}

/// <summary>Fortifies a gate into a gatehouse: flanking towers over the passage, still open to traffic.</summary>
public sealed record PlaceGatehouseCommand(int SettlementId, int X, int Y) : ISettlementCommand
{
    public CommandResult Execute(GameWorld world) =>
        world.FindSettlement(SettlementId) is { } s ? Walls.PlacePiece(world, s, X, Y, WallPiece.Gatehouse) : CommandResult.Fail("Unknown settlement");
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
