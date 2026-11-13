using Sim.Core.War;
using Godot;
using Sim.Core.Settlements;
using MedievalClient.Audio;
using MedievalClient.Ui;
using Sim.Core.Commands;
using Sim.Core.Simulation;

namespace MedievalClient;

/// <summary>
/// Entry point. Shows the start menu (or starts straight from the command line), then creates all renderers and
/// UI in code and routes input. User arguments (after "--" on Godot's command line):
///   --join HOST[:PORT]    join a shared world as --name
///   --host [PORT]         host a world for friends as --name (continues the hosted world if there is one)
///   --name NAME           player name for shared worlds
///   --world-speed PRESET  persistent | weekend | evening | sprint (new worlds)
///   --menu                show the start menu even with other options (for screenshots)
///   --seed N              world seed (default 42); starts single-player directly
///   --warmup-days N       fast-forward the simulation before showing it
///   --screenshot file.png save a screenshot after a few frames and quit (automated checks)
///   --inspect             select a citizen on start (shows the inspector panel)
///   --rules               open the rules panel on start
///   --trade [TAB]         open the trade panel on start (tab 0-4)
///   --ledger [TAB]        open the town ledger on start (tab 0-3)
///   --ledger-good ID      open the ledger's Goods tab with a good's 30-day charts (e.g. wood)
///   --follow-caravan      keep the camera on a caravan on the road (automated checks)
///   --demo-walls          lay a stone wall and a large wall, each with a tower and gatehouse, near the centre (screenshots)
///   --look-at ID          start with the camera on the town's first building of type ID (e.g. fishing_hut), or wall/gate/tower/gatehouse
///   --territory           open the territory paint tool on start
///   --zoom N              start with the camera N units away (6-90)
///   --place ID            start placing building ID with the cursor mid-screen (automated checks)
///   --cursor X,Y          with --place, put the cursor at this pixel instead
///   --save-load-test      save, reload the scene from the save, then continue (automated check)
///   --export-sounds DIR   write every generated sound to DIR as .wav files
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
    private WallTool _wallTool = null!;
    private TerritoryTool _territoryTool = null!;
    private TerritoryRenderer _territory = null!;
    private Hud _hud = null!;
    private InspectorPanel _inspector = null!;
    private RulesPanel _rules = null!;
    private TradePanel _trade = null!;
    private LedgerPanel _ledger = null!;
    private ArmyPanel _army = null!;
    private SoundManager _sound = null!;
    private MeshInstance3D _marker = null!;

    private int? _selectedBuilding;
    private int? _selectedCitizen;
    private double _uiTimer;
    private float _markerClock;
    private string? _flash;
    private double _flashTimer;
    private string? _screenshotPath;
    private int _frames;
    private static bool _saveLoadTestDone;
    private static bool _justLoaded;
    private bool _followCaravan;
    private DirectionalLight3D? _sun;
    private readonly HashSet<int> _renderedSettlements = new();
    private Node3D _world3D = null!;

    public override void _Notification(int what)
    {
        if (what == NotificationWMCloseRequest) SimSession.Current?.Dispose(); // a host saves and stops serving
    }

    public override void _ExitTree()
    {
        if (_session == null) return;
        _session.Notice -= Flash;
        _session.WorldReplaced -= Rebuild;
    }

    /// <summary>A session from command-line arguments, or null to show the start menu.</summary>
    private static SimSession? SessionFromCommandLine()
    {
        string name = SimSession.UserArg("--name") is { Length: > 0 } n ? n : "Player";
        double secondsPerDay = WorldSpeeds.SecondsPerDay(SimSession.UserArg("--world-speed") ?? "evening") ?? 60;
        if (SimSession.UserArg("--join") is { Length: > 0 } address)
        {
            int port = Sim.Net.Protocol.DefaultPort;
            int colon = address.LastIndexOf(':');
            if (colon > 0 && int.TryParse(address[(colon + 1)..], out int p))
            {
                port = p;
                address = address[..colon];
            }
            return SimSession.JoinGame(name, address, port);
        }
        if (SimSession.UserArg("--host") is { } hostArg)
        {
            int port = int.TryParse(hostArg, out int p) ? p : Sim.Net.Protocol.DefaultPort;
            return SimSession.HostGame(name, port, SimSession.SeedFromCommandLine(42),
                new WorldSettings { SecondsPerGameDay = secondsPerDay, WhenEmpty = EmptyWorldPolicy.CatchUp });
        }
        if (SimSession.UserArg("--menu") != null) return null;
        string[] directStart = { "--seed", "--warmup-days", "--screenshot", "--place", "--inspect", "--rules",
            "--territory", "--zoom", "--save-load-test", "--export-sounds" };
        return directStart.Any(a => SimSession.UserArg(a) != null)
            ? SimSession.NewLocal(SimSession.SeedFromCommandLine(42), secondsPerDay)
            : null;
    }

    private void ShowStartMenu()
    {
        var menu = new StartMenu { Name = "StartMenu" };
        menu.Started += session =>
        {
            SimSession.Current = session;
            GetTree().ReloadCurrentScene();
        };
        AddChild(menu);
    }

    /// <summary>The world object was replaced (resync with the server): rebuild the scene around it.</summary>
    private void Rebuild() => CallDeferred(MethodName.ReloadScene);

    private void ReloadScene() => GetTree().ReloadCurrentScene();

    public override void _Ready()
    {
        // The session survives scene reloads (loading a save, resyncing with a server).
        if (SimSession.Current == null)
        {
            try
            {
                SimSession.Current = SessionFromCommandLine();
            }
            catch (Exception e)
            {
                GD.PrintErr($"Couldn't start: {e.Message}");
            }
            if (SimSession.Current == null)
            {
                ShowStartMenu();
                return;
            }
        }
        _session = SimSession.Current;
        _session.Notice += Flash;
        _session.WorldReplaced += Rebuild;
        bool wasLoaded = _justLoaded;
        _justLoaded = false;
        var town = _session.PlayerTown;

        SetupLighting();

        _camera = new CameraRig { Name = "CameraRig" };
        AddChild(_camera);
        _camera.LookAtTile(town.CenterX, town.CenterY);

        var terrain = new TerrainRenderer { Name = "Terrain" };
        terrain.Initialize(_session.World.Map);
        terrain.FocusProvider = () => _camera.Focus;
        AddChild(terrain);

        _world3D = new Node3D { Name = "Settlements" };
        AddChild(_world3D);
        AddNewSettlements();

        var caravans = new CaravanRenderer { Name = "Caravans" };
        AddChild(caravans);
        caravans.Initialize(_session.World);

        var warbands = new WarbandRenderer { Name = "Warbands" };
        AddChild(warbands);
        warbands.Initialize(_session);

        _placement = new PlacementTool { Name = "Placement" };
        AddChild(_placement);
        _placement.Initialize(_session, _camera);

        _roadTool = new RoadTool { Name = "RoadTool" };
        AddChild(_roadTool);
        _roadTool.Initialize(_session, _camera);

        _wallTool = new WallTool { Name = "WallTool" };
        AddChild(_wallTool);
        _wallTool.Initialize(_session, _camera);

        _demolishTool = new DemolishTool { Name = "DemolishTool" };
        AddChild(_demolishTool);
        _demolishTool.Initialize(_session, _camera);

        _territoryTool = new TerritoryTool { Name = "TerritoryTool" };
        AddChild(_territoryTool);
        _territoryTool.Initialize(_session, _camera);

        _sound = new SoundManager { Name = "Sound" };
        AddChild(_sound);
        _sound.Initialize(town, () => _camera.Focus);

        _marker = new MeshInstance3D { Mesh = LowPolyMeshes.SelectionMarker, Visible = false };
        AddChild(_marker);

        _hud = new Hud { Name = "Hud" };
        AddChild(_hud);
        _hud.Build(_session.World.Data, town.Rules.AutoBuild, town.Rules.AutoExpandTerritory,
            SimSession.Speeds.Where(x => x <= _session.MaxSpeed).ToList(), _session.CanChangeSpeed, _session.CanPause);
        _hud.BuildRequested += id =>
        {
            CancelTools();
            ClearSelection();
            _placement.Begin(_session.World.Data.Building(id));
        };
        _hud.RoadRequested += StartRoadTool;
        _hud.RoadTierRequested += tier =>
        {
            CancelTools();
            ClearSelection();
            _roadTool.Begin(tier);
        };
        _hud.DemolishRequested += StartDemolishTool;
        _hud.WallRequested += tier => StartWallTool(tier, null);
        _hud.GateRequested += () => StartWallTool(0, WallPiece.Gate);
        _hud.TowerRequested += () => StartWallTool(0, WallPiece.Tower);
        _hud.GatehouseRequested += () => StartWallTool(0, WallPiece.Gatehouse);
        _hud.SpeedRequested += speed =>
        {
            _session.Speed = speed;
            _session.Paused = false;
        };
        _hud.PauseToggled += () =>
        {
            if (_session.CanPause) _session.Paused = !_session.Paused;
        };
        _hud.AutoBuildToggled += on => _session.Execute(new SetAutoBuildCommand(town.Id, on));
        _hud.AutoTerritoryToggled += on => _session.Execute(new SetAutoExpandTerritoryCommand(town.Id, on));
        _hud.TerritoryRequested += StartTerritoryTool;
        _hud.ExpandTerritoryRequested += () =>
        {
            var result = _session.Execute(new ExpandTerritoryCommand(town.Id));
            Report(result, "Can't expand");
            if (result.Ok) Flash($"Territory: {town.Territory.Count}/{town.TerritoryAllowance} tiles");
        };

        _inspector = new InspectorPanel { Name = "Inspector" };
        _hud.AddChild(_inspector);
        _inspector.Closed += ClearSelection;
        _inspector.UpgradeRequested += () =>
        {
            var result = _session.Execute(new UpgradeTownCentreCommand(town.Id));
            Report(result, "Can't upgrade");
            if (result is { Ok: true, CreatedId: int id })
            {
                _sound.Placed();
                _selectedBuilding = id;
                Flash($"{town.FindBuilding(id)?.Def.Name} planned. Builders start once the materials are in.");
            }
            RefreshInspector();
        };

        _rules = new RulesPanel { Name = "Rules" };
        _rules.Initialize(_session);
        _hud.AddChild(_rules);
        _hud.RulesRequested += () => ShowOnly(_rules, _rules.Toggle);

        _trade = new TradePanel { Name = "Trade" };
        _trade.Initialize(_session);
        _hud.AddChild(_trade);
        _hud.TradeRequested += ToggleTrade;

        _ledger = new LedgerPanel { Name = "Ledger" };
        _ledger.Initialize(_session);
        _hud.AddChild(_ledger);
        _hud.LedgerRequested += ToggleLedger;

        _army = new ArmyPanel { Name = "Army" };
        _army.Initialize(_session);
        _hud.AddChild(_army);
        _hud.ArmyRequested += ToggleArmy;
        _hud.SaveRequested += SaveGame;
        _hud.LoadRequested += LoadGame;
        _hud.SoundToggled += _sound.ToggleMute;
        _hud.ButtonClicked += _sound.Click;
        _hud.ConnectClickSounds(_hud);
        if (SimSession.UserArg("--export-sounds") is { Length: > 0 } soundDir) _sound.ExportAll(soundDir);
        if (_session.Mode == SessionMode.Host)
            Flash($"Hosting on port {_session.HostPort}. Friends join with your address and that port.");
        if (wasLoaded)
        {
            Flash($"Loaded {town.Name}, {_session.World.Time}");
            GD.Print($"Loaded save: {_session.World.Time}, hash {_session.World.ComputeStateHash():X16}");
        }

        if (!wasLoaded && int.TryParse(SimSession.UserArg("--warmup-days"), out int warmup) && warmup > 0)
            _session.RunDays(warmup);
        _screenshotPath = SimSession.UserArg("--screenshot");
        if (SimSession.UserArg("--rules") != null) _rules.Toggle();
        if (SimSession.UserArg("--army") != null) _army.Toggle();
        if (SimSession.UserArg("--ledger") is { } ledgerTab)
        {
            _ledger.Toggle();
            if (int.TryParse(ledgerTab, out int tab)) _ledger.ShowTab(tab);
        }
        if (SimSession.UserArg("--ledger-good") is { Length: > 0 } good)
        {
            if (!_ledger.Visible) _ledger.Toggle();
            _ledger.SelectGood(good);
        }
        if (SimSession.UserArg("--trade") is { } tradeTab)
        {
            _trade.Toggle();
            if (int.TryParse(tradeTab, out int tab)) _trade.ShowTab(tab);
        }
        if (SimSession.UserArg("--territory") != null) StartTerritoryTool();
        if (float.TryParse(SimSession.UserArg("--zoom"), out float zoom)) _camera.SetDistance(zoom);
        _followCaravan = SimSession.UserArg("--follow-caravan") != null;
        if (SimSession.UserArg("--demo-walls") != null) DemoWalls();
        if (SimSession.UserArg("--demo-raid") != null && _session.Mode == SessionMode.Local) DemoRaid();
        if (SimSession.UserArg("--demo-siege") != null && _session.Mode == SessionMode.Local) DemoSiege();
        if (SimSession.UserArg("--look-at") is { Length: > 0 } lookAt)
        {
            var player = _session.PlayerTown;
            if (lookAt is "wall" or "gate" or "tower" or "gatehouse" &&
                player.Walls.FirstOrDefault(w => lookAt == "wall" || (lookAt == "gate" && w.Value.Gate) ||
                                                 (lookAt == "tower" && w.Value.Piece == WallPiece.Tower) ||
                                                 (lookAt == "gatehouse" && w.Value.Piece == WallPiece.Gatehouse)) is { Value: not null } wall)
                _camera.LookAtTile(wall.Key.X, wall.Key.Y);
            else if (player.Buildings.FirstOrDefault(b => b.Def.Id == lookAt) is { } sight)
                _camera.LookAtTile(sight.CenterX, sight.CenterY);
        }
        if (SimSession.UserArg("--place") is { Length: > 0 } placeId && _session.World.Data.Buildings.TryGetValue(placeId, out var placeDef))
            _placement.Begin(placeDef);
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

        var sun = _sun = new DirectionalLight3D
        {
            LightColor = new Color(1f, 0.95f, 0.85f),
            LightEnergy = 0.85f,
            ShadowEnabled = true,
            DirectionalShadowMaxDistance = 90f,
            RotationDegrees = new Vector3(-50f, 35f, 0f),
        };
        AddChild(sun);
    }

    /// <summary>Draws every settlement in the world: the player's own, and other players' as they appear.</summary>
    private void AddNewSettlements()
    {
        var map = _session.World.Map;
        foreach (var settlement in _session.World.Settlements)
        {
            if (!_renderedSettlements.Add(settlement.Id)) continue;
            bool mine = settlement == _session.PlayerTown;
            var root = new Node3D { Name = $"Settlement_{settlement.Id}" };
            _world3D.AddChild(root);

            var buildings = new BuildingRenderer { Name = "Buildings" };
            buildings.Initialize(settlement);
            root.AddChild(buildings);

            var citizens = new CitizenRenderer { Name = "Citizens" };
            root.AddChild(citizens);
            citizens.Initialize(_session.World, settlement);
            if (mine) _citizens = citizens;

            var roads = new RoadRenderer { Name = "Roads" };
            root.AddChild(roads);
            roads.Initialize(map, settlement);

            var walls = new WallRenderer { Name = "Walls" };
            root.AddChild(walls);
            walls.Initialize(_session.World, settlement);

            var territory = new TerritoryRenderer { Name = "Territory" };
            if (!mine) territory.BorderColor = TerritoryRenderer.OtherPlayerColor(settlement.Id);
            root.AddChild(territory);
            territory.Initialize(map, settlement, () => _camera.Distance);
            if (mine) _territory = territory;
        }
    }

    public override void _Process(double delta)
    {
        if (_session == null)
        {
            // Start menu: still honour --screenshot for automated checks.
            if (++_frames == 30 && SimSession.UserArg("--screenshot") is { Length: > 0 } menuShot)
            {
                GetViewport().GetTexture().GetImage().SavePng(menuShot);
                GD.Print($"Saved screenshot to {menuShot}");
                GetTree().Quit();
            }
            return;
        }
        _session.Advance(delta);
        SeasonLook.Update(_session.World);
        if (_sun != null) SeasonLook.LightSun(_sun, _session.World.Time);
        if (_session.World.Settlements.Count != _renderedSettlements.Count) AddNewSettlements();
        if (_followCaravan && _session.World.Trade.Caravans.FirstOrDefault() is { } caravan)
        {
            var (at, _) = CaravanRenderer.PositionAlongPath(caravan, (float)caravan.Progress(_session.World.Time.Tick));
            _camera.Position = new Vector3(at.X, 0, at.Y);
        }
        _citizens.Animate = !_session.Paused;

        _uiTimer += delta;
        if (_uiTimer >= UiRefreshSeconds)
        {
            _uiTimer = 0;
            _hud.Refresh(_session);
            var events = _session.TakeNewEvents();
            _hud.AddEvents(events);
            _sound.OnEvents(events);
            RefreshInspector();
            if (_rules.Visible) _rules.Refresh();
            _trade.Refresh();
            _army.Refresh();
            _hud.FeedVisible = !_rules.Visible && !_trade.Visible && !_ledger.Visible && !_army.Visible;
        }

        if (_flashTimer > 0)
        {
            _flashTimer -= delta;
            if (_flashTimer <= 0) _flash = null;
        }
        _hud.SetStatus(_flash ?? (_placement.Active is { } def
            ? _placement.Problem is { } p ? $"{def.Name}: {p}" : $"Place {def.Name} (right-click to stop)"
            : _roadTool.Active ? _roadTool.Status
            : _wallTool.Active ? _wallTool.Status
            : _demolishTool.Active ? _demolishTool.Status
            : _territoryTool.Active ? _territoryTool.Status
            : null));
        _territory.ShowFill = _territoryTool.Active;

        UpdateMarker((float)delta);
        _sound.Tick(delta, !_session.Paused);

        _frames++;
        // Automated checks: put the cursor mid-screen so a tool's preview shows in the screenshot.
        if (_frames == 5 && SimSession.UserArg("--place") != null)
        {
            var cursor = GetViewport().GetVisibleRect().Size / 2;
            if (SimSession.UserArg("--cursor")?.Split(',') is [var cx, var cy] &&
                float.TryParse(cx, out float px) && float.TryParse(cy, out float py))
                cursor = new Vector2(px, py);
            GetViewport().WarpMouse(cursor);
        }
        if (_frames == 20 && !_saveLoadTestDone && SimSession.UserArg("--save-load-test") != null)
        {
            _saveLoadTestDone = true;
            GD.Print($"Save/load test: saving at {_session.World.Time}, hash {_session.World.ComputeStateHash():X16}");
            SaveGame();
            LoadGame();
            return;
        }

        if (_screenshotPath != null && _frames == 90) // let chunks stream in first
        {
            var image = GetViewport().GetTexture().GetImage();
            var error = image.SavePng(_screenshotPath);
            GD.Print(error == Error.Ok ? $"Saved screenshot to {_screenshotPath}" : $"Screenshot failed: {error}");
            SimSession.Current?.Dispose();
            GetTree().Quit();
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (_session == null) return; // start menu
        switch (@event)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true }:
                if (_placement.Active != null) PlaceBuilding();
                else if (_roadTool.Active)
                {
                    var road = _roadTool.Click();
                    Report(road, "Can't build road");
                    if (road is { Ok: true }) _sound.Placed();
                    else if (road == null) _sound.Click();
                }
                else if (_wallTool.Active)
                {
                    var wall = _wallTool.Click();
                    Report(wall, "Can't build wall");
                    if (wall is { Ok: true }) _sound.Placed();
                    else if (wall == null) _sound.Click();
                }
                else if (_territoryTool.Active) _territoryTool.BeginStroke(Input.IsKeyPressed(Key.Shift));
                else if (_demolishTool.Active)
                {
                    var torn = _demolishTool.Click();
                    Report(torn, "Can't demolish");
                    if (torn is { Ok: true }) _sound.Demolished();
                }
                else Select();
                break;

            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false }:
                _territoryTool.EndStroke();
                break;

            case InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true }:
            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape }:
                if (AnyToolActive) CancelTools();
                else ClearSelection();
                break;

            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.R }:
                StartRoadTool();
                break;

            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.V }:
                StartWallTool(_wallTool.Active && !_wallTool.GateMode ? (byte)((_wallTool.Tier + 1) % _session.World.Data.WallTiers.Count) : (byte)0, null);
                break;

            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.G }:
                // G: gate; pressed again, gatehouse.
                StartWallTool(0, _wallTool.Active && _wallTool.Piece == WallPiece.Gate ? WallPiece.Gatehouse : WallPiece.Gate);
                break;

            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.B }:
                StartWallTool(0, WallPiece.Tower);
                break;

            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.X }:
                StartDemolishTool();
                break;

            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.T }:
                StartTerritoryTool();
                break;

            case InputEventKey { Pressed: true, Keycode: Key.Bracketleft } when _territoryTool.Active:
                _territoryTool.ResizeBrush(-1);
                break;

            case InputEventKey { Pressed: true, Keycode: Key.Bracketright } when _territoryTool.Active:
                _territoryTool.ResizeBrush(+1);
                break;

            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.P }:
                ShowOnly(_rules, _rules.Toggle);
                break;

            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.K }:
                ToggleArmy();
                break;

            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.J }:
                ToggleTrade();
                break;

            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.L }:
                ToggleLedger();
                break;

            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.M }:
                _sound.ToggleMute();
                Flash(_sound.Muted ? "Sound off (M)" : "Sound on");
                break;

            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.F5 }:
                SaveGame();
                break;

            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.F9 }:
                LoadGame();
                break;

            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.Space }:
                if (_session.CanPause) _session.Paused = !_session.Paused;
                break;

            case InputEventKey { Pressed: true, Echo: false } key
                when key.Keycode >= Key.Key1 && key.Keycode < Key.Key1 + SimSession.Speeds.Length:
                double requested = SimSession.Speeds[key.Keycode - Key.Key1];
                if (!_session.CanChangeSpeed || requested > _session.MaxSpeed) break;
                _session.Speed = requested;
                _session.Paused = false;
                break;
        }
    }

    /// <summary>The rules, trade, ledger and army panels share the left side of the screen: one at a time.</summary>
    private void ShowOnly(Control panel, Action toggle)
    {
        if (_rules != panel && _rules.Visible) _rules.Toggle();
        if (_trade != panel && _trade.Visible) _trade.Toggle();
        if (_ledger != panel && _ledger.Visible) _ledger.Toggle();
        if (_army != panel && _army.Visible) _army.Toggle();
        toggle();
    }

    private void ToggleTrade() => ShowOnly(_trade, _trade.Toggle);
    private void ToggleLedger() => ShowOnly(_ledger, _ledger.Toggle);
    private void ToggleArmy() => ShowOnly(_army, _army.Toggle);

    private bool AnyToolActive => _placement.Active != null || _roadTool.Active || _wallTool.Active || _demolishTool.Active || _territoryTool.Active;

    private void CancelTools()
    {
        _placement.Cancel();
        _roadTool.Cancel();
        _wallTool.Cancel();
        _demolishTool.Cancel();
        _territoryTool.Cancel();
    }

    private void StartTerritoryTool()
    {
        CancelTools();
        ClearSelection();
        _territoryTool.Begin();
    }

    private void StartRoadTool()
    {
        CancelTools();
        ClearSelection();
        _roadTool.Begin();
    }

    /// <summary>V picks a palisade, and pressing it again moves up to stone and large stone walls.</summary>
    private void StartWallTool(byte tier, WallPiece? piece)
    {
        CancelTools();
        ClearSelection();
        _wallTool.Begin(tier, piece);
    }

    /// <summary>
    /// For screenshots: a stone wall and a large stone wall near the centre, each with a tower and a gatehouse,
    /// placed with the same commands a player uses and finished at once. The camera looks at the first.
    /// </summary>
    /// <summary>Screenshots: raise a squad of each kind the town can and put a warband in sight, close enough to muster.</summary>
    private void DemoRaid()
    {
        var world = _session.World;
        var town = _session.PlayerTown;
        town.Stock.Add("weapons", 30);
        town.Stock.Add("bows", 10);
        town.Stock.Add("armour", 10);
        if (!town.Buildings.Any(b => b.Def.Id == "barracks"))
        {
            town.Stock.Add("wood", 60);
            town.Stock.Add("stone", 60);
            var def = world.Data.Building("barracks");
            if (Sim.Core.AI.SiteFinder.Find(world, town, def) is { } site &&
                _session.Execute(new PlaceBuildingCommand(town.Id, def.Id, site.X, site.Y)) is { Ok: true, CreatedId: int id } &&
                town.FindBuilding(id) is { } barracks)
            {
                barracks.State = BuildingState.UnderConstruction;
                Sim.Core.Systems.ConstructionSystem.Complete(world, town, barracks);
            }
        }
        if (town.General == null && MilitaryGovernor.GeneralCandidate(town) is { } general)
            _session.Execute(new PromoteGeneralCommand(town.Id, general.Id));
        foreach (string unit in new[] { "spearmen", "archers", "men_at_arms" })
            _session.Execute(new RecruitSquadCommand(town.Id, unit));
        // Demo only (single-player): a warband just inside the muster radius, marching in.
        double d = Raids.MusterRadius(town) - 1;
        var band = new Warband { Id = world.NextId(), TargetId = town.Id, X = town.CenterX + d * 0.6, Y = town.CenterY + d, Raiders = 24, Archers = 9 };
        world.War.Warbands.Add(band);
        Raids.Daily(world);
        if (SimSession.UserArg("--look-at") == null) _camera.LookAtTile((int)band.X, (int)band.Y);
    }

    /// <summary>
    /// Screenshots: a big band camped before the town's walls with a ram and catapult built and a trebuchet going
    /// up, and the stretch of wall they batter half knocked down. Needs a town that has ringed itself with walls
    /// (e.g. --warmup-days 330).
    /// </summary>
    private void DemoSiege()
    {
        var world = _session.World;
        var town = _session.PlayerTown;
        var outer = Siege.OuterWalls(world, town);
        if (outer.Count == 0)
        {
            Flash("No closed ring of walls to besiege yet: warm up longer");
            return;
        }
        // The outer wall tile furthest from the centre along +Y, and a band camped beyond it.
        var target = outer.OrderByDescending(t => t.Y - town.CenterY).ThenBy(t => Math.Abs(t.X - town.CenterX)).First();
        double r = Raids.ArriveRadius(town) - 1;
        var band = new Warband
        {
            Id = world.NextId(), TargetId = town.Id, X = target.X, Y = town.CenterY + r, Raiders = 34, Archers = 12,
        };
        world.War.Warbands.Add(band);
        Siege.Begin(world, town, band);
        band.SiegeDays = 9;
        band.Engines.Add(new SiegeEngine { Kind = "ram", Progress = 1 });
        band.Engines.Add(new SiegeEngine { Kind = "catapult", Progress = 1 });
        band.Engines.Add(new SiegeEngine { Kind = "trebuchet", Progress = 0.5 });
        band.HasTarget = true;
        band.TargetX = target.X;
        band.TargetY = target.Y;
        foreach (var (pos, wall) in town.Walls.Where(kv => Math.Abs(kv.Key.X - target.X) + Math.Abs(kv.Key.Y - target.Y) <= 2).ToList())
            wall.Hp = Math.Max(1, wall.Hp * (pos == target ? 2 : 5) / 10);
        town.Alert = Alert.CallToArms; // for the banner (the real alert follows at the next day)
        if (SimSession.UserArg("--look-at") == null) _camera.LookAtTile(target.X, target.Y + 7);
    }

    private void DemoWalls()
    {
        var world = _session.World;
        var town = _session.PlayerTown;
        town.Stock.Add("stone", 2000);
        town.Stock.Add("wood", 500);
        List<(int X, int Y)>? FreeLine(int length, int fromDy)
        {
            for (int dy = fromDy; dy < fromDy + 40; dy++)
            for (int dx = -30; dx < 30; dx++)
            {
                var line = Enumerable.Range(0, length).Select(i => (town.CenterX + dx + i, town.CenterY + dy)).ToList();
                bool clear = line.All(t => Walls.Check(world, town, t.Item1, t.Item2) == null &&
                                           world.Map.GetTile(t.Item1, t.Item2) is { IsRoad: false, RoadPlanned: false, WallTier: 0, WallPlanned: false } &&
                                           world.Map.GetTile(t.Item1, t.Item2 + 1).BuildingId == 0 && world.Map.GetTile(t.Item1, t.Item2 - 1).BuildingId == 0);
                if (clear) return line;
            }
            return null;
        }
        (int X, int Y)? first = null;
        foreach (byte tier in new byte[] { 1, 2 })
        {
            if (FreeLine(9, tier == 1 ? 3 : 12) is not { } line) continue;
            _session.Execute(new PlaceWallCommand(town.Id, line, tier));
            _session.Execute(new PlaceTowerCommand(town.Id, line[2].X, line[2].Y));
            _session.Execute(new PlaceGateCommand(town.Id, line[6].X, line[6].Y));
            Walls.FinishAllPlans(world, town);
            _session.Execute(new PlaceGatehouseCommand(town.Id, line[6].X, line[6].Y));
            Walls.FinishAllPlans(world, town);
            first ??= line[4];
        }
        if (first is { } f) _camera.LookAtTile(f.X, f.Y);
    }

    private void StartDemolishTool()
    {
        CancelTools();
        ClearSelection();
        _demolishTool.Begin();
    }

    private void SaveGame()
    {
        if (!_session.CanSave)
        {
            Flash("Only the host saves a shared world.");
            return;
        }
        try
        {
            string path = _session.Save();
            Flash($"Saved to {path}");
        }
        catch (Exception e)
        {
            Flash($"Save failed: {e.Message}");
        }
    }

    private void LoadGame()
    {
        if (_session.Mode != SessionMode.Local)
        {
            Flash("Loading is for single-player worlds. Leave this shared world first.");
            return;
        }
        if (!SimSession.HasSave)
        {
            Flash("No save yet (press F5 to save)");
            return;
        }
        try
        {
            var loaded = new SimSession(SimSession.LoadSaved());
            _session.Dispose();
            SimSession.Current = loaded;
            _justLoaded = true;
            GetTree().ReloadCurrentScene();
        }
        catch (Exception e)
        {
            Flash($"Load failed: {e.Message}");
        }
    }

    private void Report(CommandResult? result, string failurePrefix)
    {
        if (result is { Ok: false, Error: { } error }) Flash($"{failurePrefix}: {error}");
    }

    private void PlaceBuilding()
    {
        var result = _placement.TryPlace();
        if (result is { Ok: true }) _sound.Placed();
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
            _inspector.ShowBuilding(_session.World, town, b);
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
