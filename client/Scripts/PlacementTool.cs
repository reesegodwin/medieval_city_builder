using Godot;
using Sim.Core.Commands;
using Sim.Core.Data;

namespace MedievalClient;

/// <summary>
/// Shows a ghost footprint under the mouse, green where the building can go and red (with the
/// reason) where it can't. Placing sends a <see cref="PlaceBuildingCommand"/>, exactly as the AI does.
/// </summary>
public partial class PlacementTool : Node3D
{
    private static readonly Color Valid = new(0.3f, 0.95f, 0.35f, 0.45f);
    private static readonly Color Invalid = new(0.95f, 0.25f, 0.2f, 0.45f);

    private SimSession _session = null!;
    private CameraRig _camera = null!;
    private MeshInstance3D _ghost = null!;
    private StandardMaterial3D _material = null!;
    private (int X, int Y)? _site;

    public BuildingDef? Active { get; private set; }

    /// <summary>Why the current spot is invalid, or null when it's fine.</summary>
    public string? Problem { get; private set; }

    public void Initialize(SimSession session, CameraRig camera)
    {
        _session = session;
        _camera = camera;
        _material = Materials.Transparent(Valid);
        _ghost = new MeshInstance3D { Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(_ghost);
    }

    public void Begin(BuildingDef def)
    {
        Active = def;
        _ghost.Mesh = new BoxMesh { Size = new Vector3(def.Width, 0.5f, def.Height), Material = _material };
        _ghost.Visible = true;
    }

    public void Cancel()
    {
        Active = null;
        Problem = null;
        _site = null;
        _ghost.Visible = false;
    }

    public override void _Process(double delta)
    {
        if (Active == null) return;
        var ground = _camera.MouseGroundPoint();
        if (ground is not Vector3 point)
        {
            _ghost.Visible = false;
            return;
        }

        var (tx, ty) = Coords.WorldToTile(point);
        int left = tx - Active.Width / 2, top = ty - Active.Height / 2;
        _site = (left, top);

        var town = _session.PlayerTown;
        Problem = Placement.Check(_session.World, town, Active, left, top);
        _material.AlbedoColor = Problem == null ? Valid : Invalid;
        _ghost.Visible = true;
        _ghost.Position = new Vector3(left + Active.Width / 2f, 0.25f, top + Active.Height / 2f);
    }

    /// <summary>Tries to place the active building at the current spot.</summary>
    public CommandResult? TryPlace()
    {
        if (Active == null || _site is not var (x, y)) return null;
        return _session.Execute(new PlaceBuildingCommand(_session.PlayerTown.Id, Active.Id, x, y));
    }
}
