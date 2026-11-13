using Godot;
using Sim.Core.Commands;
using Sim.Core.Settlements;

namespace MedievalClient.Ui;

/// <summary>
/// The settlement's standing orders: job priorities, stock limits and reserves. Every change is sent
/// as a command, exactly as it would be to a server.
/// </summary>
public partial class RulesPanel : PanelContainer
{
    private SimSession _session = null!;
    private readonly List<(SpinBox Box, Func<int> Current)> _fields = new();
    private bool _refreshing;

    public void Initialize(SimSession session) => _session = session;

    public override void _Ready()
    {
        AnchorLeft = 0;
        AnchorRight = 0;
        AnchorTop = 0;
        AnchorBottom = 1;
        OffsetLeft = 10;
        OffsetRight = 400;
        OffsetTop = 108;
        OffsetBottom = -180;
        Visible = false;

        var column = new VBoxContainer();
        AddChild(column);

        var header = new HBoxContainer();
        column.AddChild(header);
        var title = new Label { Text = "Settlement rules", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        title.AddThemeFontSizeOverride("font_size", 20);
        header.AddChild(title);
        var close = new Button { Text = "X", TooltipText = "Close (P)", FocusMode = FocusModeEnum.None };
        close.Pressed += () => Visible = false;
        header.AddChild(close);

        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        column.AddChild(scroll);
        var body = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        scroll.AddChild(body);

        var town = _session.PlayerTown;
        var world = _session.World;

        Section(body, "Job priorities", "Higher numbers are filled first. Builders, and food during shortages, get extra priority.");
        var jobKinds = town.Rules.JobPriority.Keys
            .Union(world.Data.Buildings.Values.Select(b => b.JobKind).OfType<string>())
            .Distinct().OrderBy(k => k, StringComparer.Ordinal);
        foreach (string kind in jobKinds)
        {
            string k = kind;
            Field(body, Capitalize(k), -20, 50, () => town.Rules.PriorityOf(k),
                v => new SetJobPriorityCommand(town.Id, k, v));
        }

        Section(body, "Homes", "Hovels become cottages, then townhouses, when their people's needs are met. Better homes hold more " +
                               "people, who pay more tax. Each step costs materials.");
        var upgrades = new CheckBox { Text = "Upgrade homes automatically", ButtonPressed = town.Rules.AutoUpgradeHomes, FocusMode = FocusModeEnum.None };
        upgrades.Toggled += on => _session.Execute(new SetAutoUpgradeHomesCommand(town.Id, on));
        body.AddChild(upgrades);

        Section(body, "Stock limits", "Workplaces stop (and free their workers) once stock reaches the limit. " +
                                       "Food limits also grow with population.");
        foreach (var resource in world.Data.Resources.Values)
        {
            string id = resource.Id;
            Field(body, resource.Name, 0, 10000, () => town.Rules.ProductionLimits.GetValueOrDefault(id, 10000),
                v => new SetProductionLimitCommand(town.Id, id, v), step: 10);
        }

        Section(body, "Reserves", "Workshops won't use a resource below this amount (e.g. keep grain for eating).");
        foreach (var resource in world.Data.Resources.Values.Where(r => r.IsFood))
        {
            string id = resource.Id;
            Field(body, resource.Name, 0, 2000, () => town.Rules.ReserveOf(id),
                v => new SetReserveCommand(town.Id, id, v), step: 5);
        }
    }

    public void Toggle()
    {
        Visible = !Visible;
        if (Visible) Refresh();
    }

    /// <summary>Pulls current values from the simulation (e.g. after another source changed them).</summary>
    public void Refresh()
    {
        _refreshing = true;
        foreach (var (box, current) in _fields)
        {
            if (!box.GetLineEdit().HasFocus()) box.Value = current();
        }
        _refreshing = false;
    }

    private static void Section(VBoxContainer body, string title, string help)
    {
        var label = new Label { Text = title };
        label.AddThemeFontSizeOverride("font_size", 17);
        label.AddThemeColorOverride("font_color", new Color(1f, 0.9f, 0.6f));
        body.AddChild(new Control { CustomMinimumSize = new Vector2(0, 6) });
        body.AddChild(label);
        var note = new Label { Text = help, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(340, 0) };
        note.AddThemeFontSizeOverride("font_size", 12);
        note.AddThemeColorOverride("font_color", new Color(0.8f, 0.8f, 0.8f));
        body.AddChild(note);
    }

    private void Field(VBoxContainer body, string label, int min, int max, Func<int> current, Func<int, ICommand> command, int step = 1)
    {
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = label, SizeFlagsHorizontal = SizeFlags.ExpandFill });
        var box = new SpinBox { MinValue = min, MaxValue = max, Step = step, Value = current(), CustomMinimumSize = new Vector2(120, 0) };
        box.ValueChanged += value =>
        {
            if (_refreshing) return;
            var result = _session.Execute(command((int)value));
            if (!result.Ok) box.Value = current();
        };
        row.AddChild(box);
        body.AddChild(row);
        _fields.Add((box, current));
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
