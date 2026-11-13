using Godot;
using Sim.Core.Commands;
using Sim.Core.Settlements;

namespace MedievalClient;

/// <summary>Highlights the building or road under the mouse in red; clicking tears it down.</summary>
public partial class DemolishTool : Node3D
{
    private static readonly Color Highlight = new(1f, 0.2f, 0.15f, 0.45f);

    private SimSession _session = null!;
    private CameraRig _camera = null!;
    private MeshInstance3D _highlight = null!;
    private Building? _building;
    private (int X, int Y)? _road;
    private (int X, int Y)? _wall;

    public bool Active { get; private set; }

    public void Initialize(SimSession session, CameraRig camera)
    {
        _session = session;
        _camera = camera;
        _highlight = new MeshInstance3D { Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(_highlight);
    }

    public void Begin() => Active = true;

    public void Cancel()
    {
        Active = false;
        _building = null;
        _road = null;
        _highlight.Visible = false;
    }

    public string Status => _building is { } b
        ? $"Demolish {b.Def.Name}? Refund: {RefundText(b)} (right-click to stop)"
        : _wall != null ? "Remove this wall tile (a planned wall is refunded; a finished one gives a quarter back)"
        : _road != null ? "Remove this road tile" : "Demolish: point at a building, road or wall (right-click to stop)";

    public override void _Process(double delta)
    {
        if (!Active) return;
        _building = null;
        _road = null;
        _highlight.Visible = false;
        if (_camera.MouseGroundPoint() is not Vector3 point) return;

        var (x, y) = Coords.WorldToTile(point);
        var town = _session.PlayerTown;
        var tile = _session.World.Map.GetTile(x, y);
        if (tile.BuildingId != 0 && town.FindBuilding(tile.BuildingId) is { } b)
        {
            _building = b;
            Show(new Vector3(b.Def.Width + 0.1f, 1.6f, b.Def.Height + 0.1f), Coords.BuildingCenter(b) + new Vector3(0, 0.8f, 0));
        }
        else if (town.Walls.ContainsKey((x, y)) || tile.WallPlanned)
        {
            _wall = (x, y);
            Show(new Vector3(1f, 1.4f, 1f), Coords.TileCenter(x, y) + new Vector3(0, 0.7f, 0));
        }
        else if (tile.IsRoad && town.Roads.Contains((x, y)))
        {
            _road = (x, y);
            Show(new Vector3(1f, 0.1f, 1f), Coords.TileCenter(x, y) + new Vector3(0, 0.05f, 0));
        }
    }

    public CommandResult? Click()
    {
        var town = _session.PlayerTown;
        if (_building is { } b) return _session.Execute(new DemolishBuildingCommand(town.Id, b.Id));
        if (_wall is { } w) return _session.Execute(new RemoveWallCommand(town.Id, new[] { w }));
        if (_road is { } r) return _session.Execute(new RemoveRoadCommand(town.Id, new[] { r }));
        return null;
    }

    private void Show(Vector3 size, Vector3 position)
    {
        if (_highlight.Mesh is not BoxMesh box || box.Size != size)
            _highlight.Mesh = new BoxMesh { Size = size, Material = Materials.Transparent(Highlight) };
        _highlight.Position = position;
        _highlight.Visible = true;
    }

    private static string RefundText(Building b)
    {
        double share = b.State switch
        {
            BuildingState.UnderConstruction => DemolishBuildingCommand.ConstructionRefund,
            BuildingState.Operational => DemolishBuildingCommand.OperationalRefund,
            _ => 0,
        };
        var parts = b.Def.Cost.Select(kv => (int)(kv.Value * share)).Zip(b.Def.Cost.Keys, (n, r) => $"{n} {r}")
            .Where(s => !s.StartsWith("0 ")).ToList();
        return parts.Count == 0 ? "nothing" : string.Join(", ", parts);
    }
}
