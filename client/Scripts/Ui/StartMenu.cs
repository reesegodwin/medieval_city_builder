using Godot;
using Sim.Core.Simulation;
using Sim.Net;

namespace MedievalClient.Ui;

/// <summary>
/// First screen: play alone, host a world for friends, or join one. Each choice creates a
/// <see cref="SimSession"/> and hands it to <see cref="Started"/>.
/// </summary>
public partial class StartMenu : CanvasLayer
{
    public event Action<SimSession>? Started;

    private LineEdit _name = null!;
    private OptionButton _speed = null!;
    private OptionButton _speedPolicy = null!;
    private OptionButton _whenEmpty = null!;
    private LineEdit _seed = null!;
    private LineEdit _port = null!;
    private LineEdit _address = null!;
    private LineEdit _password = null!;
    private Label _status = null!;

    public override void _Ready()
    {
        var backdrop = new ColorRect { Color = new Color(0.13f, 0.16f, 0.12f) };
        backdrop.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        AddChild(backdrop);

        var panel = new PanelContainer { CustomMinimumSize = new Vector2(560, 0) };
        panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.Center);
        panel.GrowHorizontal = Control.GrowDirection.Both;
        panel.GrowVertical = Control.GrowDirection.Both;
        AddChild(panel);
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 10);
        panel.AddChild(column);

        var title = new Label { Text = "Medieval City Builder", HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 30);
        column.AddChild(title);

        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 12);
        column.AddChild(grid);
        Control Row(string label, Control field, string tip)
        {
            grid.AddChild(new Label { Text = label, TooltipText = tip });
            field.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            field.TooltipText = tip;
            grid.AddChild(field);
            return field;
        }

        _name = (LineEdit)Row("Your name", new LineEdit { Text = System.Environment.UserName, MaxLength = Protocol.MaxNameLength },
            "Your name in shared worlds; it's how the world knows which town is yours.");
        _password = (LineEdit)Row("Password", new LineEdit { Secret = true, PlaceholderText = "for dedicated servers" },
            "Dedicated servers keep accounts: the first time you join, this password claims your name there. " +
            "It is never sent over the network; the game proves you know it.");
        _speed = (OptionButton)Row("World speed", new OptionButton(),
            "How fast the world runs. Balance is the same at every speed; only the clock changes.");
        foreach (var (name, secondsPerDay, description) in WorldSpeeds.Presets)
            _speed.AddItem($"{char.ToUpper(name[0])}{name[1..]}: {description}");
        _speed.Selected = 2; // evening
        _speedPolicy = (OptionButton)Row("Speed control", new OptionButton(), "Who may speed up a shared world.");
        _speedPolicy.AddItem("Host decides");
        _speedPolicy.AddItem("Everyone must agree");
        _speedPolicy.AddItem("Fixed speed");
        _whenEmpty = (OptionButton)Row("When nobody plays", new OptionButton(), "What happens to the world while it's empty.");
        _whenEmpty.AddItem("Pause");
        _whenEmpty.AddItem("Catch up (up to 7 days) when someone returns");
        _whenEmpty.AddItem("Keep running (only while hosted)");
        _seed = (LineEdit)Row("Seed (new worlds)", new LineEdit { PlaceholderText = "random" }, "The same seed always makes the same map.");
        _port = (LineEdit)Row("Port", new LineEdit { Text = Protocol.DefaultPort.ToString() },
            "The port friends connect to when you host (they may need it forwarded on your router).");
        _address = (LineEdit)Row("Join address", new LineEdit { PlaceholderText = "host or host:port" },
            "Address of a friend's game or a dedicated server.");

        var buttons = new HFlowContainer();
        buttons.AddThemeConstantOverride("h_separation", 8);
        column.AddChild(buttons);
        void Button(string text, string tip, Action action)
        {
            var b = new Button { Text = text, TooltipText = tip, CustomMinimumSize = new Vector2(120, 40) };
            b.Pressed += action;
            buttons.AddChild(b);
        }
        Button("New world", "Play alone in a new world", NewLocal);
        if (SimSession.HasSave) Button("Continue", "Load your single-player quick-save", ContinueLocal);
        Button(SimSession.HasHostedWorld ? "Host (continue)" : "Host", "Run a world here and let friends join while you play", Host);
        Button("Join", "Join a friend's game or a dedicated server", Join);

        _status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(540, 0) };
        _status.AddThemeColorOverride("font_color", new Color(1f, 0.75f, 0.5f));
        column.AddChild(_status);
    }

    private double SecondsPerDay => WorldSpeeds.Presets[_speed.Selected].SecondsPerDay;

    private ulong Seed => ulong.TryParse(_seed.Text, out ulong seed) ? seed : (ulong)Random.Shared.NextInt64();

    private WorldSettings Settings() => new()
    {
        SecondsPerGameDay = SecondsPerDay,
        SpeedPolicy = _speedPolicy.Selected switch { 1 => SpeedPolicy.Unanimous, 2 => SpeedPolicy.Fixed, _ => SpeedPolicy.Host },
        WhenEmpty = _whenEmpty.Selected switch { 1 => EmptyWorldPolicy.CatchUp, 2 => EmptyWorldPolicy.Run, _ => EmptyWorldPolicy.Pause },
    };

    private void NewLocal() => Start(() => SimSession.NewLocal(Seed, SecondsPerDay));

    private void ContinueLocal() => Start(() => new SimSession(SimSession.LoadSaved()));

    private void Host()
    {
        if (!int.TryParse(_port.Text, out int port))
        {
            _status.Text = "The port must be a number.";
            return;
        }
        Start(() => SimSession.HostGame(_name.Text.Trim(), port, Seed, Settings()));
    }

    private void Join()
    {
        string text = _address.Text.Trim();
        if (text.Length == 0)
        {
            _status.Text = "Enter the address of the game to join.";
            return;
        }
        int port = Protocol.DefaultPort;
        int colon = text.LastIndexOf(':');
        if (colon > 0 && int.TryParse(text[(colon + 1)..], out int p))
        {
            port = p;
            text = text[..colon];
        }
        string password = _password.Text;
        Start(() => SimSession.JoinGame(_name.Text.Trim(), text, port, password));
    }

    private void Start(Func<SimSession> create)
    {
        try
        {
            Started?.Invoke(create());
        }
        catch (Exception e)
        {
            _status.Text = $"Couldn't start: {e.Message}";
        }
    }
}
