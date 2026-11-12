using Godot;
using MedievalClient.Ui;
using Sim.Core.Commands;

namespace MedievalClient;

/// <summary>
/// Entry point. Creates the simulation session and all renderers/UI in code, and routes input.
/// User arguments (after "--" on Godot's command line):
///   --seed N              world seed (default 42)
///   --warmup-days N       fast-forward the simulation before showing it
///   --screenshot file.png save a screenshot after a few frames and quit (automated checks)
///   --inspect             select a citizen on start (shows the inspector panel)
/// </summary>
public partial class Main : Node3D
{
    private const double UiRefreshSeconds = 0.25;
    private const float CitizenPickRadius = 0.45f;

    private SimSession _session = null!;
    private CameraRig _camera = null!;
    private CitizenRenderer _citizens = null!;
    private PlacementTool _placement = null!;
    private RoadTool _roadTool = null!;
    private DemolishTool _demolishTool = null!;
    private Hud _hud = null!;
    private InspectorPanel _inspector = null!;
    private MeshInstance3D _marker = null!;

    private int? _selectedBuilding;
    private int? _selectedCitizen;
    private double _uiTimer;
    private float _markerClock;
    private string? _flash;
    private double _flashTimer;
    private string? _screenshotPath;
    private int _frames;

    public override void _Ready()
    {
        _session = new SimSession(SimSession.SeedFromCommandLine(42));
        var town = _session.PlayerTown;

        SetupLighting();

        _camera = new CameraRig { Name = "CameraRig" };
        AddChild(_camera);
        _camera.LookAtTile(town.CenterX, town.CenterY);

        var terrain = new TerrainRenderer { Name = "Terrain" };
        terrain.Initialize(_session.World.Map);
        terrain.FocusProvider = () => _camera.Focus;
        AddChild(terrain);

        var buildings = new BuildingRenderer { Name = "Buildings" };
        buildings.Initialize(town);
        AddChild(buildings);

        _citizens = new CitizenRenderer { Name = "Citizens" };
        AddChild(_citizens);
        _citizens.Initialize(_session.World, town);

        var roads = new RoadRenderer { Name = "Roads" };
        AddChild(roads);
        roads.Initialize(_session.World.Map, town);

        _placement = new PlacementTool { Name = "Placement" };
        AddChild(_placement);
        _placement.Initialize(_session, _camera);

        _roadTool = new RoadTool { Name = "RoadTool" };
        AddChild(_roadTool);
        _roadTool.Initialize(_session, _camera);

        _demolishTool = new DemolishTool { Name = "DemolishTool" };
        AddChild(_demolishTool);
        _demolishTool.Initialize(_session, _camera);

        _marker = new MeshInstance3D { Mesh = LowPolyMeshes.SelectionMarker, Visible = false };
        AddChild(_marker);

        _hud = new Hud { Name = "Hud" };
        AddChild(_hud);
        _hud.Build(_session.World.Data, town.Rules.AutoBuild);
        _hud.BuildRequested += id =>
        {
            CancelTools();
            ClearSelection();
            _placement.Begin(_session.World.Data.Building(id));
        };
        _hud.RoadRequested += StartRoadTool;
        _hud.DemolishRequested += StartDemolishTool;
        _hud.SpeedRequested += speed =>
        {
            _session.Speed = speed;
            _session.Paused = false;
        };
        _hud.PauseToggled += () => _session.Paused = !_session.Paused;
        _hud.AutoBuildToggled += on => _session.Execute(new SetAutoBuildCommand(town.Id, on));

        _inspector = new InspectorPanel { Name = "Inspector" };
        _hud.AddChild(_inspector);
        _inspector.Closed += ClearSelection;

        if (int.TryParse(SimSession.UserArg("--warmup-days"), out int warmup) && warmup > 0)
            _session.RunDays(warmup);
        _screenshotPath = SimSession.UserArg("--screenshot");
        if (SimSession.UserArg("--inspect") != null)
            _selectedCitizen = town.Citizens.FirstOrDefault(c => c.IsAdult && c.JobBuildingId != null)?.Id;

        _hud.AddEvents(_session.TakeNewEvents());
        _hud.Refresh(_session);
        RefreshInspector();
    }

    private void SetupLighting()
    {
        var sky = new Color(0.66f, 0.78f, 0.90f);
        var skyMaterial = new ProceduralSkyMaterial
        {
            SkyTopColor = new Color(0.32f, 0.52f, 0.82f),
            SkyHorizonColor = sky,
            GroundHorizonColor = sky,
            GroundBottomColor = new Color(0.35f, 0.42f, 0.30f),
            SunAngleMax = 30f,
        };
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = new Sky { SkyMaterial = skyMaterial },
            AmbientLightSource = Godot.Environment.AmbientSource.Sky,
            AmbientLightSkyContribution = 1f,
            AmbientLightEnergy = 0.45f,
            TonemapMode = Godot.Environment.ToneMapper.Filmic,
            FogEnabled = true,
            FogLightColor = sky,
            FogDensity = 0.0015f,
            FogSkyAffect = 0f,
        };
        AddChild(new WorldEnvironment { Environment = env });

        var sun = new DirectionalLight3D
        {
            LightColor = new Color(1f, 0.95f, 0.85f),
            LightEnergy = 0.85f,
            ShadowEnabled = true,
            DirectionalShadowMaxDistance = 90f,
            RotationDegrees = new Vector3(-50f, 35f, 0f),
        };
        AddChild(sun);
    }

    public override void _Process(double delta)
    {
        _session.Advance(delta);
        _citizens.Animate = !_session.Paused;

        _uiTimer += delta;
        if (_uiTimer >= UiRefreshSeconds)
        {
            _uiTimer = 0;
            _hud.Refresh(_session);
            _hud.AddEvents(_session.TakeNewEvents());
            RefreshInspector();
        }

        if (_flashTimer > 0)
        {
            _flashTimer -= delta;
            if (_flashTimer <= 0) _flash = null;
        }
        _hud.SetStatus(_flash ?? (_placement.Active is { } def
            ? _placement.Problem is { } p ? $"{def.Name}: {p}" : $"Place {def.Name} (right-click to stop)"
            : _roadTool.Active ? _roadTool.Status
            : _demolishTool.Active ? _demolishTool.Status
            : null));

        UpdateMarker((float)delta);

        if (_screenshotPath != null && ++_frames == 90) // let chunks stream in first
        {
            var image = GetViewport().GetTexture().GetImage();
            var error = image.SavePng(_screenshotPath);
            GD.Print(error == Error.Ok ? $"Saved screenshot to {_screenshotPath}" : $"Screenshot failed: {error}");
            GetTree().Quit();
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true }:
                if (_placement.Active != null) PlaceBuilding();
                else if (_roadTool.Active) Report(_roadTool.Click(), "Can't build road");
                else if (_demolishTool.Active) Report(_demolishTool.Click(), "Can't demolish");
                else Select();
                break;

            case InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true }:
            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape }:
                if (AnyToolActive) CancelTools();
                else ClearSelection();
                break;

            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.R }:
                StartRoadTool();
                break;

            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.X }:
                StartDemolishTool();
                break;

            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.Space }:
                _session.Paused = !_session.Paused;
                break;

            case InputEventKey { Pressed: true, Echo: false } key
                when key.Keycode >= Key.Key1 && key.Keycode < Key.Key1 + SimSession.Speeds.Length:
                _session.Speed = SimSession.Speeds[key.Keycode - Key.Key1];
                _session.Paused = false;
                break;
        }
    }

    private bool AnyToolActive => _placement.Active != null || _roadTool.Active || _demolishTool.Active;

    private void CancelTools()
    {
        _placement.Cancel();
        _roadTool.Cancel();
        _demolishTool.Cancel();
    }

    private void StartRoadTool()
    {
        CancelTools();
        ClearSelection();
        _roadTool.Begin();
    }

    private void StartDemolishTool()
    {
        CancelTools();
        ClearSelection();
        _demolishTool.Begin();
    }

    private void Report(CommandResult? result, string failurePrefix)
    {
        if (result is { Ok: false, Error: { } error }) Flash($"{failurePrefix}: {error}");
    }

    private void PlaceBuilding()
    {
        var result = _placement.TryPlace();
        if (result is { Ok: false, Error: { } error }) Flash($"Can't build here: {error}");
        else if (result is { Ok: true } && _placement.Active is { } def &&
                 !_session.PlayerTown.Stock.Has(def.Cost))
            Flash($"{def.Name} planned. Waiting for materials.");
    }

    private void Select()
    {
        if (_camera.MouseGroundPoint() is not Vector3 point) return;
        var town = _session.PlayerTown;

        // Citizens first: they stand on top of buildings and fields.
        int? nearest = null;
        float best = CitizenPickRadius;
        foreach (var (id, pos) in _citizens.Positions)
        {
            float d = new Vector2(pos.X - point.X, pos.Z - point.Z).Length();
            if (d < best)
            {
                best = d;
                nearest = id;
            }
        }

        ClearSelection();
        if (nearest is int citizenId)
        {
            _selectedCitizen = citizenId;
        }
        else
        {
            var (tx, ty) = Coords.WorldToTile(point);
            int buildingId = _session.World.Map.GetTile(tx, ty).BuildingId;
            if (buildingId != 0 && town.FindBuilding(buildingId) != null) _selectedBuilding = buildingId;
        }
        RefreshInspector();
    }

    private void ClearSelection()
    {
        _selectedBuilding = null;
        _selectedCitizen = null;
        _inspector.Visible = false;
        _marker.Visible = false;
    }

    private void RefreshInspector()
    {
        var town = _session.PlayerTown;
        if (_selectedCitizen is int cid)
        {
            if (town.FindCitizen(cid) is { } c) _inspector.ShowCitizen(_session.World, town, c);
            else ClearSelection(); // died or left
        }
        else if (_selectedBuilding is int bid && town.FindBuilding(bid) is { } b)
        {
            _inspector.ShowBuilding(town, b);
        }
    }

    private void UpdateMarker(float delta)
    {
        _markerClock += delta;
        float bob = Mathf.Sin(_markerClock * 3f) * 0.05f;
        var town = _session.PlayerTown;

        if (_selectedCitizen is int cid && _citizens.Positions.TryGetValue(cid, out var pos))
        {
            _marker.Visible = true;
            _marker.Position = pos + new Vector3(0, 0.5f + bob, 0);
        }
        else if (_selectedBuilding is int bid && town.FindBuilding(bid) is { } b)
        {
            _marker.Visible = true;
            _marker.Position = Coords.BuildingCenter(b) + new Vector3(0, 2.0f + bob, 0);
        }
        else
        {
            _marker.Visible = false;
        }
        _marker.RotateY(delta * 1.5f);
    }

    private void Flash(string message)
    {
        _flash = message;
        _flashTimer = 3;
    }
}
