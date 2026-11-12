using Godot;
using Sim.Core.Settlements;
using Sim.Core.Util;
using Sim.Core.World;

namespace MedievalClient;

/// <summary>Draws the settlement's roads as one dirt overlay mesh, rebuilt only when roads change.</summary>
public partial class RoadRenderer : Node3D
{
    private static readonly Color Dirt = new(0.58f, 0.47f, 0.33f);

    private Settlement _settlement = null!;
    private WorldMap _map = null!;
    private MeshInstance3D _mesh = null!;
    private int _builtVersion = -1;

    public void Initialize(WorldMap map, Settlement settlement)
    {
        _map = map;
        _settlement = settlement;
        _mesh = new MeshInstance3D { CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(_mesh);
    }

    public override void _Process(double delta)
    {
        if (_settlement == null || _settlement.RoadVersion == _builtVersion) return;
        _builtVersion = _settlement.RoadVersion;

        var mb = new MeshBuilder();
        const float y = 0.015f, inset = 0.04f;
        foreach (var (x, z) in _settlement.Roads)
        {
            if (_map.GetTile(x, z).Terrain == Terrain.Water)
            {
                AddBridge(mb, x, z);
                continue;
            }
            float jitter = (float)(Hashing.Hash01(0x40AD, x, z) - 0.5) * 0.08f;
            var color = Dirt.Lightened(jitter);
            mb.Quad(new Vector3(x + inset, y, z + inset), new Vector3(x + 1 - inset, y, z + inset),
                new Vector3(x + 1 - inset, y, z + 1 - inset), new Vector3(x + inset, y, z + 1 - inset), color, Vector3.Up);

            // Fill the gaps towards road neighbours so roads read as continuous strips.
            if (_settlement.Roads.Contains((x + 1, z)))
                mb.Quad(new Vector3(x + 1 - inset, y, z + inset), new Vector3(x + 1 + inset, y, z + inset),
                    new Vector3(x + 1 + inset, y, z + 1 - inset), new Vector3(x + 1 - inset, y, z + 1 - inset), color, Vector3.Up);
            if (_settlement.Roads.Contains((x, z + 1)))
                mb.Quad(new Vector3(x + inset, y, z + 1 - inset), new Vector3(x + 1 - inset, y, z + 1 - inset),
                    new Vector3(x + 1 - inset, y, z + 1 + inset), new Vector3(x + inset, y, z + 1 + inset), color, Vector3.Up);
        }
        _mesh.Mesh = mb.Commit();
    }

    /// <summary>A plank deck with posts and rails on the sides that face open water.</summary>
    private void AddBridge(MeshBuilder mb, int x, int z)
    {
        var plank = LowPolyMeshes.Wood.Lightened(0.05f);
        mb.Center = new Vector3(x + 0.5f, 0, z + 0.5f);
        mb.Box(new Vector3(x - 0.02f, -0.06f, z - 0.02f), new Vector3(x + 1.02f, 0.03f, z + 1.02f), plank);
        for (float p = 0.12f; p < 1f; p += 0.25f) // plank seams
            mb.Box(new Vector3(x + p, 0.03f, z), new Vector3(x + p + 0.02f, 0.035f, z + 1), plank.Darkened(0.25f));

        bool BankOrRoad(int tx, int tz) => _settlement.Roads.Contains((tx, tz)) || _map.GetTile(tx, tz).Terrain != Terrain.Water;
        var rail = LowPolyMeshes.Timber;
        if (!BankOrRoad(x, z - 1)) mb.Box(new Vector3(x, 0.03f, z), new Vector3(x + 1, 0.25f, z + 0.06f), rail);
        if (!BankOrRoad(x, z + 1)) mb.Box(new Vector3(x, 0.03f, z + 0.94f), new Vector3(x + 1, 0.25f, z + 1), rail);
        if (!BankOrRoad(x - 1, z)) mb.Box(new Vector3(x, 0.03f, z), new Vector3(x + 0.06f, 0.25f, z + 1), rail);
        if (!BankOrRoad(x + 1, z)) mb.Box(new Vector3(x + 0.94f, 0.03f, z), new Vector3(x + 1, 0.25f, z + 1), rail);
        // Piles down into the water.
        mb.Box(new Vector3(x + 0.05f, -0.3f, z + 0.05f), new Vector3(x + 0.15f, -0.06f, z + 0.15f), rail);
        mb.Box(new Vector3(x + 0.85f, -0.3f, z + 0.85f), new Vector3(x + 0.95f, -0.06f, z + 0.95f), rail);
    }
}
