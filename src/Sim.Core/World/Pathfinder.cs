namespace Sim.Core.World;

/// <summary>
/// Grid pathfinding over the tile map. Deterministic: ties in the priority queue are broken by
/// insertion order, so the same map always gives the same path.
/// </summary>
public static class Pathfinder
{
    /// <summary>Walking cost of a dirt road or bridge (open ground is 1).</summary>
    public const double RoadCost = 0.4;

    private static double[] _roadWalkCosts = { RoadCost };
    private static double[] _roadSpeeds = { 1 };
    private static double _cheapestRoad = RoadCost;

    /// <summary>Surface costs per road tier, from roads.json (set when game data loads).</summary>
    public static void SetRoadTiers(double[] walkCosts, double[] caravanSpeeds)
    {
        _roadWalkCosts = walkCosts.Length > 0 ? walkCosts : new[] { RoadCost };
        _roadSpeeds = caravanSpeeds.Length > 0 ? caravanSpeeds : new[] { 1.0 };
        _cheapestRoad = _roadWalkCosts.Min();
    }

    public static double RoadWalkCost(in Tile tile) =>
        tile.Terrain == Terrain.Water ? RoadCost : _roadWalkCosts[Math.Min(tile.RoadTier, _roadWalkCosts.Length - 1)];

    /// <summary>Caravan travel cost of a road tile (1 for dirt; better surfaces are cheaper).</summary>
    public static double RoadTravelCost(in Tile tile) =>
        tile.Terrain == Terrain.Water ? 1 : 1 / _roadSpeeds[Math.Min(tile.RoadTier, _roadSpeeds.Length - 1)];

    /// <summary>Road-building cost of crossing open water, where a bridge would have to be built.</summary>
    public const double BridgePlanCost = 5.0;
    public const int DefaultMaxExpanded = 20_000;

    public enum Mode
    {
        /// <summary>People walking: avoids water and buildings, prefers roads.</summary>
        Walk,

        /// <summary>Laying roads: buildable ground, existing roads preferred, water crossed by (expensive) bridges.</summary>
        BuildRoad,

        /// <summary>Caravans: only along finished roads (and bridges).</summary>
        RoadOnly,
    }

    private static readonly (int Dx, int Dy)[] Directions = { (0, -1), (1, 0), (0, 1), (-1, 0) };

    /// <summary>Cost to step onto a tile, or null if it can't be entered.</summary>
    public static double? StepCost(in Tile tile, Mode mode)
    {
        if (tile.BuildingId != 0) return null;
        if (mode == Mode.BuildRoad)
            return tile.IsRoad || tile.RoadPlanned ? 0.5 : tile.IsBuildable ? 1.0 : tile.Terrain == Terrain.Water ? BridgePlanCost : null;
        if (mode == Mode.RoadOnly) return tile.IsRoad ? RoadTravelCost(tile) : null;

        if (tile.IsRoad) return RoadWalkCost(tile); // includes bridges over water
        return tile.Terrain switch
        {
            Terrain.Water => null,
            Terrain.Forest => 1.6,
            Terrain.Rock or Terrain.IronDeposit => 2.0,
            _ => 1.0,
        };
    }

    /// <summary>A* from one tile to another. The start and goal may themselves be blocked (e.g. a doorway).</summary>
    public static List<(int X, int Y)>? FindPath(WorldMap map, (int X, int Y) from, (int X, int Y) to,
        Mode mode = Mode.Walk, int maxExpanded = DefaultMaxExpanded)
    {
        double minStep = mode switch { Mode.Walk => _cheapestRoad, Mode.RoadOnly => 1 / _roadSpeeds.Max(), _ => 0.5 };
        return Search(map, from, (x, y) => x == to.X && y == to.Y, mode, maxExpanded,
            (x, y) => (Math.Abs(x - to.X) + Math.Abs(y - to.Y)) * minStep, allowBlockedGoal: true);
    }

    /// <summary>
    /// A* from a tile to any goal tile, guided towards <paramref name="towards"/> (not guaranteed shortest when goals
    /// lie off that direction, but fast over long distances).
    /// </summary>
    public static List<(int X, int Y)>? FindPathTowards(WorldMap map, (int X, int Y) from, Func<int, int, bool> isGoal,
        (int X, int Y) towards, Mode mode, int maxExpanded, Func<int, int, bool>? blocked = null)
    {
        double minStep = mode switch { Mode.Walk => _cheapestRoad, Mode.RoadOnly => 1 / _roadSpeeds.Max(), _ => 0.5 };
        return Search(map, from, isGoal, mode, maxExpanded,
            (x, y) => (Math.Abs(x - towards.X) + Math.Abs(y - towards.Y)) * minStep, allowBlockedGoal: false, blocked);
    }

    /// <summary>Dijkstra from a tile to the nearest tile matching <paramref name="isGoal"/>.</summary>
    public static List<(int X, int Y)>? FindPathToAny(WorldMap map, (int X, int Y) from, Func<int, int, bool> isGoal,
        Mode mode = Mode.Walk, int maxExpanded = DefaultMaxExpanded, Func<int, int, bool>? blocked = null) =>
        Search(map, from, isGoal, mode, maxExpanded, (_, _) => 0, allowBlockedGoal: false, blocked);

    private static List<(int X, int Y)>? Search(WorldMap map, (int X, int Y) from, Func<int, int, bool> isGoal,
        Mode mode, int maxExpanded, Func<int, int, double> heuristic, bool allowBlockedGoal,
        Func<int, int, bool>? blocked = null)
    {
        var open = new PriorityQueue<(int X, int Y), (double F, long Order)>();
        var cost = new Dictionary<(int, int), double> { [from] = 0 };
        var cameFrom = new Dictionary<(int, int), (int, int)>();
        var closed = new HashSet<(int, int)>();
        long order = 0;
        open.Enqueue(from, (heuristic(from.X, from.Y), order++));

        while (open.TryDequeue(out var current, out _))
        {
            if (!closed.Add(current)) continue;
            if (isGoal(current.X, current.Y)) return Reconstruct(cameFrom, current);
            if (closed.Count > maxExpanded) return null;

            double g = cost[current];
            foreach (var (dx, dy) in Directions)
            {
                var next = (current.X + dx, current.Y + dy);
                if (closed.Contains(next)) continue;

                double? step = blocked != null && blocked(next.Item1, next.Item2)
                    ? null
                    : StepCost(map.GetTile(next.Item1, next.Item2), mode);
                if (step == null)
                {
                    if (!allowBlockedGoal || !isGoal(next.Item1, next.Item2)) continue;
                    step = 1.0;
                }

                double ng = g + step.Value;
                if (cost.TryGetValue(next, out double old) && old <= ng) continue;
                cost[next] = ng;
                cameFrom[next] = current;
                open.Enqueue(next, (ng + heuristic(next.Item1, next.Item2), order++));
            }
        }
        return null;
    }

    private static List<(int X, int Y)> Reconstruct(Dictionary<(int, int), (int, int)> cameFrom, (int, int) end)
    {
        var path = new List<(int X, int Y)> { end };
        while (cameFrom.TryGetValue(end, out var previous))
        {
            path.Add(previous);
            end = previous;
        }
        path.Reverse();
        return path;
    }

    /// <summary>
    /// Multi-source Dijkstra over a rectangle: the walking cost from the nearest source to every tile.
    /// Used for logistics (distance from stockpiles to each workplace).
    /// </summary>
    public static CostGrid CostField(WorldMap map, IEnumerable<(int X, int Y)> sources, int minX, int minY, int maxX, int maxY,
        Mode mode = Mode.Walk)
    {
        var grid = new CostGrid(minX, minY, maxX, maxY);
        var open = new PriorityQueue<(int X, int Y), (double Cost, long Order)>();
        long order = 0;

        foreach (var s in sources)
        {
            if (!grid.Contains(s.X, s.Y) || StepCost(map.GetTile(s.X, s.Y), mode) == null) continue;
            if (grid[s.X, s.Y] <= 0) continue;
            grid[s.X, s.Y] = 0;
            open.Enqueue(s, (0, order++));
        }

        while (open.TryDequeue(out var current, out var priority))
        {
            if (priority.Cost > grid[current.X, current.Y]) continue;
            foreach (var (dx, dy) in Directions)
            {
                int nx = current.X + dx, ny = current.Y + dy;
                if (!grid.Contains(nx, ny)) continue;
                double? step = StepCost(map.GetTile(nx, ny), mode);
                if (step == null) continue;
                double nc = priority.Cost + step.Value;
                if (nc >= grid[nx, ny]) continue;
                grid[nx, ny] = nc;
                open.Enqueue((nx, ny), (nc, order++));
            }
        }
        return grid;
    }
}

/// <summary>Travel costs over a rectangle of tiles; unreachable or outside tiles are infinite.</summary>
public sealed class CostGrid
{
    private readonly double[] _cost;

    public int MinX { get; }
    public int MinY { get; }
    public int Width { get; }
    public int Height { get; }

    public CostGrid(int minX, int minY, int maxX, int maxY)
    {
        MinX = minX;
        MinY = minY;
        Width = maxX - minX + 1;
        Height = maxY - minY + 1;
        _cost = new double[Width * Height];
        Array.Fill(_cost, double.PositiveInfinity);
    }

    public bool Contains(int x, int y) => x >= MinX && y >= MinY && x < MinX + Width && y < MinY + Height;

    public double this[int x, int y]
    {
        get => Contains(x, y) ? _cost[(y - MinY) * Width + (x - MinX)] : double.PositiveInfinity;
        set => _cost[(y - MinY) * Width + (x - MinX)] = value;
    }
}
