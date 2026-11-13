using Godot;
using Sim.Core.Commands;
using Sim.Core.Settlements;

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

    /// <summary>Placing a gate, tower or gatehouse on a wall (one click), rather than drawing a wall.</summary>
    public WallPiece? Piece { get; private set; }

    public bool GateMode => Piece != null;

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

    public void Begin(byte tier, WallPiece? piece = null)
    {
        Tier = tier;
        Piece = piece;
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
            if (Piece is { } piece)
            {
                var stone = world.Data.WallTiers[Math.Min(1, world.Data.WallTiers.Count - 1)];
                string what = piece switch
                {
                    WallPiece.Tower => $"Tower: click a wall to raise a tower on it: taller, with room for more defenders " +
                                       $"({Price(world.Data.WallTiers[0].Tower.Cost)} on a palisade, {Price(stone.Tower.Cost)} on stone).",
                    WallPiece.Gatehouse => $"Gatehouse: click a gate to fortify it with flanking towers; traffic still passes " +
                                           $"({Price(world.Data.WallTiers[0].Gatehouse.Cost)} on a palisade, {Price(stone.Gatehouse.Cost)} on stone). Press G again for gates.",
                    _ => "Gate: click a wall to put a gate in it. People, carts and roads pass through gates; " +
                         $"a gate costs {Price(world.Data.WallTiers[0].GateCost)} in a palisade, more in stone. Press G again for a gatehouse.",
                };
                return Problem != null ? $"{piece}: {Problem}" : what + " (right-click to stop)";
            }
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
            if (Piece is { } want)
            {
                var current = town.Walls.TryGetValue((x, y), out var seg) ? seg.Piece
                    : town.WallPlans.FirstOrDefault(p => p.X == x && p.Y == y)?.Piece;
                bool ok = current is { } c && c != want && Walls.CanBecome(c, want);
                Problem ??= ok ? null : want == WallPiece.Gatehouse ? "Gatehouses go on a gate" : $"{want}s go on a plain wall";
                color = ok ? GateColor : Invalid;
            }
            else
            {
                bool done = !Walls.NeedsWork(town, tile, x, y, Tier);
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
        if (Piece is { } piece)
        {
            var (px, py) = _tiles[0];
            return _session.Execute(piece switch
            {
                WallPiece.Tower => new PlaceTowerCommand(town.Id, px, py),
                WallPiece.Gatehouse => new PlaceGatehouseCommand(town.Id, px, py),
                _ => (Sim.Core.Commands.ICommand)new PlaceGateCommand(town.Id, px, py),
            });
        }
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
