using Godot;
using Sim.Core.Commands;

namespace MedievalClient;

/// <summary>
/// Walls: click a start tile, move the mouse, click again for an L-shaped wall of the chosen kind (or an upgrade
/// of the walls already there). In gate mode, click a wall to turn it into a gate. The preview uses the same checks
/// the server applies.
/// </summary>
public partial class WallTool : Node3D
{
    private const int MaxTiles = 256;
    private static readonly Color Valid = new(0.35f, 0.95f, 0.4f, 0.55f);
    private static readonly Color Invalid = new(0.95f, 0.25f, 0.2f, 0.55f);
    private static readonly Color Existing = new(0.9f, 0.85f, 0.6f, 0.35f);
    private static readonly Color GateColor = new(0.45f, 0.75f, 1f, 0.6f);

    private SimSession _session = null!;
    private CameraRig _camera = null!;
    private MultiMesh _preview = null!;
    private (int X, int Y)? _start;
    private List<(int X, int Y)> _tiles = new();

    public bool Active { get; private set; }

    /// <summary>Kind of wall (index into walls.json).</summary>
    public byte Tier { get; private set; }

    /// <summary>Gate mode: one click turns a wall tile into a gate.</summary>
    public bool GateMode { get; private set; }

    public string? Problem { get; private set; }

    public void Initialize(SimSession session, CameraRig camera)
    {
        _session = session;
        _camera = camera;
        var tileMesh = new BoxMesh { Size = new Vector3(0.9f, 0.5f, 0.9f), Material = Materials.Overlay };
        _preview = TerrainRenderer.NewMultiMesh(tileMesh, MaxTiles, colors: true);
        _preview.VisibleInstanceCount = 0;
        AddChild(new MultiMeshInstance3D { Multimesh = _preview, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
    }

    public void Begin(byte tier, bool gate = false)
    {
        Tier = tier;
        GateMode = gate;
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

    private static string Price(Dictionary<string, int> cost) =>
        cost.Count == 0 ? "nothing" : string.Join(", ", cost.Select(kv => $"{kv.Value} {kv.Key}"));

    public string Status
    {
        get
        {
            var world = _session.World;
            if (GateMode)
                return Problem != null ? $"Gate: {Problem}" :
                    "Gate: click a wall to put a gate in it (right-click to stop). People, carts and roads pass through gates; " +
                    $"a gate costs {Price(world.Data.WallTiers[0].GateCost)} in a palisade, more in stone.";
            var def = world.Data.WallTiers[Tier];
            if (Problem != null) return $"{def.Name}: {Problem}";
            if (_start == null)
                return $"{def.Name}: click where the wall starts, then where it ends ({Price(def.Cost)} per tile). Walls stop everyone " +
                       "except at gates; where a wall crosses a road it becomes a gate. Homes inside a closed ring feel safer.";
            var cost = Walls.PlanCost(world, _session.PlayerTown, _tiles, Tier);
            return $"{def.Name}: click where it ends ({_tiles.Count} tiles, {Price(cost)})";
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
        _tiles = GateMode || _start is not { } start ? new List<(int, int)> { hover } : LPath(start, hover);

        Problem = null;
        var world = _session.World;
        var town = _session.PlayerTown;
        int n = Math.Min(_tiles.Count, MaxTiles);
        for (int i = 0; i < n; i++)
        {
            var (x, y) = _tiles[i];
            var tile = world.Map.GetTile(x, y);
            Color color;
            if (GateMode)
            {
                bool wall = town.Walls.ContainsKey((x, y)) || tile.WallPlanned;
                Problem ??= wall ? null : "Gates go in a wall";
                color = wall ? GateColor : Invalid;
            }
            else
            {
                bool done = !Walls.NeedsWork(town, tile, x, y, Tier, tile.IsRoad);
                string? error = town.Walls.ContainsKey((x, y)) || tile.WallPlanned ? null : Walls.Check(world, town, x, y);
                Problem ??= error;
                color = done ? Existing : error != null ? Invalid : tile.IsRoad ? GateColor : Valid;
            }
            _preview.SetInstanceTransform(i, new Transform3D(Basis.Identity, Coords.TileCenter(x, y) + new Vector3(0, GroundY(x, y) + 0.25f, 0)));
            _preview.SetInstanceColor(i, color);
        }
        _preview.VisibleInstanceCount = n;
        if (_tiles.Count > MaxTiles) Problem = "Wall too long";
    }

    /// <summary>Handles a left click. Returns the command result once a wall or gate is submitted.</summary>
    public CommandResult? Click()
    {
        if (!Active || _tiles.Count == 0) return null;
        var town = _session.PlayerTown;
        if (GateMode) return _session.Execute(new PlaceGateCommand(town.Id, _tiles[0].X, _tiles[0].Y));
        if (_start == null)
        {
            _start = _tiles[0];
            return null;
        }
        if (_tiles.Count > MaxTiles) return CommandResult.Fail("Wall too long");
        var result = _session.Execute(new PlaceWallCommand(town.Id, _tiles.ToList(), Tier));
        if (result.Ok) _start = null;
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

    private float GroundY(int x, int y) =>
        Mathf.Max(TerrainShape.HeightAt(_session.World.Map, x + 0.5f, y + 0.5f), TerrainShape.WaterLevel);
}
