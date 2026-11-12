using Godot;
using Sim.Core.Data;
using Sim.Core.Events;
using Sim.Core.Settlements;
using Sim.Core.Systems;
using Sim.Core.Time;

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
    private CheckBox _autoTerritory = null!;

    public event Action<string>? BuildRequested;
    public event Action? RoadRequested;
    public event Action<byte>? RoadTierRequested;
    public event Action? DemolishRequested;
    public event Action<double>? SpeedRequested;
    public event Action? PauseToggled;
    public event Action<bool>? AutoBuildToggled;
    public event Action<bool>? AutoTerritoryToggled;
    public event Action? TerritoryRequested;
    public event Action? ExpandTerritoryRequested;
    public event Action? RulesRequested;
    public event Action? TradeRequested;
    public event Action? SaveRequested;
    public event Action? LoadRequested;
    public event Action? SoundToggled;

    /// <summary>Raised whenever any HUD button is pressed (for the click sound).</summary>
    public event Action? ButtonClicked;

    /// <param name="speeds">Speed multipliers to offer (relative to the world's normal speed).</param>
    /// <param name="canChangeSpeed">False when the world's speed is fixed or someone else controls it.</param>
    public void Build(GameData data, bool autoBuild, bool autoTerritory, IReadOnlyList<double> speeds, bool canChangeSpeed, bool canPause)
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

        _pause = new Button
        {
            Text = "Pause", ToggleMode = true, FocusMode = Control.FocusModeEnum.None, Disabled = !canPause,
            TooltipText = canPause ? "Space" : "Shared worlds can't be paused",
        };
        _pause.Pressed += () => PauseToggled?.Invoke();
        topRow.AddChild(_pause);
        int key = 1;
        foreach (double speed in speeds)
        {
            var b = new Button
            {
                Text = $"{speed}x", ToggleMode = true, FocusMode = Control.FocusModeEnum.None, Disabled = !canChangeSpeed,
                TooltipText = canChangeSpeed
                    ? $"Key {key++}: {speed}x the world's normal speed"
                    : "This world's speed is fixed or set by the host",
            };
            double s = speed;
            b.Pressed += () => SpeedRequested?.Invoke(s);
            _speedButtons[speed] = b;
            topRow.AddChild(b);
        }

        _autoBuild = new CheckBox
        {
            Text = "Auto",
            ButtonPressed = autoBuild,
            TooltipText = "Let the settlement place buildings on its own",
            FocusMode = Control.FocusModeEnum.None,
        };
        _autoBuild.Toggled += on => AutoBuildToggled?.Invoke(on);
        topRow.AddChild(_autoBuild);

        _autoTerritory = new CheckBox
        {
            Text = "Auto land",
            ButtonPressed = autoTerritory,
            TooltipText = "Spend spare territory allowance evenly around the town centre each day.\nTurn off to paint territory yourself (T).",
            FocusMode = Control.FocusModeEnum.None,
        };
        _autoTerritory.Toggled += on => AutoTerritoryToggled?.Invoke(on);
        topRow.AddChild(_autoTerritory);

        foreach (var (text, tip, action) in new (string, string, Action)[]
                 {
                     ("Rules", "Job priorities, stock limits and reserves (P)", () => RulesRequested?.Invoke()),
                     ("Trade", "Offers, trade routes and caravans (J). Needs a Trading Post.", () => TradeRequested?.Invoke()),
                     ("Save", "Quick-save (F5)", () => SaveRequested?.Invoke()),
                     ("Load", "Load the quick-save (F9)", () => LoadRequested?.Invoke()),
                     ("Sound", "Sound on/off (M)", () => SoundToggled?.Invoke()),
                 })
        {
            var b = new Button { Text = text, TooltipText = tip, FocusMode = Control.FocusModeEnum.None };
            b.Pressed += action;
            topRow.AddChild(b);
        }

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
        for (int t = 1; t < data.RoadTiers.Count; t++)
        {
            var tier = data.RoadTiers[t];
            byte index = (byte)t;
            string price = string.Join(", ", tier.Cost.Select(kv => $"{kv.Value} {kv.Key}"));
            ToolButton($"{tier.Name}\n{price}/tile", $"Lay a {tier.Name.ToLowerInvariant()} or resurface roads ({price} per tile). " +
                "Walkers and haulers move faster on it, and caravans travel faster.", () => RoadTierRequested?.Invoke(index));
        }
        ToolButton("Demolish\nrefund", "Tear down buildings or roads (X). Refunds part of the cost.", () => DemolishRequested?.Invoke());
        ToolButton("Territory\npaint", "Paint where your territory goes (T): drag to claim, Shift+drag to give land back, [ ] brush size.\n" +
            "Your allowance grows with population, up to the cap of your town centre (upgrade it to raise the cap).",
            () => TerritoryRequested?.Invoke());
        ToolButton("Territory\nexpand evenly", "Claim all spare territory allowance evenly around the town centre.",
            () => ExpandTerritoryRequested?.Invoke());

        foreach (var def in data.Buildings.Values)
        {
            if (def.IsTownCentre) continue; // upgraded from the inspector, never placed
            if (def.Home is { Level: > 0 }) continue; // hovels become cottages and townhouses on their own
            string cost = def.Cost.Count == 0 ? "free" : string.Join(", ", def.Cost.Select(kv => $"{kv.Value} {kv.Key}"));
            var b = new Button
            {
                Text = $"{def.Name}\n{cost}",
                TooltipText = Describe(data, def),
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
            Text = "WASD/edge/middle-drag: pan   Wheel: zoom   Q/E: rotate   R: roads   X: demolish   T: territory   P: rules   J: trade\nLeft-click: select/place   Right-click/Esc: cancel   Space: pause   1-4: speed   F5/F9: save/load",
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

    /// <summary>Hooks every button and checkbox under <paramref name="root"/> to <see cref="ButtonClicked"/>.</summary>
    public void ConnectClickSounds(Node root)
    {
        foreach (var child in root.GetChildren())
        {
            if (child is BaseButton button) button.Pressed += () => ButtonClicked?.Invoke();
            ConnectClickSounds(child);
        }
    }

    private static string Describe(GameData data, BuildingDef def)
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
        if (def.TownCentre is { } centre) lines.Add($"Town centre: territory up to {centre.TerritoryCap} tiles; its roadworkers build roads");
        if (def.Home is { UpgradeTo: { } up } home)
            lines.Add($"Home for {home.Class}s; becomes a {data.Building(up).Name.ToLowerInvariant()} once its people have " +
                      string.Join(", ", data.Building(up).Home!.Requires) + " (taxes rise with each step)");
        if (def.Upkeep > 0) lines.Add($"Upkeep {def.Upkeep:0.#} coin a day (half output while the treasury can't pay)");
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
        string where = session.Mode switch
        {
            SessionMode.Host => $"  ·  hosting :{session.HostPort}",
            SessionMode.Join => "  ·  online",
            _ => "",
        };
        string behind = session.TicksBehind > TimeScale.TicksPerDay ? $"  (catching up {session.TicksBehind / TimeScale.TicksPerDay} days)" : "";
        _date.Text = $"{town.Name}  ·  {world.Time}{where}\nMood {moodNow:0}/100  ·  {session.Speed:0.#}x{behind}";

        int adults = town.Citizens.Count(c => c.IsAdult);
        int idle = town.Citizens.Count(c => c.IsAdult && c.JobBuildingId == null);
        int homeless = town.Citizens.Count(c => c.HomeBuildingId == null);
        int haulers = town.Buildings.Where(b => b.CurrentJobKind == JobKinds.Hauler).Sum(b => b.WorkerIds.Count);
        _stock.Text =
            $"Pop {town.Citizens.Count} ({adults} adults, {haulers} hauling, {idle} idle{(homeless > 0 ? $", {homeless} homeless" : "")})   " +
            $"Food {Needs.FoodDays(world, town):0.0} days (grain {town.Stock.Get("grain")}, bread {town.Stock.Get("bread")}, " +
            $"veg {town.Stock.Get("vegetables")})   Ale {town.Stock.Get("ale")}   " +
            $"Coin {town.Stock.Get("coin")} ({EconomySystem.DailyTax(world, town) - EconomySystem.DailyUpkeep(town):+0.#;-0.#}/day" +
            (town.UpkeepUnpaid ? ", UPKEEP UNPAID" : "") + $")   Wood {town.Stock.Get("wood")}   Stone {town.Stock.Get("stone")}   Tools {town.Stock.Get("tools")}   " +
            $"Cloth {town.Stock.Get("cloth")}   Furniture {town.Stock.Get("furniture")}\n" +
            $"Storage {town.Stock.Total}/{town.StorageCapacity}   Land {town.Territory.Count}/{town.TerritoryAllowance} " +
            $"(cap {town.TerritoryCap})   Needs met: " +
            string.Join("   ", world.Data.Needs.Select(n => $"{n.Name} {ServiceSystem.Coverage(town, n.Id):P0}"));

        _pause.SetPressedNoSignal(session.Paused);
        foreach (var (speed, button) in _speedButtons)
            button.SetPressedNoSignal(!session.Paused && Math.Abs(session.Speed - speed) < 0.001);
        _autoBuild.SetPressedNoSignal(town.Rules.AutoBuild);
        _autoTerritory.SetPressedNoSignal(town.Rules.AutoExpandTerritory);
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

    /// <summary>The event feed shares the left side of the screen with the rules panel.</summary>
    public bool FeedVisible
    {
        set => _feedLabel.Visible = value;
    }
}
