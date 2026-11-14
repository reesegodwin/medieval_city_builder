using Godot;
using Sim.Core.Diplomacy;
using Sim.Core.Settlements;
using Sim.Core.Simulation;
using Sim.Core.Time;

namespace MedievalClient.Ui;

/// <summary>
/// Diplomacy (N): proposals waiting for an answer, and every other realm with its temper, what it thinks of you, how
/// strong it is next to you, the treaties you share, and what you can propose, give or demand.
/// </summary>
public partial class DiplomacyPanel : PanelContainer
{
    private const int GiftCoin = 50;
    private const int DemandCoin = 100;

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
        var title = new Label { Text = "Diplomacy", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        title.AddThemeFontSizeOverride("font_size", 20);
        header.AddChild(title);
        var close = new Button { Text = "X", TooltipText = "Close (N)", FocusMode = FocusModeEnum.None };
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

    public void Refresh()
    {
        if (!Visible) return;
        var book = World.Diplomacy;
        string signature = string.Join("|",
            Town.FactionId, book.Factions.Count, World.Time.Day,
            string.Join(",", book.Relations.Select(r => $"{r.A}-{r.B}:{(int)r.Opinion}")),
            string.Join(",", book.Treaties.Select(t => t.Id)),
            string.Join(",", book.Proposals.Select(p => p.Id)),
            Town.Stock.Get(Sim.Core.Economy.Inventory.Coin) >= DemandCoin);
        if (signature == _shown) return;
        _shown = signature;
        Build();
    }

    private void Build()
    {
        foreach (var child in _body.GetChildren()) child.QueueFree();
        var book = World.Diplomacy;
        int me = Town.FactionId;
        var mine = book.Find(me);
        if (mine == null)
        {
            Line("Your realm is being proclaimed; check back tomorrow.");
            return;
        }
        var others = book.Factions.Where(f => f.Id != me && Statecraft.Capital(World, f.Id) != null).ToList();
        if (others.Count == 0)
        {
            Section("No other realms", "There is nobody else in this land yet. Worlds made with neighbours (start menu) have computer-run " +
                                       "realms nearby; more are founded as the years pass.");
            return;
        }

        // --- Waiting for your answer ---
        var incoming = book.Proposals.Where(p => p.ToFaction == me).ToList();
        if (incoming.Count > 0)
        {
            Section("Waiting for your answer", "Proposals lapse after a few days. An unanswered demand counts as refused.");
            foreach (var p in incoming)
            {
                var from = book.Find(p.FromFaction)!;
                double days = (p.ExpiresTick - World.Time.Tick) / (double)TimeScale.TicksPerDay;
                Line($"[{from.Name}] propose {Statecraft.Describe(p.Kind)}" +
                     (p.Goods.Count > 0 ? $": {Sim.Core.Trade.Trading.Describe(p.Goods)}" : "") + $" ({days:0.#} days left)",
                     Color.FromHtml(from.Colour).Lightened(0.3f));
                var row = new HBoxContainer();
                int id = p.Id;
                row.AddChild(MakeButton("Accept", () => Do(new AnswerProposalCommand(Town.Id, id, true), "Accepted")));
                row.AddChild(MakeButton("Refuse", () => Do(new AnswerProposalCommand(Town.Id, id, false), "Refused")));
                _body.AddChild(row);
            }
        }
        var outgoing = book.Proposals.Where(p => p.FromFaction == me).ToList();
        if (outgoing.Count > 0)
        {
            Section("Your proposals", "");
            foreach (var p in outgoing)
                Line($"To {book.Find(p.ToFaction)?.Name}: {Statecraft.Describe(p.Kind).Replace("that you become their vassal", "that they become your vassal")} (waiting)");
        }

        // --- The realms ---
        double myStrength = Statecraft.Strength(World, me);
        if (Statecraft.OverlordOf(World, me) is { } overlord)
            Line($"You are the vassal of {overlord.Name}: you pay them {World.Data.Factions.Diplomacy.VassalShare:P0} of your income, " +
                 "and bandits trouble you less.", new Color(1f, 0.85f, 0.55f));
        foreach (var f in others)
        {
            double opinion = Statecraft.Opinion(World, me, f.Id);
            double ratio = Statecraft.Strength(World, f.Id) / Math.Max(1, myStrength);
            var seat = Statecraft.Capital(World, f.Id)!;
            string temper = f.IsNpc ? World.Data.Factions.Personality(f.Personality).Name.ToLowerInvariant() : "player";
            int distance = Math.Max(Math.Abs(seat.CenterX - Town.CenterX), Math.Abs(seat.CenterY - Town.CenterY));
            _body.AddChild(new Control { CustomMinimumSize = new Vector2(0, 6) });
            var name = new Label { Text = $"■ {f.Name} ({temper})" };
            name.AddThemeFontSizeOverride("font_size", 17);
            name.AddThemeColorOverride("font_color", Color.FromHtml(f.Colour).Lightened(0.3f));
            _body.AddChild(name);
            Line($"{seat.Name}, {distance} tiles away: {Statecraft.OpinionWord(opinion)} towards you ({opinion:+0;-0;0}); " +
                 $"{(ratio >= 1.5 ? "much stronger than you" : ratio >= 1.1 ? "stronger than you" : ratio > 0.9 ? "about as strong as you" : ratio > 0.6 ? "weaker than you" : "much weaker than you")}");
            if (f.IsNpc) Line(World.Data.Factions.Personality(f.Personality).Description, new Color(0.8f, 0.8f, 0.8f));

            foreach (var t in book.Treaties.Where(t => (t.A == me && t.B == f.Id) || (t.B == me && t.A == f.Id)))
            {
                string what = t.Kind == TreatyKind.Vassalage ? (t.A == me ? "They are your vassal" : "You are their vassal") : $"A {Statecraft.Describe(t.Kind)}";
                var row = new HBoxContainer();
                row.AddChild(new Label { Text = $"• {what}, since day {t.StartDay}", SizeFlagsHorizontal = SizeFlags.ExpandFill });
                int id = t.Id;
                row.AddChild(MakeButton("Break", () => Do(new EndTreatyCommand(Town.Id, id), "Treaty broken: every realm will remember it"),
                    "End it now. They, and every other realm, will think less of you."));
                _body.AddChild(row);
            }

            var actions = new HFlowContainer();
            int target = f.Id;
            void Offer(string text, ProposalKind kind, string tip, IReadOnlyDictionary<string, int>? goods = null)
            {
                if (Statecraft.CheckProposal(World, me, target, kind, goods) != null) return;
                actions.AddChild(MakeButton(text, () => Do(new ProposeCommand(Town.Id, target, kind, goods), "Proposal sent"), tip));
            }
            Offer("Trade pact", ProposalKind.TradePact, "Caravans between you travel faster and their towns pay friendlier prices; you warm to each other over time.");
            Offer("Non-aggression", ProposalKind.NonAggression, "Neither of you may go to war on the other while it lasts.");
            Offer("Alliance", ProposalKind.Alliance, "No war between you, you share what your watchmen see, and allies come to each other's aid.");
            Offer("Become their vassal", ProposalKind.OfferVassalage, "Pay them a share of your income; in return bandits trouble you less and they won't attack you.");
            Offer("Demand vassalage", ProposalKind.DemandVassalage, "Demand that they pay you a share of their income. The weak may agree; the proud won't forget.");
            Offer($"Demand {DemandCoin} coin", ProposalKind.Demand, "Demand coin, or else. Refusals sour relations.",
                new Dictionary<string, int> { [Sim.Core.Economy.Inventory.Coin] = DemandCoin });
            if (Town.Stock.Get(Sim.Core.Economy.Inventory.Coin) >= GiftCoin)
                actions.AddChild(MakeButton($"Gift {GiftCoin} coin", () => Do(new SendGiftCommand(Town.Id, target,
                    new Dictionary<string, int> { [Sim.Core.Economy.Inventory.Coin] = GiftCoin }), "Gift sent"), "Envoys carry it at once; it warms relations."));
            _body.AddChild(actions);
        }
    }

    private void Do(Sim.Core.Commands.ICommand command, string ok)
    {
        var result = _session.Execute(command);
        _note.Text = result.Ok ? ok : result.Error ?? "";
        _shown = "";
        Refresh();
    }

    private static Button MakeButton(string text, Action pressed, string? tip = null)
    {
        var b = new Button { Text = text, FocusMode = FocusModeEnum.None, TooltipText = tip ?? "" };
        b.Pressed += pressed;
        return b;
    }

    private void Section(string title, string help)
    {
        _body.AddChild(new Control { CustomMinimumSize = new Vector2(0, 6) });
        var label = new Label { Text = title };
        label.AddThemeFontSizeOverride("font_size", 17);
        label.AddThemeColorOverride("font_color", new Color(1f, 0.9f, 0.6f));
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
}
