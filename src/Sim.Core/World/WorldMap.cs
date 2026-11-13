using Sim.Core.Util;

namespace Sim.Core.World;

public enum Terrain : byte
{
    Grass,
    Fertile,
    Forest,
    Rock,
    IronDeposit,
    Water,
}

public struct Tile
{
    public Terrain Terrain;

    /// <summary>Id of the building covering this tile, or 0 for none.</summary>
    public int BuildingId;

    /// <summary>A road runs over this tile. Buildings placed on it replace the road.</summary>
    public bool IsRoad;

    /// <summary>Road surface (index into roads.json: 0 dirt, 1 gravel, 2 stone). Bridges stay at 0.</summary>
    public byte RoadTier;

    /// <summary>A road (or an upgrade to it) is planned here and waiting for roadworkers.</summary>
    public bool RoadPlanned;

    /// <summary>Id of the settlement whose territory this tile is in, or 0 for unclaimed land.</summary>
    public int TerritoryId;

    /// <summary>A finished wall: 0 for none, otherwise 1 + its index in walls.json (1 palisade, 2 stone, 3 large).</summary>
    public byte WallTier;

    /// <summary>A wall (or an upgrade or gate) is planned here and waiting for the crew.</summary>
    public bool WallPlanned;

    /// <summary>The wall here is (or will be) a gate: people, carts and roads pass through.</summary>
    public bool IsGate;

    /// <summary>A finished wall that isn't a gate: nothing gets through.</summary>
    public readonly bool BlocksPassage => WallTier > 0 && !IsGate;

    public readonly bool IsBuildable => BuildingId == 0 && WallTier == 0 && !WallPlanned &&
                                        (Terrain == Terrain.Grass || Terrain == Terrain.Fertile);
}

public sealed class Chunk
{
    public const int Size = 64;

    public int ChunkX { get; }
    public int ChunkY { get; }
    public Tile[] Tiles { get; } = new Tile[Size * Size];

    public Chunk(int chunkX, int chunkY)
    {
        ChunkX = chunkX;
        ChunkY = chunkY;
    }

    public ref Tile this[int localX, int localY] => ref Tiles[localY * Size + localX];
}

/// <summary>
/// Infinite, lazily generated map. Chunks are generated from (seed, chunk coordinates) only,
/// so the world can keep expanding as players explore, and the result never depends on the
/// order chunks were visited in. Only changes (buildings) need to be persisted, not terrain.
/// </summary>
public sealed class WorldMap
{
    private readonly Dictionary<(int, int), Chunk> _chunks = new();
    private readonly WorldGenerator _generator;

    public ulong Seed { get; }
    public int LoadedChunkCount => _chunks.Count;

    public WorldMap(ulong seed)
    {
        Seed = seed;
        _generator = new WorldGenerator(seed);
    }

    public ref Tile GetTile(int x, int y)
    {
        int cx = FloorDiv(x, Chunk.Size);
        int cy = FloorDiv(y, Chunk.Size);
        var chunk = GetChunk(cx, cy);
        return ref chunk[x - cx * Chunk.Size, y - cy * Chunk.Size];
    }

    public Chunk GetChunk(int chunkX, int chunkY)
    {
        if (!_chunks.TryGetValue((chunkX, chunkY), out var chunk))
        {
            chunk = _generator.GenerateChunk(chunkX, chunkY);
            _chunks[(chunkX, chunkY)] = chunk;
        }
        return chunk;
    }

    public int CountTerrainInRadius(int x, int y, int radius, Terrain terrain)
    {
        int count = 0;
        for (int dy = -radius; dy <= radius; dy++)
        for (int dx = -radius; dx <= radius; dx++)
        {
            if (GetTile(x + dx, y + dy).Terrain == terrain) count++;
        }
        return count;
    }

    private static int FloorDiv(int a, int b) => (int)Math.Floor(a / (double)b);
}

public sealed class WorldGenerator
{
    private readonly ulong _heightSeed;
    private readonly ulong _moistureSeed;
    private readonly ulong _riverSeed;
    private readonly ulong _oreSeed;

    public WorldGenerator(ulong seed)
    {
        _heightSeed = Hashing.Mix(seed, 1);
        _moistureSeed = Hashing.Mix(seed, 2);
        _riverSeed = Hashing.Mix(seed, 3);
        _oreSeed = Hashing.Mix(seed, 4);
    }

    public Chunk GenerateChunk(int chunkX, int chunkY)
    {
        var chunk = new Chunk(chunkX, chunkY);
        for (int ly = 0; ly < Chunk.Size; ly++)
        for (int lx = 0; lx < Chunk.Size; lx++)
        {
            chunk[lx, ly].Terrain = TerrainAt(chunkX * Chunk.Size + lx, chunkY * Chunk.Size + ly);
        }
        return chunk;
    }

    /// <summary>Terrain is a pure function of world coordinates, which makes chunk borders seamless.</summary>
    public Terrain TerrainAt(int x, int y)
    {
        double height = Noise.Fbm(_heightSeed, x / 72.0, y / 72.0, 5);
        double moisture = Noise.Fbm(_moistureSeed, x / 40.0, y / 40.0, 4);

        // Rivers follow the 0.5 contour of a separate noise field, giving long winding lines.
        double riverField = Math.Abs(Noise.Fbm(_riverSeed, x / 110.0, y / 110.0, 3) - 0.5);

        if (height < 0.28) return Terrain.Water;
        if (riverField < 0.010 && height < 0.68) return Terrain.Water;
        if (height > 0.68)
        {
            return Noise.Value(_oreSeed, x / 6.0, y / 6.0) > 0.78 ? Terrain.IronDeposit : Terrain.Rock;
        }
        if (moisture > 0.60) return Terrain.Forest;
        if (riverField < 0.045 || moisture > 0.50) return Terrain.Fertile;
        return Terrain.Grass;
    }
}

public static class Noise
{
    /// <summary>Smooth value noise in [0, 1).</summary>
    public static double Value(ulong seed, double x, double y)
    {
        int x0 = (int)Math.Floor(x);
        int y0 = (int)Math.Floor(y);
        double u = Smooth(x - x0);
        double v = Smooth(y - y0);

        double a = Hashing.Hash01(seed, x0, y0);
        double b = Hashing.Hash01(seed, x0 + 1, y0);
        double c = Hashing.Hash01(seed, x0, y0 + 1);
        double d = Hashing.Hash01(seed, x0 + 1, y0 + 1);

        return Lerp(Lerp(a, b, u), Lerp(c, d, u), v);
    }

    /// <summary>Fractal (multi-octave) value noise normalized to [0, 1).</summary>
    public static double Fbm(ulong seed, double x, double y, int octaves)
    {
        double sum = 0, amplitude = 1, norm = 0, frequency = 1;
        for (int i = 0; i < octaves; i++)
        {
            sum += Value(Hashing.Mix(seed, (ulong)i), x * frequency, y * frequency) * amplitude;
            norm += amplitude;
            amplitude *= 0.5;
            frequency *= 2;
        }
        return sum / norm;
    }

    private static double Smooth(double t) => t * t * (3 - 2 * t);
    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
}
