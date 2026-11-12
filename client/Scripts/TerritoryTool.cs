using Godot;
using Sim.Core.Commands;

namespace MedievalClient;

/// <summary>
/// Paints territory with a round brush: drag to claim land, Shift+drag to give land back. Claims only count
/// where they connect to existing territory and fit the allowance; the server (Sim.Core) decides.
/// </summary>
public partial class TerritoryTool : Node3D
{
    public const int MinBrush = 0;
    public const int MaxBrush = 12;
    private const int MaxPreview = (2 * MaxBrush + 1) * (2 * MaxBrush + 1);

    private static readonly Color Claimable = new(0.35f, 0.95f, 0.4f, 0.5f);
    private static readonly Color Owned = new(0.95f, 0.78f, 0.25f, 0.25f);
    private static readonly Color Blocked = new(0.95f, 0.25f, 0.2f, 0.5f);
    private static readonly Color Erase = new(1f, 0.55f, 0.15f, 0.55f);

    private SimSession _session = null!;
    private CameraRig _camera = null!;
    private MultiMesh _preview = null!;
    private readonly List<(int X, int Y)> _brushTiles = new();
    private (int X, int Y)? _lastStrokeTile;
    private bool _stroking;
    private bool _erasing;

    public bool Active { get; private set; }
    public int BrushRadius { get; private set; } = 3;

    /// <summary>The last refusal from the simulation while painting, shown in the status line.</summary>
    public string? Problem { get; private set; }

    public void Initialize(SimSession session, CameraRig camera)
    {
        _session = session;
        _camera = camera;
        var tileMesh = new BoxMesh { Size = new Vector3(0.95f, 0.04f, 0.95f), Material = Materials.Overlay };
        _preview = TerrainRenderer.NewMultiMesh(tileMesh, MaxPreview, colors: true);
        _preview.VisibleInstanceCount = 0;
        AddChild(new MultiMeshInstance3D { Multimesh = _preview, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
    }

    public void Begin()
    {
        Active = true;
        Problem = null;
    }

    public void Cancel()
    {
        Active = false;
        _stroking = false;
        Problem = null;
        _preview.VisibleInstanceCount = 0;
    }

    public void ResizeBrush(int change) => BrushRadius = Math.Clamp(BrushRadius + change, MinBrush, MaxBrush);

    /// <summary>Mouse button went down over the map.</summary>
    public void BeginStroke(bool erase)
    {
        if (!Active) return;
        _stroking = true;
        _erasing = erase;
        _lastStrokeTile = null;
        Problem = null;
    }

    public void EndStroke() => _stroking = false;

    public string Status
    {
        get
        {
            var town = _session.PlayerTown;
            string land = $"{town.Territory.Count}/{town.TerritoryAllowance} tiles (cap {town.TerritoryCap})";
            string auto = town.Rules.AutoExpandTerritory ? "  Auto land is on: spare allowance is claimed evenly each day." : "";
            string problem = Problem != null ? $"  [{Problem}]" : "";
            return $"Territory {land}: drag to claim, Shift+drag to give back, [ ] brush size ({BrushRadius}), right-click to stop.{auto}{problem}";
        }
    }

    public override void _Process(double delta)
    {
        if (!Active) return;
        if (_stroking && !Input.IsMouseButtonPressed(MouseButton.Left)) _stroking = false;

        if (_camera.MouseGroundPoint() is not Vector3 point)
        {
            _preview.VisibleInstanceCount = 0;
            return;
        }
        var center = Coords.WorldToTile(point);
        bool erase = _stroking ? _erasing : Input.IsKeyPressed(Key.Shift);

        _brushTiles.Clear();
        for (int dy = -BrushRadius; dy <= BrushRadius; dy++)
        for (int dx = -BrushRadius; dx <= BrushRadius; dx++)
            if (dx * dx + dy * dy <= BrushRadius * BrushRadius + BrushRadius)
                _brushTiles.Add((center.X + dx, center.Y + dy));

        if (_stroking && center != _lastStrokeTile)
        {
            _lastStrokeTile = center;
            Paint(erase);
        }
        ShowPreview(erase);
    }

    private void Paint(bool erase)
    {
        var town = _session.PlayerTown;
        var world = _session.World;
        if (erase)
        {
            var owned = _brushTiles.Where(t => world.Map.GetTile(t.X, t.Y).TerritoryId == town.Id).ToList();
            if (owned.Count == 0) return;
            var result = _session.Execute(new ReleaseTerritoryCommand(town.Id, owned));
            Problem = result.Ok ? null : result.Error;
        }
        else
        {
            var fresh = _brushTiles.Where(t => world.Map.GetTile(t.X, t.Y).TerritoryId != town.Id).ToList();
            if (fresh.Count == 0) return;
            var result = _session.Execute(new ClaimTerritoryCommand(town.Id, fresh));
            Problem = result.Ok ? null : result.Error;
        }
    }

    private void ShowPreview(bool erase)
    {
        var town = _session.PlayerTown;
        var world = _session.World;
        int n = 0;
        foreach (var (x, y) in _brushTiles)
        {
            if (n >= MaxPreview) break;
            var tile = world.Map.GetTile(x, y);
            bool mine = tile.TerritoryId == town.Id;
            Color color;
            if (erase) color = mine && TerritoryRules.CheckRelease(world, town, x, y) == null ? Erase : mine ? Blocked : Owned with { A = 0 };
            else color = mine ? Owned : TerritoryRules.CheckClaim(world, town, x, y) == null ? Claimable : Blocked;
            if (color.A <= 0) continue;
            _preview.SetInstanceTransform(n, new Transform3D(Basis.Identity, Coords.TileCenter(x, y) + new Vector3(0, 0.04f, 0)));
            _preview.SetInstanceColor(n, color);
            n++;
        }
        _preview.VisibleInstanceCount = n;
    }
}
