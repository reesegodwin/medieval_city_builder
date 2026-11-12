using Godot;
using Sim.Core.Settlements;
using Sim.Core.World;

namespace MedievalClient;

/// <summary>
/// Draws the settlement's territory border as a thin coloured line along the edges of claimed land, rebuilt
/// only when the territory changes. While the territory tool is open the claimed land is also tinted.
/// </summary>
public partial class TerritoryRenderer : Node3D
{
    private static readonly Color Border = new(0.95f, 0.78f, 0.25f);
    private static readonly Color Fill = new(0.95f, 0.78f, 0.25f, 0.16f);
    private const float Width = 0.12f;
    private const float Y = 0.03f;

    private Settlement _settlement = null!;
    private WorldMap _map = null!;
    private MeshInstance3D _border = null!;
    private MeshInstance3D _fill = null!;
    private int _builtVersion = -1;
    private bool _fillBuilt;

    /// <summary>Tint all claimed land (used while painting territory).</summary>
    public bool ShowFill { get; set; }

    public void Initialize(WorldMap map, Settlement settlement)
    {
        _map = map;
        _settlement = settlement;
        _border = new MeshInstance3D { CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        _fill = new MeshInstance3D { CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Visible = false };
        AddChild(_border);
        AddChild(_fill);
    }

    private bool Owns(int x, int y) => _map.GetTile(x, y).TerritoryId == _settlement.Id;

    public override void _Process(double delta)
    {
        if (_settlement == null) return;
        if (_settlement.TerritoryVersion != _builtVersion)
        {
            _builtVersion = _settlement.TerritoryVersion;
            _fillBuilt = false;
            BuildBorder();
        }
        if (ShowFill && !_fillBuilt)
        {
            _fillBuilt = true;
            var mb = new MeshBuilder();
            foreach (var (x, z) in _settlement.Territory)
                mb.Quad(new Vector3(x, Y - 0.01f, z), new Vector3(x + 1, Y - 0.01f, z),
                    new Vector3(x + 1, Y - 0.01f, z + 1), new Vector3(x, Y - 0.01f, z + 1), Fill, Vector3.Up);
            _fill.Mesh = mb.Commit(Materials.Overlay);
        }
        _fill.Visible = ShowFill;
    }

    private void BuildBorder()
    {
        var mb = new MeshBuilder();
        foreach (var (x, z) in _settlement.Territory)
        {
            if (!Owns(x, z - 1)) Strip(mb, x, z, x + 1, z, 0, 1);
            if (!Owns(x, z + 1)) Strip(mb, x, z + 1, x + 1, z + 1, 0, -1);
            if (!Owns(x - 1, z)) Strip(mb, x, z, x, z + 1, 1, 0);
            if (!Owns(x + 1, z)) Strip(mb, x + 1, z, x + 1, z + 1, -1, 0);
        }
        _border.Mesh = mb.Commit();
    }

    /// <summary>A flat strip along a tile edge, extending inward by <see cref="Width"/>.</summary>
    private static void Strip(MeshBuilder mb, float x0, float z0, float x1, float z1, int inX, int inZ)
    {
        var a = new Vector3(x0, Y, z0);
        var b = new Vector3(x1, Y, z1);
        var offset = new Vector3(inX * Width, 0, inZ * Width);
        mb.Quad(a, b, b + offset, a + offset, Border, Vector3.Up);
    }
}
