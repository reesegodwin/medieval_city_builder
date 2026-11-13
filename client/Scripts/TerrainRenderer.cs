using Godot;
using Sim.Core.Util;
using Sim.Core.World;

namespace MedievalClient;

/// <summary>
/// Streams terrain chunks around the camera: one flat-shaded, triangulated heightfield per chunk (see
/// <see cref="TerrainShape"/>) plus MultiMeshes for trees and rocks. Terrain comes from the simulation's seeded generator, so the world keeps going
/// in every direction.
/// </summary>
public partial class TerrainRenderer : Node3D
{
    /// <summary>Chunks within this many chunks of the camera focus are shown.</summary>
    [Export] public int ViewRadiusChunks { get; set; } = 2;

    private const int MaxChunkBuildsPerFrame = 1;

    private readonly Dictionary<(int, int), Node3D> _chunks = new();
    private WorldMap _map = null!;
    private ulong _seed;

    public Func<Vector3>? FocusProvider { get; set; }

    public void Initialize(WorldMap map)
    {
        _map = map;
        _seed = map.Seed;
    }

    public const float WaterLevel = TerrainShape.WaterLevel;

    public override void _Process(double delta)
    {
        if (FocusProvider == null) return;
        var focus = FocusProvider();
        int fcx = Mathf.FloorToInt(focus.X / Chunk.Size);
        int fcy = Mathf.FloorToInt(focus.Z / Chunk.Size);

        // Build the nearest missing chunk(s) first.
        int built = 0;
        for (int r = 0; r <= ViewRadiusChunks && built < MaxChunkBuildsPerFrame; r++)
        for (int cy = fcy - r; cy <= fcy + r && built < MaxChunkBuildsPerFrame; cy++)
        for (int cx = fcx - r; cx <= fcx + r && built < MaxChunkBuildsPerFrame; cx++)
        {
            if (Math.Max(Math.Abs(cx - fcx), Math.Abs(cy - fcy)) != r) continue;
            if (_chunks.ContainsKey((cx, cy))) continue;
            var node = BuildChunk(cx, cy);
            AddChild(node);
            _chunks[(cx, cy)] = node;
            built++;
        }

        // Drop chunks well outside the view.
        foreach (var key in _chunks.Keys.ToList())
        {
            if (Math.Max(Math.Abs(key.Item1 - fcx), Math.Abs(key.Item2 - fcy)) > ViewRadiusChunks + 1)
            {
                _chunks[key].QueueFree();
                _chunks.Remove(key);
            }
        }
    }

    private Node3D BuildChunk(int cx, int cy)
    {
        var root = new Node3D { Name = $"Chunk_{cx}_{cy}" };
        var ground = new MeshBuilder();
        var water = new MeshBuilder();
        var treeTransforms = new List<Transform3D>();
        var rockTransforms = new List<(Transform3D, Color)>();

        int x0 = cx * Chunk.Size, y0 = cy * Chunk.Size;
        const int n = Chunk.Size + 1;

        // Heights and colours at tile corners; neighbouring chunks compute the same values on shared edges.
        var heights = new float[n, n];
        var colors = new Color[n, n];
        for (int j = 0; j < n; j++)
        for (int i = 0; i < n; i++)
        {
            heights[i, j] = TerrainShape.CornerHeight(_map, x0 + i, y0 + j);
            colors[i, j] = TerrainShape.CornerColor(_map, x0 + i, y0 + j, heights[i, j]);
        }

        for (int ly = 0; ly < Chunk.Size; ly++)
        for (int lx = 0; lx < Chunk.Size; lx++)
        {
            int x = x0 + lx, y = y0 + ly;
            var terrain = _map.GetTile(x, y).Terrain;
            var a = new Vector3(x, heights[lx, ly], y);
            var b = new Vector3(x + 1, heights[lx + 1, ly], y);
            var c = new Vector3(x + 1, heights[lx + 1, ly + 1], y + 1);
            var d = new Vector3(x, heights[lx, ly + 1], y + 1);
            Color ca = colors[lx, ly], cb = colors[lx + 1, ly], cc = colors[lx + 1, ly + 1], cd = colors[lx, ly + 1];

            // Split along the diagonal whose ends are closest in height, so ridges and shores follow the land.
            float jitter = (float)(Hashing.Hash01(_seed ^ 0x51, x, y) - 0.5) * 0.04f;
            if (Mathf.Abs(a.Y - c.Y) <= Mathf.Abs(b.Y - d.Y))
            {
                Facet(ground, a, b, c, Average(ca, cb, cc).Lightened(jitter));
                Facet(ground, a, c, d, Average(ca, cc, cd).Darkened(jitter));
            }
            else
            {
                Facet(ground, a, b, d, Average(ca, cb, cd).Lightened(jitter));
                Facet(ground, b, c, d, Average(cb, cc, cd).Darkened(jitter));
            }

            // Water covers every tile whose ground dips below the surface, including the shore of land tiles.
            if (Mathf.Min(Mathf.Min(a.Y, b.Y), Mathf.Min(c.Y, d.Y)) < WaterLevel)
                water.Quad(new(x, WaterLevel, y), new(x + 1, WaterLevel, y), new(x + 1, WaterLevel, y + 1), new(x, WaterLevel, y + 1), Colors.White, Vector3.Up);

            double r = Hashing.Hash01(_seed ^ 0x7EE, x, y);
            if (terrain == Terrain.Forest && r < 0.85)
            {
                treeTransforms.Add(Scatter(x, y, 0.8f + (float)Hashing.Hash01(_seed ^ 0x5CA, x, y) * 0.6f));
            }
            else if ((terrain == Terrain.Rock || terrain == Terrain.IronDeposit) && r < 0.22)
            {
                var tint = terrain == Terrain.IronDeposit ? new Color(1.0f, 0.75f, 0.6f) : Colors.White;
                rockTransforms.Add((Scatter(x, y, 0.7f + (float)r * 2f), tint));
            }
        }

        root.AddChild(new MeshInstance3D { Mesh = ground.Commit(SeasonLook.Default) });
        if (!water.IsEmpty)
            root.AddChild(new MeshInstance3D { Mesh = water.Commit(Materials.Water), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });

        if (treeTransforms.Count > 0)
        {
            var mm = NewMultiMesh(LowPolyMeshes.Tree, treeTransforms.Count, colors: true);
            for (int i = 0; i < treeTransforms.Count; i++)
            {
                mm.SetInstanceTransform(i, treeTransforms[i]);
                var origin = treeTransforms[i].Origin;
                float v = (float)Hashing.Hash01(_seed ^ 0x7C0, (int)origin.X, (int)origin.Z);
                mm.SetInstanceColor(i, new Color(0.85f + v * 0.2f, 0.9f + v * 0.15f, 0.8f + (1 - v) * 0.2f));
            }
            root.AddChild(new MultiMeshInstance3D { Multimesh = mm });
        }
        if (rockTransforms.Count > 0)
        {
            var mm = NewMultiMesh(LowPolyMeshes.Rock, rockTransforms.Count, colors: true);
            for (int i = 0; i < rockTransforms.Count; i++)
            {
                mm.SetInstanceTransform(i, rockTransforms[i].Item1);
                mm.SetInstanceColor(i, rockTransforms[i].Item2);
            }
            root.AddChild(new MultiMeshInstance3D { Multimesh = mm });
        }
        return root;
    }

    /// <summary>One flat-shaded ground triangle (normal facing up).</summary>
    private static void Facet(MeshBuilder mb, Vector3 a, Vector3 b, Vector3 c, Color color)
    {
        var normal = (b - a).Cross(c - a).Normalized();
        if (normal.Y < 0) normal = -normal;
        mb.Tri(a, b, c, color, normal);
    }

    private static Color Average(Color a, Color b, Color c) =>
        new((a.R + b.R + c.R) / 3f, (a.G + b.G + c.G) / 3f, (a.B + b.B + c.B) / 3f);

    private Transform3D Scatter(int x, int y, float scale)
    {
        float jx = (float)Hashing.Hash01(_seed ^ 0xA1, x, y) * 0.5f + 0.25f;
        float jz = (float)Hashing.Hash01(_seed ^ 0xB2, x, y) * 0.5f + 0.25f;
        float rot = (float)Hashing.Hash01(_seed ^ 0xC3, x, y) * Mathf.Tau;
        var basis = new Basis(Vector3.Up, rot).Scaled(Vector3.One * scale);
        float h = TerrainShape.HeightAt(_map, x + jx, y + jz) - 0.03f; // sink slightly so slopes don't show a gap
        return new Transform3D(basis, new Vector3(x + jx, h, y + jz));
    }

    public static MultiMesh NewMultiMesh(Mesh mesh, int count, bool colors = false) => new()
    {
        TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
        UseColors = colors,
        Mesh = mesh,
        InstanceCount = count,
    };
}

/// <summary>
/// The rendered shape of the land. Heights live on tile corners and blend the tiles around them, so river
/// banks slope into the water and rock rises into hills instead of stepping like blocks. Buildable ground
/// stays nearly flat so buildings and roads sit on it. Purely cosmetic: the simulation still uses tiles.
/// </summary>
public static class TerrainShape
{
    public const float WaterLevel = -0.1f;

    private static readonly Color Sand = new(0.74f, 0.69f, 0.50f);

    private static float BaseHeight(Terrain t) => t switch
    {
        Terrain.Water => -0.34f,
        Terrain.Rock or Terrain.IronDeposit => 0.3f,
        Terrain.Forest => 0.04f,
        _ => 0f,
    };

    public static Color ColorOf(Terrain t) => t switch
    {
        Terrain.Grass => new Color(0.50f, 0.64f, 0.29f),
        Terrain.Fertile => new Color(0.38f, 0.56f, 0.22f),
        Terrain.Forest => new Color(0.27f, 0.43f, 0.18f),
        Terrain.Rock => new Color(0.56f, 0.54f, 0.50f),
        Terrain.IronDeposit => new Color(0.56f, 0.40f, 0.31f),
        Terrain.Water => new Color(0.30f, 0.36f, 0.32f), // river bed under the water surface
        _ => Colors.Magenta,
    };

    private static bool IsBuildableGround(Terrain t) => t is Terrain.Grass or Terrain.Fertile;

    /// <summary>Height of corner (vx, vy), shared by tiles (vx-1, vy-1) to (vx, vy).</summary>
    public static float CornerHeight(WorldMap map, int vx, int vy)
    {
        float sum = 0;
        int water = 0, rock = 0, buildable = 0;
        for (int dy = -1; dy <= 0; dy++)
        for (int dx = -1; dx <= 0; dx++)
        {
            var t = map.GetTile(vx + dx, vy + dy).Terrain;
            sum += BaseHeight(t);
            if (t == Terrain.Water) water++;
            else if (t is Terrain.Rock or Terrain.IronDeposit) rock++;
            else if (IsBuildableGround(t)) buildable++;
        }
        float h = sum / 4f;

        if (rock > 0 || water == 4)
        {
            // Wider look: deep in rock the land rises into hills; deep in water the bed sinks.
            int rocks = 0, waters = 0;
            for (int dy = -2; dy <= 1; dy++)
            for (int dx = -2; dx <= 1; dx++)
            {
                var t = map.GetTile(vx + dx, vy + dy).Terrain;
                if (t is Terrain.Rock or Terrain.IronDeposit) rocks++;
                else if (t == Terrain.Water) waters++;
            }
            if (rock > 0) h += 1.1f * (rocks / 16f) * (rocks / 16f);
            if (water == 4) h -= 0.12f * waters / 16f;
        }

        // Gentle irregularity everywhere except on flat building land.
        if (buildable < 4) h += (float)(Hashing.Hash01(map.Seed ^ 0xC0FFEE, vx, vy) - 0.5) * (rock > 0 ? 0.16f : 0.05f);

        // Keep building land close to flat so buildings and roads sit on it.
        if (buildable > 0) h = Mathf.Clamp(h, -0.16f, 0.08f);
        return h;
    }

    /// <summary>Blended colour at a corner, with a sandy strip where land meets water.</summary>
    public static Color CornerColor(WorldMap map, int vx, int vy, float height)
    {
        float r = 0, g = 0, b = 0;
        bool nearWater = false;
        for (int dy = -1; dy <= 0; dy++)
        for (int dx = -1; dx <= 0; dx++)
        {
            var t = map.GetTile(vx + dx, vy + dy).Terrain;
            var c = ColorOf(t);
            r += c.R;
            g += c.G;
            b += c.B;
            nearWater |= t == Terrain.Water;
        }
        var color = new Color(r / 4f, g / 4f, b / 4f);
        if (nearWater && height > WaterLevel - 0.08f && height < WaterLevel + 0.1f) color = color.Lerp(Sand, 0.65f);
        if (height > 0.35f) color = color.Lightened(Mathf.Min(0.25f, (height - 0.35f) * 0.4f)); // pale high rock
        return color;
    }

    /// <summary>Ground height at any point (bilinear between the surrounding corners).</summary>
    public static float HeightAt(WorldMap map, float x, float z)
    {
        int ix = Mathf.FloorToInt(x), iz = Mathf.FloorToInt(z);
        float fx = x - ix, fz = z - iz;
        float h00 = CornerHeight(map, ix, iz), h10 = CornerHeight(map, ix + 1, iz);
        float h01 = CornerHeight(map, ix, iz + 1), h11 = CornerHeight(map, ix + 1, iz + 1);
        return Mathf.Lerp(Mathf.Lerp(h00, h10, fx), Mathf.Lerp(h01, h11, fx), fz);
    }
}
