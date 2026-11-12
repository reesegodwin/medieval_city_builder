using Godot;
using Sim.Core.Data;
using Sim.Core.Events;
using Sim.Core.Systems;

namespace MedievalClient.Ui;

/// <summary>Top bar (date, stock, speed), build menu, event feed and placement status, built in code.</summary>
public partial class Hud : CanvasLayer
{
    private const int MaxFeedLines = 9;

    private readonly Queue<string> _feed = new();
    private readonly Dictionary<double, Button> _speedButtons = new();
    private Label _date = null!;
    private Label _stock = null!;
    private Label _feedLabel = null!;
    private Label _status = null!;
    private Button _pause = null!;
    private CheckBox _autoBuild = null!;

    public event Action<string>? BuildRequested;
    public event Action? RoadRequested;
    public event Action? DemolishRequested;
    public event Action<double>? SpeedRequested;
    public event Action? PauseToggled;
    public event Action<bool>? AutoBuildToggled;

    public void Build(GameData data, bool autoBuild)
    {
        // --- Top bar ---
        var top = new PanelContainer();
        top.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopWide);
        AddChild(top);
        var topRow = new HBoxContainer();
        topRow.AddThemeConstantOverride("separation", 18);
        top.AddChild(topRow);

        _date = new Label { CustomMinimumSize = new Vector2(220, 0) };
        _stock = new Label { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipText = true };
        _stock.AddThemeFontSizeOverride("font_size", 14);
        topRow.AddChild(_date);
        topRow.AddChild(_stock);

        _pause = new Button { Text = "Pause", ToggleMode = true, TooltipText = "Space", FocusMode = Control.FocusModeEnum.None };
        _pause.Pressed += () => PauseToggled?.Invoke();
        topRow.AddChild(_pause);
        int key = 1;
        foreach (double speed in SimSession.Speeds)
        {
            var b = new Button { Text = $"{speed}x", ToggleMode = true, TooltipText = $"Key {key++}", FocusMode = Control.FocusModeEnum.None };
            double s = speed;
            b.Pressed += () => SpeedRequested?.Invoke(s);
            _speedButtons[speed] = b;
            topRow.AddChild(b);
        }

        _autoBuild = new CheckBox
        {
            Text = "Auto-build",
            ButtonPressed = autoBuild,
            TooltipText = "Let the settlement place buildings on its own",
            FocusMode = Control.FocusModeEnum.None,
        };
        _autoBuild.Toggled += on => AutoBuildToggled?.Invoke(on);
        topRow.AddChild(_autoBuild);

        // --- Build menu (bottom) ---
        var bottom = new PanelContainer();
        bottom.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomWide);
        bottom.GrowVertical = Control.GrowDirection.Begin;
        AddChild(bottom);
        var buildRow = new HFlowContainer();
        buildRow.AddThemeConstantOverride("h_separation", 4);
        buildRow.AddThemeConstantOverride("v_separation", 4);
        bottom.AddChild(buildRow);

        Button ToolButton(string text, string tooltip, Action pressed)
        {
            var b = new Button
            {
                Text = text,
                TooltipText = tooltip,
                CustomMinimumSize = new Vector2(96, 44),
                FocusMode = Control.FocusModeEnum.None,
            };
            b.AddThemeColorOverride("font_color", new Color(1f, 0.9f, 0.6f));
            b.Pressed += pressed;
            buildRow.AddChild(b);
            return b;
        }
        ToolButton("Roads\nbridges: wood", "Lay roads (R). Free on land; over water they become bridges (4 wood per tile). Roads make hauling faster; people walk along them.", () => RoadRequested?.Invoke());
        ToolButton("Demolish\nrefund", "Tear down buildings or roads (X). Refunds part of the cost.", () => DemolishRequested?.Invoke());

        foreach (var def in data.Buildings.Values)
        {
            string cost = def.Cost.Count == 0 ? "free" : string.Join(", ", def.Cost.Select(kv => $"{kv.Value} {kv.Key}"));
            var b = new Button
            {
                Text = $"{def.Name}\n{cost}",
                TooltipText = Describe(def),
                CustomMinimumSize = new Vector2(104, 44),
                FocusMode = Control.FocusModeEnum.None,
            };
            string id = def.Id;
            b.Pressed += () => BuildRequested?.Invoke(id);
            buildRow.AddChild(b);
        }

        // --- Event feed and status (bottom-left, above the build menu) ---
        var feedBox = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        feedBox.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomLeft);
        feedBox.OffsetTop = -330;
        feedBox.OffsetBottom = -112;
        feedBox.OffsetLeft = 12;
        feedBox.OffsetRight = 560;
        feedBox.GrowVertical = Control.GrowDirection.Begin;
        AddChild(feedBox);

        _feedLabel = new Label
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            VerticalAlignment = VerticalAlignment.Bottom,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        _feedLabel.AddThemeColorOverride("font_shadow_color", Colors.Black);
        _feedLabel.AddThemeConstantOverride("shadow_offset_x", 1);
        _feedLabel.AddThemeConstantOverride("shadow_offset_y", 1);
        feedBox.AddChild(_feedLabel);

        _status = new Label { MouseFilter = Control.MouseFilterEnum.Ignore };
        _status.AddThemeColorOverride("font_color", new Color(1f, 0.9f, 0.5f));
        _status.AddThemeColorOverride("font_shadow_color", Colors.Black);
        _status.AddThemeConstantOverride("shadow_offset_x", 1);
        _status.AddThemeConstantOverride("shadow_offset_y", 1);
        feedBox.AddChild(_status);

        // --- Controls help (top-right, under the bar) ---
        var help = new Label
        {
            Text = "WASD/edge/middle-drag: pan   Wheel: zoom   Q/E: rotate   R: roads   X: demolish\nLeft-click: select/place   Right-click/Esc: cancel   Space: pause   1-4: speed",
            HorizontalAlignment = HorizontalAlignment.Right,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        help.AddThemeFontSizeOverride("font_size", 12);
        help.AddThemeColorOverride("font_shadow_color", Colors.Black);
        help.AddThemeConstantOverride("shadow_offset_x", 1);
        help.AddThemeConstantOverride("shadow_offset_y", 1);
        help.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopRight);
        help.OffsetTop = 64;
        help.OffsetLeft = -560;
        help.OffsetRight = -12;
        AddChild(help);
    }

    private static string Describe(BuildingDef def)
    {
        var lines = new List<string> { def.Name, $"Size {def.Width}x{def.Height}, {def.BuildDays:0.##} worker-days to build" };
        if (def.Housing > 0) lines.Add($"Houses {def.Housing}");
        if (def.Storage > 0) lines.Add($"Stores {def.Storage} goods");
        if (def.Recipe is { } r)
        {
            string inputs = r.Inputs.Count == 0 ? "" : string.Join(", ", r.Inputs.Select(kv => $"{kv.Value} {kv.Key}")) + " -> ";
            string outputs = string.Join(", ", r.Outputs.Select(kv => $"{kv.Value} {kv.Key}"));
            lines.Add($"{def.Jobs} x {def.JobKind}: {inputs}{outputs} per worker-day");
        }
        if (def.FertileBonus > 0) lines.Add($"+{def.FertileBonus:P0} output on fertile soil");
        if (def.NearTerrain is { } near) lines.Add($"Needs {near.Terrain} within {near.Radius} tiles");
        if (def.Service is { } service)
        {
            lines.Add($"Serves {service.Need} to up to {service.Capacity} people within {service.Radius} tiles of their home");
            foreach (var (resource, perPerson) in service.ConsumesPerPersonDay)
                lines.Add($"Uses {perPerson:0.##} {resource} per person per day");
        }
        return string.Join("\n", lines);
    }

    public void Refresh(SimSession session)
    {
        var world = session.World;
        var town = session.PlayerTown;
        double moodNow = town.Citizens.Count > 0 ? town.Citizens.Average(c => c.Mood) : 0;
        _date.Text = $"{town.Name}  ·  {world.Time}\nMood {moodNow:0}/100";

        int adults = town.Citizens.Count(c => c.IsAdult);
        int idle = town.Citizens.Count(c => c.IsAdult && c.JobBuildingId == null);
        int homeless = town.Citizens.Count(c => c.HomeBuildingId == null);
        _stock.Text =
            $"Pop {town.Citizens.Count} ({adults} adults, {idle} idle{(homeless > 0 ? $", {homeless} homeless" : "")})   " +
            $"Food {Needs.FoodDays(world, town):0.0} days (grain {town.Stock.Get("grain")}, bread {town.Stock.Get("bread")}, " +
            $"veg {town.Stock.Get("vegetables")})   Ale {town.Stock.Get("ale")}   " +
            $"Wood {town.Stock.Get("wood")}   Stone {town.Stock.Get("stone")}\n" +
            $"Storage {town.Stock.Total}/{town.StorageCapacity}   Needs met: " +
            string.Join("   ", world.Data.Needs.Select(n => $"{n.Name} {ServiceSystem.Coverage(town, n.Id):P0}"));

        _pause.SetPressedNoSignal(session.Paused);
        foreach (var (speed, button) in _speedButtons)
            button.SetPressedNoSignal(!session.Paused && Math.Abs(session.Speed - speed) < 0.001);
        _autoBuild.SetPressedNoSignal(town.Rules.AutoBuild);
    }

    public void AddEvents(IEnumerable<GameEvent> events)
    {
        bool changed = false;
        foreach (var e in events)
        {
            if (e.Kind == EventKind.BuildingPlaced) continue;
            _feed.Enqueue($"{e.Time}: {e.Message}");
            while (_feed.Count > MaxFeedLines) _feed.Dequeue();
            changed = true;
        }
        if (changed) _feedLabel.Text = string.Join("\n", _feed);
    }

    public void SetStatus(string? text) => _status.Text = text ?? "";
}
