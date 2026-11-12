using Godot;
using Sim.Core.Util;
using Sim.Core.World;

namespace MedievalClient;

/// <summary>
/// Streams terrain chunks around the camera: one flat-shaded mesh per chunk plus MultiMeshes for
/// trees and rocks. Terrain comes from the simulation's seeded generator, so the world keeps going
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

    public static float HeightOf(Terrain t) => t switch
    {
        Terrain.Water => -0.25f,
        Terrain.Rock => 0.45f,
        Terrain.IronDeposit => 0.45f,
        _ => 0f,
    };

    public static Color ColorOf(Terrain t) => t switch
    {
        Terrain.Grass => new Color(0.50f, 0.64f, 0.29f),
        Terrain.Fertile => new Color(0.38f, 0.56f, 0.22f),
        Terrain.Forest => new Color(0.27f, 0.43f, 0.18f),
        Terrain.Rock => new Color(0.56f, 0.54f, 0.50f),
        Terrain.IronDeposit => new Color(0.56f, 0.40f, 0.31f),
        Terrain.Water => new Color(0.22f, 0.42f, 0.62f),
        _ => Colors.Magenta,
    };

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
        var treeTransforms = new List<Transform3D>();
        var rockTransforms = new List<(Transform3D, Color)>();

        int x0 = cx * Chunk.Size, y0 = cy * Chunk.Size;
        for (int ly = 0; ly < Chunk.Size; ly++)
        for (int lx = 0; lx < Chunk.Size; lx++)
        {
            int x = x0 + lx, y = y0 + ly;
            var terrain = _map.GetTile(x, y).Terrain;
            float h = HeightOf(terrain);
            float jitter = (float)(Hashing.Hash01(_seed ^ 0x51, x, y) - 0.5) * 0.06f;
            var color = ColorOf(terrain).Lightened(jitter);

            ground.Quad(new(x, h, y), new(x + 1, h, y), new(x + 1, h, y + 1), new(x, h, y + 1), color, Vector3.Up);

            // Cliff walls down to lower neighbours.
            AddWall(ground, x, y, h, x + 1, y, color, Vector3.Right);
            AddWall(ground, x, y, h, x - 1, y, color, Vector3.Left);
            AddWall(ground, x, y, h, x, y + 1, color, Vector3.Back);
            AddWall(ground, x, y, h, x, y - 1, color, Vector3.Forward);

            double r = Hashing.Hash01(_seed ^ 0x7EE, x, y);
            if (terrain == Terrain.Forest && r < 0.85)
            {
                treeTransforms.Add(Scatter(x, y, h, 0.8f + (float)Hashing.Hash01(_seed ^ 0x5CA, x, y) * 0.6f));
            }
            else if ((terrain == Terrain.Rock || terrain == Terrain.IronDeposit) && r < 0.22)
            {
                var tint = terrain == Terrain.IronDeposit ? new Color(1.0f, 0.75f, 0.6f) : Colors.White;
                rockTransforms.Add((Scatter(x, y, h, 0.7f + (float)r * 2f), tint));
            }
        }

        root.AddChild(new MeshInstance3D { Mesh = ground.Commit() });

        if (treeTransforms.Count > 0)
        {
            var mm = NewMultiMesh(LowPolyMeshes.Tree, treeTransforms.Count);
            for (int i = 0; i < treeTransforms.Count; i++) mm.SetInstanceTransform(i, treeTransforms[i]);
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

    private void AddWall(MeshBuilder mb, int x, int y, float h, int nx, int ny, Color color, Vector3 normal)
    {
        float nh = HeightOf(_map.GetTile(nx, ny).Terrain);
        if (nh >= h) return;
        var c = color.Darkened(0.25f);
        // The shared edge between tile (x,y) and its neighbour.
        if (nx != x)
        {
            float ex = nx > x ? x + 1 : x;
            mb.Quad(new(ex, nh, y), new(ex, nh, y + 1), new(ex, h, y + 1), new(ex, h, y), c, normal);
        }
        else
        {
            float ez = ny > y ? y + 1 : y;
            mb.Quad(new(x, nh, ez), new(x + 1, nh, ez), new(x + 1, h, ez), new(x, h, ez), c, normal);
        }
    }

    private Transform3D Scatter(int x, int y, float h, float scale)
    {
        float jx = (float)Hashing.Hash01(_seed ^ 0xA1, x, y) * 0.5f + 0.25f;
        float jz = (float)Hashing.Hash01(_seed ^ 0xB2, x, y) * 0.5f + 0.25f;
        float rot = (float)Hashing.Hash01(_seed ^ 0xC3, x, y) * Mathf.Tau;
        var basis = new Basis(Vector3.Up, rot).Scaled(Vector3.One * scale);
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
