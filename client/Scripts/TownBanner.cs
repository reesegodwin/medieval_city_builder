using Godot;
using Sim.Core.Diplomacy;
using Sim.Core.Settlements;
using Sim.Core.Simulation;

namespace MedievalClient;

/// <summary>
/// A town's banner: a tall flag in its faction's colour by the town centre, with the town and realm names floating
/// above it (always facing the camera). NPC realms show their temperament. Refreshes when the town changes hands or
/// gets its faction (a new town gets one at the end of its first day).
/// </summary>
public partial class TownBanner : Node3D
{
    private GameWorld _world = null!;
    private Settlement _town = null!;
    private Label3D _label = null!;
    private MeshInstance3D _flag = null!;
    private TerritoryRenderer? _territory;
    private string _shown = "";

    public void Initialize(GameWorld world, Settlement town, TerritoryRenderer? territory)
    {
        _world = world;
        _town = town;
        _territory = territory;
        float ground = Mathf.Max(TerrainShape.HeightAt(world.Map, town.CenterX + 0.5f, town.CenterY + 0.5f), TerrainShape.WaterLevel);
        Position = new Vector3(town.CenterX + 3.5f, ground, town.CenterY + 3.5f);

        _flag = new MeshInstance3D
        {
            Mesh = LowPolyMeshes.WarBanner,
            Scale = new Vector3(2.2f, 2.6f, 2.2f),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_flag);
        _label = new Label3D
        {
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            FontSize = 36,
            OutlineSize = 10,
            FixedSize = true,      // the same size on screen at any zoom
            PixelSize = 0.0005f,
            Position = new Vector3(0, 8.5f, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            NoDepthTest = true,
        };
        AddChild(_label);
        Refresh();
    }

    public override void _Process(double delta)
    {
        if (_town != null) Refresh();
    }

    private void Refresh()
    {
        var faction = Factions.Of(_world, _town);
        string key = $"{_town.FactionId}|{faction?.Name}|{faction?.Colour}|{_town.Citizens.Count / 10}";
        if (key == _shown) return;
        _shown = key;

        var colour = faction != null ? Color.FromHtml(faction.Colour) : Colors.White;
        string temper = faction is { IsNpc: true } ? $" · {_world.Data.Factions.Personality(faction.Personality).Name}" : "";
        _label.Text = $"{_town.Name}\n{faction?.Name ?? ""}{temper} · {_town.Citizens.Count} people";
        _label.Modulate = colour.Lightened(0.35f);
        _label.OutlineModulate = new Color(0, 0, 0, 0.85f);
        _flag.MaterialOverride = new StandardMaterial3D { AlbedoColor = colour, VertexColorUseAsAlbedo = true };
        if (_territory != null && faction is { IsNpc: true }) _territory.BorderColor = colour;
    }
}
