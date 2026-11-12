using Godot;
using Sim.Core.Commands;
using Sim.Core.Data;

namespace MedievalClient;

/// <summary>
/// Shows a see-through ghost of the building under the mouse on a footprint pad that follows the ground:
/// green where the building can go and red (with the reason) where it can't. Placing sends a <see cref="PlaceBuildingCommand"/>, exactly as the AI does.
/// </summary>
public partial class PlacementTool : Node3D
{
    private static readonly Color Valid = new(0.3f, 0.95f, 0.35f, 0.45f);
    private static readonly Color Invalid = new(0.95f, 0.25f, 0.2f, 0.45f);

    // The ghost keeps the building's own colours, washed towards green or red and partly transparent.
    private static readonly Color ValidTint = new(0.75f, 1f, 0.75f, 0.6f);
    private static readonly Color InvalidTint = new(1f, 0.55f, 0.5f, 0.6f);
    private static readonly Color ValidGlow = new(0.05f, 0.25f, 0.05f);
    private static readonly Color InvalidGlow = new(0.3f, 0.03f, 0.02f);

    private SimSession _session = null!;
    private CameraRig _camera = null!;
    private MeshInstance3D _ghost = null!;
    private MeshInstance3D _pad = null!;
    private StandardMaterial3D _ghostMaterial = null!;
    private (int X, int Y, bool Ok)? _padBuiltFor;
    private MeshInstance3D _radius = null!;
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
        _ghostMaterial = new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true,
            VertexColorIsSrgb = true,
            AlbedoColor = ValidTint,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            // Draw depth first so only the outer surface shows, not the faces inside the model.
            DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Always,
            EmissionEnabled = true,
            Emission = ValidGlow,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            Roughness = 1f,
        };
        _ghost = new MeshInstance3D
        {
            Visible = false,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = _ghostMaterial,
        };
        AddChild(_ghost);
        _pad = new MeshInstance3D { Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(_pad);
        _radius = new MeshInstance3D { Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(_radius);
    }

    public void Begin(BuildingDef def)
    {
        Active = def;
        _ghost.Mesh = LowPolyMeshes.Building(def);
        _ghost.Visible = true;
        _padBuiltFor = null;
        _radius.Mesh = null;
    }

    public void Cancel()
    {
        Active = null;
        Problem = null;
        _site = null;
        _ghost.Visible = false;
        _pad.Visible = false;
        _radius.Visible = false;
    }

    public override void _Process(double delta)
    {
        if (Active == null) return;
        var ground = _camera.MouseGroundPoint();
        if (ground is not Vector3 point)
        {
            _ghost.Visible = false;
            _pad.Visible = false;
            _radius.Visible = false;
            return;
        }

        var (tx, ty) = Coords.WorldToTile(point);
        int left = tx - Active.Width / 2, top = ty - Active.Height / 2;
        _site = (left, top);

        var town = _session.PlayerTown;
        Problem = Placement.Check(_session.World, town, Active, left, top);
        bool ok = Problem == null;
        _material.AlbedoColor = ok ? Valid : Invalid;
        _ghostMaterial.AlbedoColor = ok ? ValidTint : InvalidTint;
        _ghostMaterial.Emission = ok ? ValidGlow : InvalidGlow;
        _ghost.Visible = true;
        _ghost.Position = Coords.TileCorner(left, top); // building models have their origin at the footprint corner

        if (_padBuiltFor is not var (px, py, _) || px != left || py != top)
        {
            // The service reach depends only on position, so it's rebuilt only when the site moves.
            _radius.Mesh = Active.Service is { } service
                ? ReachOutline(left + Active.Width / 2, top + Active.Height / 2, service.Radius)
                : null;
        }
        if (_padBuiltFor != (left, top, ok))
        {
            _padBuiltFor = (left, top, ok);
            _pad.Mesh = FootprintPad(left, top, Active.Width, Active.Height, ok ? Valid : Invalid);
        }
        _pad.Visible = true;
        _radius.Visible = _radius.Mesh != null;
    }

    /// <summary>A translucent pad over the footprint tiles, draped on the ground's shape.</summary>
    private ArrayMesh FootprintPad(int left, int top, int width, int height, Color color)
    {
        var map = _session.World.Map;
        Vector3 V(int x, int z) => new(x, Mathf.Max(TerrainShape.CornerHeight(map, x, z), TerrainShape.WaterLevel) + 0.04f, z);
        var mb = new MeshBuilder();
        for (int z = top; z < top + height; z++)
        for (int x = left; x < left + width; x++)
            mb.Quad(V(x, z), V(x + 1, z), V(x + 1, z + 1), V(x, z + 1), color, Vector3.Up);
        return mb.Commit(Materials.Overlay);
    }

    /// <summary>
    /// The diamond of homes a service building would reach (Manhattan distance from its centre tile), draped
    /// over the ground in one-tile pieces so hills don't hide it.
    /// </summary>
    private ArrayMesh ReachOutline(int cx, int cz, int radius)
    {
        var map = _session.World.Map;
        var color = new Color(1f, 0.9f, 0.3f, 0.6f);
        const float t = 0.25f;
        Vector3 At(Vector2 p) => new(p.X, Mathf.Max(TerrainShape.HeightAt(map, p.X, p.Y), TerrainShape.WaterLevel) + 0.08f, p.Y);

        var centre = new Vector2(cx + 0.5f, cz + 0.5f);
        var corners = new[] { new Vector2(0, -radius), new Vector2(radius, 0), new Vector2(0, radius), new Vector2(-radius, 0) };
        var mb = new MeshBuilder();
        for (int i = 0; i < 4; i++)
        {
            var a = centre + corners[i];
            var b = centre + corners[(i + 1) % 4];
            var inward = new Vector2(b.Y - a.Y, a.X - b.X).Normalized() * t;
            for (int k = 0; k < radius; k++)
            {
                var p0 = a.Lerp(b, k / (float)radius);
                var p1 = a.Lerp(b, (k + 1) / (float)radius);
                mb.Quad(At(p0), At(p1), At(p1 + inward), At(p0 + inward), color, Vector3.Up);
            }
        }
        return mb.Commit(Materials.Overlay);
    }

    /// <summary>Tries to place the active building at the current spot.</summary>
    public CommandResult? TryPlace()
    {
        if (Active == null || _site is not var (x, y)) return null;
        return _session.Execute(new PlaceBuildingCommand(_session.PlayerTown.Id, Active.Id, x, y));
    }
}
