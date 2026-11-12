using Godot;
using Sim.Core.Settlements;
using Sim.Core.World;

namespace MedievalClient;

/// <summary>
/// Draws the settlement's territory border as a coloured line along the inside edge of claimed land. It widens
/// as the camera zooms out so it stays readable, and is rebuilt only when the territory or the (stepped) width
/// changes. While the territory tool is open the claimed land is also tinted.
/// </summary>
public partial class TerritoryRenderer : Node3D
{
    private static readonly Color Border = new(0.95f, 0.78f, 0.25f);
    private static readonly Color Fill = new(0.95f, 0.78f, 0.25f, 0.16f);
    private const float MinWidth = 0.12f;
    private const float MaxWidth = 0.7f;
    private const float WidthPerDistance = 0.0075f;
    private const float WidthStep = 0.04f;
    private const float Y = 0.03f;
    private static readonly (int Dx, int Dz)[] Diagonals = { (-1, -1), (1, -1), (1, 1), (-1, 1) };

    /// <summary>Unshaded and unaffected by fog, so the border stays readable from far away.</summary>
    private static readonly StandardMaterial3D BorderMaterial = new()
    {
        VertexColorUseAsAlbedo = true,
        VertexColorIsSrgb = true,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        DisableFog = true,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };

    private Settlement _settlement = null!;
    private WorldMap _map = null!;
    private MeshInstance3D _border = null!;
    private MeshInstance3D _fill = null!;
    private int _builtVersion = -1;
    private float _builtWidth = -1;
    private Func<float> _cameraDistance = () => 28f;
    private bool _fillBuilt;

    /// <summary>Tint all claimed land (used while painting territory).</summary>
    public bool ShowFill { get; set; }

    /// <param name="cameraDistance">Current zoom; the border widens as the camera pulls back.</param>
    public void Initialize(WorldMap map, Settlement settlement, Func<float> cameraDistance)
    {
        _cameraDistance = cameraDistance;
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
        // Width in tiles grows with zoom, snapped to steps so zooming only triggers a few rebuilds.
        float width = Mathf.Clamp(_cameraDistance() * WidthPerDistance, MinWidth, MaxWidth);
        width = Mathf.Max(MinWidth, Mathf.Round(width / WidthStep) * WidthStep);
        if (_settlement.TerritoryVersion != _builtVersion)
        {
            _builtVersion = _settlement.TerritoryVersion;
            _fillBuilt = false;
            _builtWidth = -1;
        }
        if (!Mathf.IsEqualApprox(width, _builtWidth))
        {
            _builtWidth = width;
            BuildBorder(width);
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

    private void BuildBorder(float width)
    {
        // Strips extend inward, so a wide border never covers land outside the territory.
        float y = Y + width * 0.02f;
        var mb = new MeshBuilder();
        foreach (var (x, z) in _settlement.Territory)
        {
            if (!Owns(x, z - 1)) Strip(mb, x, z, x + 1, z, 0, 1, width, y);
            if (!Owns(x, z + 1)) Strip(mb, x, z + 1, x + 1, z + 1, 0, -1, width, y);
            if (!Owns(x - 1, z)) Strip(mb, x, z, x, z + 1, 1, 0, width, y);
            if (!Owns(x + 1, z)) Strip(mb, x + 1, z, x + 1, z + 1, -1, 0, width, y);

            // Inside corners of the staircase: both side neighbours are ours but the diagonal isn't. Without a
            // corner square, wide strips leave a notch at every step and diagonal borders look dotted.
            foreach (var (dx, dz) in Diagonals)
            {
                if (Owns(x + dx, z + dz) || !Owns(x + dx, z) || !Owns(x, z + dz)) continue;
                float cx = dx > 0 ? x + 1 - width : x, cz = dz > 0 ? z + 1 - width : z;
                mb.Quad(new Vector3(cx, y, cz), new Vector3(cx + width, y, cz),
                    new Vector3(cx + width, y, cz + width), new Vector3(cx, y, cz + width), Border, Vector3.Up);
            }
        }
        _border.Mesh = mb.Commit(BorderMaterial);
    }

    /// <summary>A flat strip along a tile edge, extending inward by <paramref name="width"/>.</summary>
    private static void Strip(MeshBuilder mb, float x0, float z0, float x1, float z1, int inX, int inZ, float width, float y)
    {
        var a = new Vector3(x0, y, z0);
        var b = new Vector3(x1, y, z1);
        var offset = new Vector3(inX * width, 0, inZ * width);
        mb.Quad(a, b, b + offset, a + offset, Border, Vector3.Up);
    }
}
