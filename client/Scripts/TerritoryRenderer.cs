using Godot;
using Sim.Core.Settlements;
using Sim.Core.World;

namespace MedievalClient;

/// <summary>
/// Draws the settlement's territory border as a smoothed coloured line (marching squares, so steps become
/// diagonals) draped over the ground. It widens
/// as the camera zooms out so it stays readable, and is rebuilt only when the territory or the (stepped) width
/// changes. While the territory tool is open the claimed land is also tinted.
/// </summary>
public partial class TerritoryRenderer : Node3D
{
    private static readonly Color OwnBorder = new(0.95f, 0.78f, 0.25f);

    /// <summary>Line colour: gold for the player's own land, a distinct colour for each other settlement.</summary>
    public Color BorderColor { get; set; } = OwnBorder;

    private static readonly Color[] OtherColors =
    {
        new(0.85f, 0.25f, 0.25f), new(0.3f, 0.5f, 0.95f), new(0.65f, 0.35f, 0.85f),
        new(0.25f, 0.8f, 0.8f), new(0.95f, 0.5f, 0.2f), new(0.9f, 0.9f, 0.9f),
    };

    public static Color OtherPlayerColor(int settlementId) => OtherColors[settlementId % OtherColors.Length];
    private static readonly Color Fill = new(0.95f, 0.78f, 0.25f, 0.16f);
    private const float MinWidth = 0.12f;
    private const float MaxWidth = 0.7f;
    private const float WidthPerDistance = 0.0075f;
    private const float WidthStep = 0.04f;
    private const float Y = 0.03f;

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
            BuildFill();
        }
        _fill.Visible = ShowFill;
    }

    // ---- Marching squares over tile centres ----
    // A "cell" sits between four tile centres; its top-left tile is (cx, cy). Walking the cell's outline
    // (corner, edge midpoint, corner, ...) and keeping owned corners plus midpoints where ownership changes
    // gives the owned part of the cell. Two midpoints next to each other on that outline are a piece of the
    // border. Staircases become 45-degree lines and straight edges stay on the tile boundaries.

    private static readonly (int Dx, int Dz)[] CellCorners = { (0, 0), (1, 0), (1, 1), (0, 1) };

    /// <summary>Cells touching at least one owned tile.</summary>
    private HashSet<(int, int)> TouchedCells()
    {
        var cells = new HashSet<(int, int)>();
        foreach (var (x, z) in _settlement.Territory)
            for (int dz = -1; dz <= 0; dz++)
            for (int dx = -1; dx <= 0; dx++)
                cells.Add((x + dx, z + dz));
        return cells;
    }

    /// <summary>The owned polygon of a cell, as 2D points with a flag marking edge midpoints.</summary>
    private List<(Vector2 P, bool Mid)> CellPolygon(int cx, int cz)
    {
        var owned = new bool[4];
        for (int k = 0; k < 4; k++) owned[k] = Owns(cx + CellCorners[k].Dx, cz + CellCorners[k].Dz);
        var poly = new List<(Vector2, bool)>(8);
        for (int k = 0; k < 4; k++)
        {
            var (ax, az) = CellCorners[k];
            var (bx, bz) = CellCorners[(k + 1) % 4];
            var a = new Vector2(cx + ax + 0.5f, cz + az + 0.5f);
            var b = new Vector2(cx + bx + 0.5f, cz + bz + 0.5f);
            if (owned[k]) poly.Add((a, false));
            if (owned[k] != owned[(k + 1) % 4]) poly.Add(((a + b) / 2, true));
        }
        return poly;
    }

    /// <summary>Ground height with a per-build cache (corner heights are reused many times).</summary>
    private Func<Vector2, float, Vector3> Drape()
    {
        var cache = new Dictionary<(int, int), float>();
        float Corner(int x, int z)
        {
            if (!cache.TryGetValue((x, z), out float h)) cache[(x, z)] = h = TerrainShape.CornerHeight(_map, x, z);
            return h;
        }
        return (p, lift) =>
        {
            int ix = Mathf.FloorToInt(p.X), iz = Mathf.FloorToInt(p.Y);
            float fx = p.X - ix, fz = p.Y - iz;
            float h = Mathf.Lerp(Mathf.Lerp(Corner(ix, iz), Corner(ix + 1, iz), fx),
                Mathf.Lerp(Corner(ix, iz + 1), Corner(ix + 1, iz + 1), fx), fz);
            return new Vector3(p.X, Mathf.Max(h, TerrainShape.WaterLevel) + lift, p.Y); // ride on water, not the riverbed
        };
    }

    private void BuildBorder(float width)
    {
        float lift = Y + width * 0.08f;
        var at = Drape();
        var mb = new MeshBuilder();
        foreach (var (cx, cz) in TouchedCells())
        {
            var poly = CellPolygon(cx, cz);
            for (int i = 0; i < poly.Count; i++)
            {
                var (a, aMid) = poly[i];
                var (b, bMid) = poly[(i + 1) % poly.Count];
                if (aMid && bMid) Segment(mb, a, b, width, lift, at);
            }
        }
        _border.Mesh = mb.Commit(BorderMaterial);
    }

    /// <summary>A flat band centred on a border segment, extended by half its width so joints overlap cleanly.</summary>
    private void Segment(MeshBuilder mb, Vector2 a, Vector2 b, float width, float lift, Func<Vector2, float, Vector3> at)
    {
        var dir = (b - a).Normalized();
        var side = new Vector2(-dir.Y, dir.X) * (width / 2);
        a -= dir * (width / 2);
        b += dir * (width / 2);
        mb.Quad(at(a + side, lift), at(b + side, lift), at(b - side, lift), at(a - side, lift), BorderColor, Vector3.Up);
    }

    private void BuildFill()
    {
        var at = Drape();
        var mb = new MeshBuilder();
        foreach (var (cx, cz) in TouchedCells())
        {
            var poly = CellPolygon(cx, cz);
            if (poly.Count < 3) continue;
            var first = at(poly[0].P, Y);
            for (int i = 1; i + 1 < poly.Count; i++)
                mb.Tri(first, at(poly[i].P, Y), at(poly[i + 1].P, Y), Fill, Vector3.Up);
        }
        _fill.Mesh = mb.Commit(Materials.Overlay);
    }
}
