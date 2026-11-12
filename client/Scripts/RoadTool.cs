using Godot;
using Sim.Core.Commands;

namespace MedievalClient;

/// <summary>
/// Click a start tile, move the mouse, click again: lays an L-shaped road (horizontal, then vertical).
/// The preview colours each tile green or red using the same validation the server will apply.
/// </summary>
public partial class RoadTool : Node3D
{
    private const int MaxTiles = 256;
    private static readonly Color Valid = new(0.35f, 0.95f, 0.4f, 0.55f);
    private static readonly Color Invalid = new(0.95f, 0.25f, 0.2f, 0.55f);
    private static readonly Color Existing = new(0.9f, 0.85f, 0.6f, 0.35f);

    private SimSession _session = null!;
    private CameraRig _camera = null!;
    private MultiMesh _preview = null!;
    private (int X, int Y)? _start;
    private List<(int X, int Y)> _tiles = new();

    public bool Active { get; private set; }
    public string? Problem { get; private set; }

    public void Initialize(SimSession session, CameraRig camera)
    {
        _session = session;
        _camera = camera;
        var tileMesh = new BoxMesh { Size = new Vector3(0.9f, 0.05f, 0.9f), Material = Materials.Overlay };
        _preview = TerrainRenderer.NewMultiMesh(tileMesh, MaxTiles, colors: true);
        _preview.VisibleInstanceCount = 0;
        AddChild(new MultiMeshInstance3D { Multimesh = _preview, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
    }

    public void Begin()
    {
        Active = true;
        _start = null;
    }

    public void Cancel()
    {
        Active = false;
        _start = null;
        Problem = null;
        _tiles.Clear();
        _preview.VisibleInstanceCount = 0;
    }

    public string Status
    {
        get
        {
            if (Problem != null) return $"Road: {Problem}";
            if (_start == null) return "Road: click where the road starts (right-click to stop). Roads over water become bridges.";
            int wood = Roads.BridgeCost(_session.World, _tiles);
            string bridge = wood > 0 ? $", bridge costs {wood} wood (you have {_session.PlayerTown.Stock.Get("wood")})" : "";
            return $"Road: click where it ends ({_tiles.Count} tiles{bridge})";
        }
    }

    public override void _Process(double delta)
    {
        if (!Active) return;
        if (_camera.MouseGroundPoint() is not Vector3 point)
        {
            _preview.VisibleInstanceCount = 0;
            return;
        }

        var hover = Coords.WorldToTile(point);
        _tiles = _start is { } start ? LPath(start, hover) : new List<(int, int)> { hover };

        Problem = null;
        var world = _session.World;
        int n = Math.Min(_tiles.Count, MaxTiles);
        for (int i = 0; i < n; i++)
        {
            var (x, y) = _tiles[i];
            bool isRoad = world.Map.GetTile(x, y).IsRoad;
            string? error = isRoad ? null : Roads.Check(world, _session.PlayerTown, x, y);
            Problem ??= error;
            _preview.SetInstanceTransform(i, new Transform3D(Basis.Identity, Coords.TileCenter(x, y) + new Vector3(0, 0.05f, 0)));
            _preview.SetInstanceColor(i, isRoad ? Existing : error == null ? Valid : Invalid);
        }
        _preview.VisibleInstanceCount = n;
        if (_tiles.Count > MaxTiles) Problem = "Road too long";
    }

    /// <summary>Handles a left click. Returns the command result once a road is submitted.</summary>
    public CommandResult? Click()
    {
        if (!Active || _tiles.Count == 0) return null;
        if (_start == null)
        {
            _start = _tiles[0];
            return null;
        }
        if (_tiles.Count > MaxTiles) return CommandResult.Fail("Road too long");

        var result = _session.Execute(new PlaceRoadCommand(_session.PlayerTown.Id, _tiles.ToList()));
        if (result.Ok) _start = null; // ready for the next road
        return result;
    }

    private static List<(int X, int Y)> LPath((int X, int Y) a, (int X, int Y) b)
    {
        var tiles = new List<(int, int)>();
        int sx = Math.Sign(b.X - a.X), sy = Math.Sign(b.Y - a.Y);
        for (int x = a.X; ; x += sx)
        {
            tiles.Add((x, a.Y));
            if (x == b.X) break;
        }
        for (int y = a.Y + sy; sy != 0; y += sy)
        {
            tiles.Add((b.X, y));
            if (y == b.Y) break;
        }
        return tiles;
    }
}
