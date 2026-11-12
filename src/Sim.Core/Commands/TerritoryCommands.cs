using Sim.Core.Events;
using Sim.Core.Settlements;
using Sim.Core.Simulation;
using Sim.Core.Systems;

namespace Sim.Core.Commands;

/// <summary>
/// Territory is a set of claimed tiles, not a fixed radius. Its size is limited by an allowance that grows
/// with population, up to the cap of the settlement's town-centre tier. Within that allowance the player
/// paints where it goes (one bank of a river, along a valley...), or lets it expand evenly around the centre.
/// </summary>
public static class TerritoryRules
{
    /// <summary>A new settlement's allowance is a diamond of this radius around the centre.</summary>
    public const int BaseRadius = 44;

    /// <summary>The allowance grows by one ring of the diamond for this many citizens.</summary>
    public const int CitizensPerExtraRing = 8;

    /// <summary>No tile further than this from the centre (in either axis) can be claimed.</summary>
    public const int MaxClaimDistance = 160;

    /// <summary>Cap used when the game data has no town-centre tiers (a diamond of radius 100).</summary>
    public static readonly int DefaultCap = DiamondArea(100);

    /// <summary>Tiles within Manhattan distance r of a point.</summary>
    public static int DiamondArea(int r) => 2 * r * r + 2 * r + 1;

    /// <summary>Territory allowance from population alone, before the town-centre cap.</summary>
    public static int PopulationAllowance(int population) =>
        DiamondArea(BaseRadius + population / CitizensPerExtraRing);

    public static int SpareAllowance(Settlement s) => Math.Max(0, s.TerritoryAllowance - s.Territory.Count);

    /// <summary>Why this settlement can't claim the tile, or null if it can (ignoring adjacency and allowance).</summary>
    public static string? CheckClaim(GameWorld world, Settlement s, int x, int y)
    {
        int owner = world.Map.GetTile(x, y).TerritoryId;
        if (owner == s.Id) return "Already yours";
        if (owner != 0) return "Claimed by another settlement";
        if (Math.Max(Math.Abs(x - s.CenterX), Math.Abs(y - s.CenterY)) > MaxClaimDistance) return "Too far from the town centre";
        return null;
    }

    /// <summary>Why the tile can't be given up, or null if it can.</summary>
    public static string? CheckRelease(GameWorld world, Settlement s, int x, int y)
    {
        var tile = world.Map.GetTile(x, y);
        if (tile.TerritoryId != s.Id) return "Not your territory";
        if (tile.BuildingId != 0) return "A building stands there";
        if (tile.IsRoad) return "A road runs there";
        return null;
    }

    private static readonly (int Dx, int Dy)[] Neighbours = { (0, -1), (1, 0), (0, 1), (-1, 0) };

    private static bool TouchesTerritory(GameWorld world, Settlement s, int x, int y)
    {
        foreach (var (dx, dy) in Neighbours)
            if (world.Map.GetTile(x + dx, y + dy).TerritoryId == s.Id) return true;
        return false;
    }

    /// <summary>
    /// Claims the given tiles that connect to existing territory (directly or through other given tiles),
    /// nearest to the existing border first, until the allowance runs out. Returns how many were claimed.
    /// </summary>
    public static int ClaimConnected(GameWorld world, Settlement s, IEnumerable<(int X, int Y)> tiles)
    {
        int spare = SpareAllowance(s);
        if (spare == 0) return 0;

        var ordered = new List<(int X, int Y)>();
        var wanted = new HashSet<(int, int)>();
        foreach (var t in tiles)
            if (CheckClaim(world, s, t.X, t.Y) == null && wanted.Add(t)) ordered.Add(t);

        var queue = new Queue<(int X, int Y)>();
        foreach (var t in ordered)
            if (TouchesTerritory(world, s, t.X, t.Y) || (s.Territory.Count == 0 && t == (s.CenterX, s.CenterY)))
                queue.Enqueue(t);

        int claimed = 0;
        while (queue.Count > 0 && claimed < spare)
        {
            var (x, y) = queue.Dequeue();
            if (world.Map.GetTile(x, y).TerritoryId != 0) continue;
            Claim(world, s, x, y);
            claimed++;
            foreach (var (dx, dy) in Neighbours)
            {
                var n = (x + dx, y + dy);
                if (wanted.Contains(n) && world.Map.GetTile(n.Item1, n.Item2).TerritoryId == 0) queue.Enqueue(n);
            }
        }
        return claimed;
    }

    /// <summary>
    /// Claims spare allowance evenly around the centre, ring by ring (a diamond, like the old fixed radius),
    /// skipping land other settlements hold. Returns how many tiles were claimed.
    /// </summary>
    public static int ExpandEvenly(GameWorld world, Settlement s)
    {
        int spare = SpareAllowance(s);
        int claimed = 0;
        for (int r = 0; r <= MaxClaimDistance && claimed < spare; r++)
        {
            foreach (var (x, y) in ManhattanRing(s.CenterX, s.CenterY, r))
            {
                if (claimed >= spare) break;
                if (CheckClaim(world, s, x, y) != null) continue;
                if (r > 0 && !TouchesTerritory(world, s, x, y)) continue;
                Claim(world, s, x, y);
                claimed++;
            }
        }
        return claimed;
    }

    /// <summary>Gives up the tiles that can be released. Returns how many were.</summary>
    public static int Release(GameWorld world, Settlement s, IEnumerable<(int X, int Y)> tiles)
    {
        int released = 0;
        foreach (var (x, y) in tiles)
        {
            if (CheckRelease(world, s, x, y) != null) continue;
            world.Map.GetTile(x, y).TerritoryId = 0;
            s.Territory.Remove((x, y));
            released++;
        }
        if (released > 0)
        {
            s.TerritoryVersion++;
            s.TerritoryExtent = s.Territory.Count == 0
                ? 0
                : s.Territory.Max(t => Math.Max(Math.Abs(t.X - s.CenterX), Math.Abs(t.Y - s.CenterY)));
        }
        return released;
    }

    /// <summary>Marks a tile as this settlement's (no checks; also used when loading saves).</summary>
    internal static void Claim(GameWorld world, Settlement s, int x, int y)
    {
        world.Map.GetTile(x, y).TerritoryId = s.Id;
        if (!s.Territory.Add((x, y))) return;
        s.TerritoryVersion++;
        s.TerritoryExtent = Math.Max(s.TerritoryExtent, Math.Max(Math.Abs(x - s.CenterX), Math.Abs(y - s.CenterY)));
    }

    /// <summary>Points at Manhattan distance r, in a fixed order.</summary>
    public static IEnumerable<(int X, int Y)> ManhattanRing(int cx, int cy, int r)
    {
        if (r == 0)
        {
            yield return (cx, cy);
            yield break;
        }
        for (int dx = -r; dx <= r; dx++)
        {
            int dy = r - Math.Abs(dx);
            yield return (cx + dx, cy - dy);
            if (dy != 0) yield return (cx + dx, cy + dy);
        }
    }
}

/// <summary>Daily territory upkeep: auto-expansion when the settlement's rules allow it.</summary>
public static class TerritorySystem
{
    public static void Daily(GameWorld world, Settlement s)
    {
        if (s.Rules.AutoExpandTerritory) TerritoryRules.ExpandEvenly(world, s);
    }
}

/// <summary>Claims painted tiles. Only tiles connected to existing territory count, up to the allowance.</summary>
public sealed record ClaimTerritoryCommand(int SettlementId, IReadOnlyList<(int X, int Y)> Tiles) : ICommand
{
    public CommandResult Execute(GameWorld world)
    {
        var s = world.FindSettlement(SettlementId);
        if (s == null) return CommandResult.Fail("Unknown settlement");
        if (Tiles.Count == 0) return CommandResult.Fail("No tiles");
        if (TerritoryRules.SpareAllowance(s) == 0)
            return CommandResult.Fail($"Territory allowance used up ({s.Territory.Count}/{s.TerritoryAllowance} tiles)");

        int claimed = TerritoryRules.ClaimConnected(world, s, Tiles);
        if (claimed == 0)
        {
            string? reason = Tiles.Select(t => TerritoryRules.CheckClaim(world, s, t.X, t.Y)).FirstOrDefault(e => e != null);
            return CommandResult.Fail(reason ?? "New territory must touch your existing territory");
        }
        return CommandResult.Success();
    }
}

/// <summary>Gives up territory tiles that have no buildings or roads on them, freeing allowance.</summary>
public sealed record ReleaseTerritoryCommand(int SettlementId, IReadOnlyList<(int X, int Y)> Tiles) : ICommand
{
    public CommandResult Execute(GameWorld world)
    {
        var s = world.FindSettlement(SettlementId);
        if (s == null) return CommandResult.Fail("Unknown settlement");
        if (TerritoryRules.Release(world, s, Tiles) > 0) return CommandResult.Success();
        string? reason = Tiles.Select(t => TerritoryRules.CheckRelease(world, s, t.X, t.Y)).FirstOrDefault(e => e != null);
        return CommandResult.Fail(reason ?? "No tiles");
    }
}

/// <summary>Spends all spare allowance evenly around the town centre.</summary>
public sealed record ExpandTerritoryCommand(int SettlementId) : ICommand
{
    public CommandResult Execute(GameWorld world)
    {
        var s = world.FindSettlement(SettlementId);
        if (s == null) return CommandResult.Fail("Unknown settlement");
        if (TerritoryRules.SpareAllowance(s) == 0)
            return CommandResult.Fail($"Territory allowance used up ({s.Territory.Count}/{s.TerritoryAllowance} tiles)");
        return TerritoryRules.ExpandEvenly(world, s) > 0
            ? CommandResult.Success()
            : CommandResult.Fail("No unclaimed land next to your territory");
    }
}

public sealed record SetAutoExpandTerritoryCommand(int SettlementId, bool Enabled) : ICommand
{
    public CommandResult Execute(GameWorld world)
    {
        var s = world.FindSettlement(SettlementId);
        if (s == null) return CommandResult.Fail("Unknown settlement");
        s.Rules.AutoExpandTerritory = Enabled;
        return CommandResult.Success();
    }
}

/// <summary>
/// Rebuilds the town centre as its next tier, in place. The new tier is a normal construction site (materials,
/// then builders); the territory cap rises when it's finished, and doesn't drop in the meantime.
/// </summary>
public sealed record UpgradeTownCentreCommand(int SettlementId) : ICommand
{
    /// <summary>Why the settlement can't upgrade its centre now, or null if it can.</summary>
    public static string? Check(GameWorld world, Settlement s, out Data.BuildingDef? next)
    {
        next = null;
        if (s.TownCentre is not { } centre) return "No town centre";
        if (!centre.IsOperational) return $"The {centre.Def.Name} is still being built";
        if (centre.Def.TownCentre!.UpgradeTo is not { } nextId) return $"The {centre.Def.Name} is the highest tier";
        next = world.Data.Building(nextId);
        if (s.Citizens.Count < next.TownCentre!.MinPopulation)
            return $"Needs {next.TownCentre.MinPopulation} people (you have {s.Citizens.Count})";
        return null;
    }

    public CommandResult Execute(GameWorld world)
    {
        var s = world.FindSettlement(SettlementId);
        if (s == null) return CommandResult.Fail("Unknown settlement");
        if (Check(world, s, out var next) is { } error) return CommandResult.Fail(error);

        var old = s.TownCentre!;
        Work.ReleaseWorkers(s, old);
        for (int y = old.Y; y < old.Y + old.Def.Height; y++)
        for (int x = old.X; x < old.X + old.Def.Width; x++)
            world.Map.GetTile(x, y).BuildingId = 0;
        s.RemoveBuilding(old);

        var upgraded = Placement.Place(world, s, next!, old.X, old.Y);
        world.Log(EventKind.BuildingPlaced, s, $"{old.Def.Name} is being rebuilt as a {next!.Name}");
        return CommandResult.Success(upgraded.Id);
    }
}
