using Godot;
using Sim.Core.Settlements;
using Sim.Core.Util;

namespace MedievalClient;

/// <summary>Draws the settlement's roads as one dirt overlay mesh, rebuilt only when roads change.</summary>
public partial class RoadRenderer : Node3D
{
    private static readonly Color Dirt = new(0.58f, 0.47f, 0.33f);

    private Settlement _settlement = null!;
    private MeshInstance3D _mesh = null!;
    private int _builtVersion = -1;

    public void Initialize(Settlement settlement)
    {
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
}
