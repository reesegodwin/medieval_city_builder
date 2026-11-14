using Godot;
using Sim.Core.Settlements;
using Sim.Core.Simulation;
using Sim.Core.War;

namespace MedievalClient.Ui;

/// <summary>
/// The town's army (K): the alert level and warbands in sight (counts as rough as the scouts can tell), the general,
/// squads with recruit and disband buttons, gear in stock, raid and muster rules, and recent battles.
/// </summary>
public partial class ArmyPanel : PanelContainer
{
    private SimSession _session = null!;
    private VBoxContainer _body = null!;
    private Label _note = null!;
    private string _shown = "";

    public void Initialize(SimSession session) => _session = session;

    private GameWorld World => _session.World;
    private Settlement Town => _session.PlayerTown;

    public override void _Ready()
    {
        AnchorLeft = 0;
        AnchorRight = 0;
        AnchorTop = 0;
        AnchorBottom = 1;
        OffsetLeft = 10;
        OffsetRight = 480;
        OffsetTop = 108;
        OffsetBottom = -180;
        Visible = false;

        var column = new VBoxContainer();
        AddChild(column);
        var header = new HBoxContainer();
        column.AddChild(header);
        var title = new Label { Text = "Army", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        title.AddThemeFontSizeOverride("font_size", 20);
        header.AddChild(title);
        var close = new Button { Text = "X", TooltipText = "Close (K)", FocusMode = FocusModeEnum.None };
        close.Pressed += () => Visible = false;
        header.AddChild(close);

        _note = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _note.AddThemeColorOverride("font_color", new Color(1f, 0.85f, 0.55f));
        column.AddChild(_note);

        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        column.AddChild(scroll);
        _body = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _body.AddThemeConstantOverride("separation", 4);
        scroll.AddChild(_body);
    }

    public void Toggle()
    {
        Visible = !Visible;
        if (Visible)
        {
            _shown = "";
            Refresh();
        }
    }

    /// <summary>Rebuilds the panel when what it shows has changed.</summary>
    public void Refresh()
    {
        if (!Visible) return;
        var s = Town;
        var war = World.Data.War;
        string signature = string.Join("|",
            s.Alert, s.Rules.Raids, s.Rules.MusterPercent, s.General?.CitizenId, s.General?.Battles,
            string.Join(",", s.Squads.Select(q => $"{q.Id}:{q.SoldierIds.Count}:{(int)q.Xp}")),
            string.Join(",", war.Units.SelectMany(u => u.Gear.Keys).Distinct().Select(g => s.Stock.Get(g))),
            string.Join(",", World.War.Warbands.Where(w => InSight(World, s, w)).Select(w => Raids.Describe(s, w) + (int)Raids.Distance(s, w))),
            World.War.Reports.Count, Armies.SquadCapacity(World, s), Armies.SpareHands(s), SiegeSignature(s),
            s.Stock.Get(Sim.Core.Economy.Inventory.Coin) / 50);
        if (signature == _shown) return;
        _shown = signature;
        Build();
    }

    /// <summary>Warbands the town's watch (or an ally's) can see (fog of war).</summary>
    public static bool InSight(Sim.Core.Simulation.GameWorld world, Settlement s, Warband w) => Raids.Sees(world, s, w);

    private void Build()
    {
        foreach (var child in _body.GetChildren()) child.QueueFree();
        var s = Town;
        var war = World.Data.War;

        // --- Alert and threats ---
        if (Siege.BandAt(World, s) is { } besiegers) BuildSiege(s, besiegers);
        else
        {
            Section(AlertText(s.Alert), AlertHelp(s.Alert), AlertColor(s.Alert));
            foreach (var band in World.War.Warbands.Where(w => InSight(World, s, w)))
                Line($"• {Capitalize(Raids.Describe(s, band))}, {Raids.Distance(s, band):0} tiles out");
        }

        // --- General ---
        Section("General", "Leads the town's soldiers. Traits shape battles; skill grows with every battle fought.");
        if (s.General is { } general && s.FindCitizen(general.CitizenId) is { } leader)
        {
            string traits = string.Join(", ", general.Traits.Select(t => war.Trait(t)?.Name ?? t));
            Line($"{leader.FullName}, skill {general.Skill:0}, {general.Battles} battles. Traits: {traits}");
        }
        else Line("No general.");
        if (MilitaryGovernor.GeneralCandidate(s) is { } candidate)
            ActionButton(s.General == null ? $"Appoint {candidate.FullName}" : $"Replace with {candidate.FullName}",
                "The most experienced adult under 50 who isn't serving. The old general goes back to work.",
                () => _session.Execute(new PromoteGeneralCommand(s.Id, candidate.Id)));

        // --- Squads ---
        int regular = s.Squads.Count(q => !q.Levy);
        int capacity = Armies.SquadCapacity(World, s);
        Section($"Squads ({regular}/{capacity} in barracks, {Armies.Soldiers(s)} soldiers)",
            "Soldiers keep their homes and food but don't work or pay tax, and cost upkeep. Levies are raised by the muster " +
            "and go home when the danger has passed.");
        foreach (var squad in s.Squads)
        {
            var unit = war.Unit(squad.UnitId);
            var row = new HBoxContainer();
            row.AddChild(new Label
            {
                Text = $"{unit?.Name ?? squad.UnitId}: {squad.SoldierIds.Count}{(squad.Levy ? " (levy)" : "")}, xp {squad.Xp:0}",
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            });
            int id = squad.Id;
            var disband = new Button { Text = "Disband", FocusMode = FocusModeEnum.None, TooltipText = "Back to work; gear returns to stock" };
            disband.Pressed += () => Report(_session.Execute(new DisbandSquadCommand(s.Id, id)), "Squad stood down");
            row.AddChild(disband);
            _body.AddChild(row);
        }

        // --- Recruit ---
        Section("Recruit", capacity == 0
            ? "Build a barracks to train squads (each holds " + war.SquadsPerBarracks + ")."
            : $"A squad is {war.SquadSize} people: the jobless and haulers first, service workers last. Spare hands now: {Armies.SpareHands(s)}.");
        foreach (var unit in war.Units.Where(u => !u.Raider))
        {
            string gear = unit.Gear.Count == 0 ? "no gear" : string.Join(" + ", unit.Gear.Select(kv => $"{kv.Value * war.SquadSize} {kv.Key}"));
            string id = unit.Id;
            ActionButton($"{unit.Name} ({gear}, {unit.PayPerSoldierDay * war.SquadSize:0.#} coin/day)",
                $"{unit.Description}\nAttack {unit.Attack:0.#}, defence {unit.Defence:0.#}{(unit.Ranged ? ", ranged" : "")}." +
                (unit.Gear.Count > 0 ? $"\nOnly recruits whose families have {Sim.Core.Economy.HouseholdSystem.GearPrice(World, unit):0.#} coin saved take up this gear." : ""),
                () => Report(_session.Execute(new RecruitSquadCommand(s.Id, id)), $"Raised a squad of {unit.Name.ToLowerInvariant()}"));
        }
        Line("Gear in stock: " + string.Join(", ", war.Units.SelectMany(u => u.Gear.Keys).Distinct()
            .Select(g => $"{s.Stock.Get(g)} {World.Data.Resources.GetValueOrDefault(g)?.Name.ToLowerInvariant() ?? g}")));

        // --- Rules ---
        Section("Raids and the muster", "Bandits march on towns now and then. When they come close, the muster arms this share " +
                                        "of adults as militia (twice that at a call to arms).");
        var raids = new CheckBox { Text = "Bandit raids", ButtonPressed = s.Rules.Raids, FocusMode = FocusModeEnum.None };
        raids.Toggled += on => _session.Execute(new SetRaidsCommand(s.Id, on));
        _body.AddChild(raids);
        var muster = new HBoxContainer();
        muster.AddChild(new Label { Text = "Muster % of adults", SizeFlagsHorizontal = SizeFlags.ExpandFill });
        var percent = new SpinBox { MinValue = 0, MaxValue = 80, Step = 5, Value = s.Rules.MusterPercent, CustomMinimumSize = new Vector2(110, 0) };
        percent.ValueChanged += v => _session.Execute(new SetMusterCommand(s.Id, (int)v));
        muster.AddChild(percent);
        _body.AddChild(muster);

        // --- Battles ---
        var reports = World.War.Reports.Where(r => r.SettlementId == s.Id).Reverse().Take(6).ToList();
        if (reports.Count > 0)
        {
            Section("Recent battles", "");
            foreach (var r in reports)
                Line($"Day {r.Day}: {r.Summary}", r.DefendersWon ? new Color(0.7f, 0.95f, 0.7f) : new Color(1f, 0.6f, 0.55f));
        }
    }

    private string SiegeSignature(Settlement s) => Siege.BandAt(World, s) is { } b
        ? $"{b.SiegeDays}:{b.Size}:{string.Join(",", b.Engines.Select(e => $"{e.Kind}{(int)(e.Progress * 10)}h{e.Hp}{e.InPlace}"))}:" +
          (b.HasTarget && s.Walls.TryGetValue((b.TargetX, b.TargetY), out var w) ? w.Hp : -1) + $":{b.LastSallyDay}"
        : "";

    /// <summary>The siege: how long, the engines, the wall under attack, and what the town can do about it.</summary>
    private void BuildSiege(Settlement s, Warband band)
    {
        var rules = World.Data.War.Siege;
        Section($"Under siege: day {band.SiegeDays} of at most {rules.MaxDays}",
            "The raiders are building siege engines to break the walls. Outside the walls work runs at half pace, no caravans " +
            "get through, and the militia stay armed. Hold out until they give up, ride out against their engines, or pay them to go.",
            AlertColor(Alert.CallToArms));
        Line($"• {Capitalize(Raids.Describe(s, band))} are camped before the walls");
        foreach (var engine in band.Engines)
        {
            string name = rules.Engine(engine.Kind)?.Name ?? engine.Kind;
            var def = rules.Engine(engine.Kind);
            bool inRange = def != null && Siege.InArcherRange(rules, def);
            Line(!engine.Built ? $"• {name}: being built out of bowshot ({engine.Progress:P0})"
                : !engine.InPlace ? $"• {name}: being moved into place" + (inRange ? $", within our archers' range ({Math.Max(0, engine.Hp)}/{def!.Hp})" : "")
                : inRange ? $"• {name}: battering the walls, within our archers' range ({Math.Max(0, engine.Hp)}/{def!.Hp})"
                : $"• {name}: battering the walls from beyond bowshot");
        }
        if (band.Engines.Count == 0) Line("• No engines yet");
        Line($"• Our archers and towers shoot {Siege.ArrowDamage(World, s):0} damage a day at engines within {rules.ArcherRange:0} tiles");
        if (band.HasTarget && s.Walls.TryGetValue((band.TargetX, band.TargetY), out var wall))
        {
            int max = Sim.Core.Commands.Walls.PieceHitPoints(World, wall.Tier, wall.Piece);
            Line($"• The wall under attack has {wall.Hp} of {max} strength left", wall.Hp < max / 3 ? new Color(1f, 0.6f, 0.55f) : null);
        }

        ActionButton($"Sally out with {Armies.Soldiers(s)} soldiers", "Ride out against the besiegers: a battle without the walls' help. " +
            "Win, and their engines burn. Once a day.", () => Report(_session.Execute(new SallyCommand(s.Id)), "The soldiers sallied out"));
        int price = Siege.Tribute(World, band);
        ActionButton($"Pay them {price} coin to leave (treasury {s.Stock.Get(Sim.Core.Economy.Inventory.Coin)})",
            "The raiders take the coin and lift the siege.",
            () => Report(_session.Execute(new PayTributeCommand(s.Id)), "The raiders took the coin and left"));
    }

    private void Report(Sim.Core.Commands.CommandResult result, string ok)
    {
        _note.Text = result.Ok ? ok : result.Error ?? "";
        _shown = "";
        Refresh();
    }

    public static string AlertText(Alert alert) => alert switch
    {
        Alert.Watch => "Watch: raiders sighted",
        Alert.Muster => "Muster: the militia are arming",
        Alert.CallToArms => "Call to arms!",
        _ => "All quiet",
    };

    private static string AlertHelp(Alert alert) => alert switch
    {
        Alert.Watch => "A warband has been seen heading this way. Nothing is lost yet; towers let the watch see further.",
        Alert.Muster => "The warband is close: the muster has called people from their work to arm.",
        Alert.CallToArms => "The raiders are at the walls: everyone called up fights.",
        _ => "No raiders in sight.",
    };

    public static Color AlertColor(Alert alert) => alert switch
    {
        Alert.Watch => new Color(1f, 0.9f, 0.5f),
        Alert.Muster => new Color(1f, 0.65f, 0.3f),
        Alert.CallToArms => new Color(1f, 0.4f, 0.35f),
        _ => new Color(0.75f, 0.9f, 0.75f),
    };

    private void Section(string title, string help, Color? color = null)
    {
        _body.AddChild(new Control { CustomMinimumSize = new Vector2(0, 6) });
        var label = new Label { Text = title };
        label.AddThemeFontSizeOverride("font_size", 17);
        label.AddThemeColorOverride("font_color", color ?? new Color(1f, 0.9f, 0.6f));
        _body.AddChild(label);
        if (help.Length == 0) return;
        var note = new Label { Text = help, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(340, 0) };
        note.AddThemeFontSizeOverride("font_size", 12);
        note.AddThemeColorOverride("font_color", new Color(0.8f, 0.8f, 0.8f));
        _body.AddChild(note);
    }

    private void Line(string text, Color? color = null)
    {
        var label = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(340, 0) };
        if (color is { } c) label.AddThemeColorOverride("font_color", c);
        _body.AddChild(label);
    }

    private void ActionButton(string text, string tooltip, Action pressed)
    {
        var b = new Button { Text = text, TooltipText = tooltip, FocusMode = FocusModeEnum.None, Alignment = HorizontalAlignment.Left };
        b.Pressed += pressed;
        _body.AddChild(b);
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
