using Sim.Core.Settlements;
using Sim.Core.Simulation;
using Sim.Core.World;

namespace Sim.Core.Systems;

/// <summary>
/// Which land lies inside a closed ring of walls, and how strong that ring is. Worked out by flooding in from the
/// edge of the town's land: finished walls (gates included: they're guarded), buildings and water stop the flood, and
/// whatever it doesn't reach is enclosed. Derived from saved state and cached by wall and territory versions.
/// </summary>
public static class WallSystem
{
    /// <summary>Enclosed tiles, and the weakest wall tier (index into walls.json) the outside touches.</summary>
    public sealed class Enclosure
    {
        public int WallVersion { get; init; }
        public int TerritoryVersion { get; init; }
        public int BuildingsVersion { get; init; }
        public HashSet<(int X, int Y)> Inside { get; } = new();
        public int RingTier { get; set; } = -1;
    }

    /// <summary>Margin around the territory's bounding box for the outside flood.</summary>
    private const int Margin = 2;

    public static Enclosure Enclose(GameWorld world, Settlement s)
    {
        if (s.EnclosureCache is { } cached && cached.WallVersion == s.WallVersion && cached.TerritoryVersion == s.TerritoryVersion &&
            cached.BuildingsVersion == s.BuildingsVersion)
            return cached;
        var result = new Enclosure { WallVersion = s.WallVersion, TerritoryVersion = s.TerritoryVersion, BuildingsVersion = s.BuildingsVersion };
        s.EnclosureCache = result;
        if (s.Walls.Count == 0 || s.Territory.Count == 0) return result;

        int x0 = s.Territory.Min(t => t.X) - Margin, x1 = s.Territory.Max(t => t.X) + Margin;
        int y0 = s.Territory.Min(t => t.Y) - Margin, y1 = s.Territory.Max(t => t.Y) + Margin;
        int w = x1 - x0 + 1, h = y1 - y0 + 1;
        var outside = new bool[w * h];
        bool Barrier(int x, int y)
        {
            var tile = world.Map.GetTile(x, y);
            return tile.WallTier > 0 || tile.BuildingId != 0 || tile.Terrain == Terrain.Water; // buildings close gaps too
        }

        var queue = new Queue<(int, int)>();
        void Seed(int x, int y)
        {
            int i = (y - y0) * w + (x - x0);
            if (outside[i] || Barrier(x, y)) return;
            outside[i] = true;
            queue.Enqueue((x, y));
        }
        for (int x = x0; x <= x1; x++) { Seed(x, y0); Seed(x, y1); }
        for (int y = y0; y <= y1; y++) { Seed(x0, y); Seed(x1, y); }

        int weakest = int.MaxValue;
        while (queue.Count > 0)
        {
            var (x, y) = queue.Dequeue();
            foreach (var (dx, dy) in new[] { (0, -1), (1, 0), (0, 1), (-1, 0) })
            {
                int nx = x + dx, ny = y + dy;
                if (nx < x0 || nx > x1 || ny < y0 || ny > y1) continue;
                var tile = world.Map.GetTile(nx, ny);
                if (tile.WallTier > 0) { weakest = Math.Min(weakest, tile.WallTier - 1); continue; }
                Seed(nx, ny);
            }
        }
        if (weakest == int.MaxValue) return result; // no wall faces the outside: nothing is walled in

        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            if (outside[(y - y0) * w + (x - x0)]) continue;
            var tile = world.Map.GetTile(x, y);
            // Open ground only: buildings stop the flood, so their own tiles would always look walled in.
            if (tile.WallTier == 0 && tile.BuildingId == 0 && tile.Terrain != Terrain.Water) result.Inside.Add((x, y));
        }
        result.RingTier = result.Inside.Count > 0 ? weakest : -1;
        return result;
    }

    /// <summary>Whether a building stands inside the walls: its doorstep is walled-in ground.</summary>
    public static bool IsInside(GameWorld world, Settlement s, Building b)
    {
        var enclosure = Enclose(world, s);
        return enclosure.RingTier >= 0 && enclosure.Inside.Contains(b.DoorTile);
    }

    /// <summary>The mood for living behind the town's walls (0 when the home isn't walled in).</summary>
    public static double SafetyMood(GameWorld world, Settlement s, Building home) =>
        IsInside(world, s, home) ? Commands.Walls.Def(world, Enclose(world, s).RingTier).SafetyMood : 0;
}
