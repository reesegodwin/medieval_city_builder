using Godot;
using Sim.Core.Economy;
using Sim.Core.Settlements;
using Sim.Core.Simulation;
using Sim.Core.Trade;

namespace MedievalClient.Ui;

/// <summary>
/// Trading: browse and accept other towns' offers, post your own (sell, buy or barter, with a fair-price button),
/// run trade routes, and follow caravans on the road. Deals take effect at once; goods arrive by caravan.
/// </summary>
public partial class TradePanel : PanelContainer
{
    private SimSession _session = null!;
    private Label _note = null!;
    private VBoxContainer _market = null!, _mine = null!, _routes = null!, _caravans = null!;
    private string _shown = "";

    // New offer
    private OptionButton _mode = null!, _giveRes = null!, _wantRes = null!;
    private SpinBox _giveAmount = null!, _wantAmount = null!;
    private HSlider _priceFactor = null!;
    private Label _fairLabel = null!;

    // New route
    private OptionButton _partner = null!, _routeGive = null!, _routeWant = null!;
    private SpinBox _routeGiveAmount = null!, _routeWantAmount = null!, _routeDays = null!;

    private List<string> _resources = new();
    private TabContainer _tabs = null!;

    /// <summary>Opens on a tab: 0 Market, 1 My offers, 2 New offer, 3 Routes, 4 Caravans.</summary>
    public void ShowTab(int index) => _tabs.CurrentTab = Math.Clamp(index, 0, _tabs.GetTabCount() - 1);

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
        OffsetRight = 520;
        OffsetTop = 108;
        OffsetBottom = -180;
        Visible = false;

        _resources = World.Data.Resources.Values.Where(r => r.Value > 0 && r.Id != Inventory.Coin)
            .OrderBy(r => r.Id, StringComparer.Ordinal).Select(r => r.Id).ToList();

        var column = new VBoxContainer();
        AddChild(column);
        var header = new HBoxContainer();
        column.AddChild(header);
        var title = new Label { Text = "Trade", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        title.AddThemeFontSizeOverride("font_size", 20);
        header.AddChild(title);
        var close = new Button { Text = "X", TooltipText = "Close (J)", FocusMode = FocusModeEnum.None };
        close.Pressed += () => Visible = false;
        header.AddChild(close);

        _note = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _note.AddThemeColorOverride("font_color", new Color(1f, 0.85f, 0.55f));
        column.AddChild(_note);

        var tabs = _tabs = new TabContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        column.AddChild(tabs);
        _market = Tab(tabs, "Market");
        _mine = Tab(tabs, "My offers");
        BuildNewOffer(Tab(tabs, "New offer"));
        _routes = Tab(tabs, "Routes");
        _caravans = Tab(tabs, "Caravans");
    }

    private static VBoxContainer Tab(TabContainer tabs, string name)
    {
        var scroll = new ScrollContainer { Name = name, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        tabs.AddChild(scroll);
        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(box);
        return box;
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

    // ---------- new offer ----------

    private OptionButton ResourcePicker(IEnumerable<string> ids)
    {
        var picker = new OptionButton();
        foreach (string id in ids) picker.AddItem(World.Data.Resources[id].Name);
        return picker;
    }

    private void BuildNewOffer(VBoxContainer box)
    {
        var grid = new GridContainer { Columns = 2 };
        box.AddChild(grid);
        void Row(string label, Control field)
        {
            grid.AddChild(new Label { Text = label });
            field.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            grid.AddChild(field);
        }

        _mode = new OptionButton();
        _mode.AddItem("Sell goods for coin");
        _mode.AddItem("Buy goods with coin");
        _mode.AddItem("Barter goods for goods");
        _mode.Selected = 0;
        Row("Kind", _mode);

        _giveRes = ResourcePicker(_resources);
        _giveAmount = new SpinBox { MinValue = 1, MaxValue = 100000, Value = 10 };
        _wantRes = ResourcePicker(_resources);
        _wantAmount = new SpinBox { MinValue = 1, MaxValue = 1000000, Value = 10 };
        Row("Goods", _giveRes);
        Row("Amount", _giveAmount);
        Row("For", _wantRes);
        Row("Price / amount", _wantAmount);

        _priceFactor = new HSlider { MinValue = 0.5, MaxValue = 3, Step = 0.05, Value = 1, TooltipText = "Price as a multiple of the fair price" };
        Row("Price factor", _priceFactor);
        _fairLabel = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        box.AddChild(_fairLabel);

        var buttons = new HBoxContainer();
        box.AddChild(buttons);
        var fair = new Button { Text = "Fair price", TooltipText = "Sell at what merchants pay, or buy at what they charge" };
        fair.Pressed += () =>
        {
            _priceFactor.Value = 1;
            ApplyPrice();
        };
        buttons.AddChild(fair);
        var post = new Button { Text = "Post offer" };
        post.Pressed += PostOffer;
        buttons.AddChild(post);

        _mode.ItemSelected += _ => UpdateNewOffer();
        _giveRes.ItemSelected += _ => ApplyPrice();
        _giveAmount.ValueChanged += _ => ApplyPrice();
        _priceFactor.ValueChanged += _ => ApplyPrice();
        _wantRes.ItemSelected += _ => UpdateFairLabel();
        _wantAmount.ValueChanged += _ => UpdateFairLabel();
        UpdateNewOffer();
    }

    private int Mode => _mode.Selected; // 0 sell, 1 buy, 2 barter

    private void UpdateNewOffer()
    {
        _wantRes.Disabled = Mode != 2;
        _priceFactor.Editable = Mode != 2;
        ApplyPrice();
    }

    /// <summary>Sets the coin price from the fair price and the price factor.</summary>
    private void ApplyPrice()
    {
        if (Mode == 2)
        {
            UpdateFairLabel();
            return;
        }
        var goods = new Dictionary<string, int> { [_resources[_giveRes.Selected]] = (int)_giveAmount.Value };
        int fair = Mode == 0 ? Trading.FairSellPrice(World, goods) : Trading.FairBuyPrice(World, goods);
        _wantAmount.Value = Math.Max(1, (int)Math.Round(fair * _priceFactor.Value));
        UpdateFairLabel();
    }

    private void UpdateFairLabel()
    {
        var goods = new Dictionary<string, int> { [_resources[_giveRes.Selected]] = (int)_giveAmount.Value };
        _fairLabel.Text = Mode switch
        {
            0 => $"Fair price: {Trading.FairSellPrice(World, goods)} coin. Merchants buy at that or less; raise it to keep goods you'd rather not sell.",
            1 => $"Fair price: {Trading.FairBuyPrice(World, goods)} coin (what merchants charge). Merchants sell tools, cloth, ore, stone, wood, grain and wool.",
            _ => $"You give goods worth {Trading.Value(World, goods):0} coin for goods worth " +
                 $"{Trading.Value(World, new Dictionary<string, int> { [_resources[_wantRes.Selected]] = (int)_wantAmount.Value }):0}.",
        };
    }

    private void PostOffer()
    {
        string goods = _resources[_giveRes.Selected];
        int amount = (int)_giveAmount.Value;
        int price = (int)_wantAmount.Value;
        var (give, want) = Mode switch
        {
            0 => (Goods(goods, amount), Goods(Inventory.Coin, price)),
            1 => (Goods(Inventory.Coin, price), Goods(goods, amount)),
            _ => (Goods(goods, amount), Goods(_resources[_wantRes.Selected], price)),
        };
        Run(new PostOfferCommand(Town.Id, give, want), "Offer posted");
    }

    private static Dictionary<string, int> Goods(string r, int n) => new() { [r] = n };

    private void Run(Sim.Core.Commands.ICommand command, string done)
    {
        var result = _session.Execute(command);
        _note.Text = result.Ok ? done : result.Error ?? "Refused";
        _shown = "";
    }

    // ---------- refresh ----------

    /// <summary>Rebuilds the lists when the trade state changed (not every frame, so buttons stay clickable).</summary>
    public void Refresh()
    {
        if (!Visible) return;
        var book = World.Trade;
        string state = $"{book.Offers.Count}:{string.Join(",", book.Offers.Select(o => o.Id))}|{string.Join(",", book.Routes.Select(r => $"{r.Id}{r.Accepted}{r.Rounds}{r.Skipped}"))}" +
                       $"|{string.Join(",", book.Caravans.Select(c => c.Id))}|{World.Time.Day}|{Town.Stock.Get(Inventory.Coin)}|{World.Settlements.Count}";
        if (state == _shown) return;
        _shown = state;

        bool hasPost = Trading.TradingPost(Town) != null;
        if (!hasPost) _note.Text = "Build a Trading Post to post and accept offers. Merchants visit towns that have a staffed one.";
        else if (_note.Text.StartsWith("Build a Trading Post")) _note.Text = "";

        FillMarket(hasPost);
        FillMine();
        FillRoutes(hasPost);
        FillCaravans();
    }

    private string TownName(int id) => id == 0 ? "Merchants" : World.FindSettlement(id)?.Name ?? "?";

    private static void Clear(Container box)
    {
        foreach (var child in box.GetChildren()) child.QueueFree();
    }

    private static void Text(Container box, string text, Color? color = null)
    {
        var label = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        if (color is { } c) label.AddThemeColorOverride("font_color", c);
        box.AddChild(label);
    }

    private HBoxContainer Line(Container box, string text)
    {
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = SizeFlags.ExpandFill });
        box.AddChild(row);
        return row;
    }

    private string Fairness(TradeOffer o)
    {
        if (Trading.IsOnlyCoin(o.Want)) return $"fair {Trading.FairSellPrice(World, o.Give)}-{Trading.FairBuyPrice(World, o.Give)}";
        if (Trading.IsOnlyCoin(o.Give)) return $"fair {Trading.FairSellPrice(World, o.Want)}-{Trading.FairBuyPrice(World, o.Want)}";
        return $"worth {Trading.Value(World, o.Give):0} for {Trading.Value(World, o.Want):0}";
    }

    private void FillMarket(bool hasPost)
    {
        Clear(_market);
        var others = World.Trade.Offers.Where(o => o.SettlementId != Town.Id).ToList();
        Text(_market, $"You have {Town.Stock.Get(Inventory.Coin)} coin. Merchants visit every {World.Data.Trade.MerchantVisitDays:0} days.");
        if (others.Count == 0) Text(_market, "No other town has an offer up right now.");
        foreach (var o in others)
        {
            var row = Line(_market, $"{TownName(o.SettlementId)} gives {Trading.Describe(o.Give)} for {Trading.Describe(o.Want)}  ({Fairness(o)}, " +
                                    $"{(o.ExpiresTick - World.Time.Tick) / (double)Sim.Core.Time.TimeScale.TicksPerDay:0.#} days left)");
            var accept = new Button { Text = "Accept", Disabled = !hasPost || !Town.Stock.Has(o.Want) };
            int id = o.Id;
            accept.Pressed += () => Run(new AcceptOfferCommand(Town.Id, id), "Deal! Caravans are on the way.");
            row.AddChild(accept);
        }
    }

    private void FillMine()
    {
        Clear(_mine);
        var mine = World.Trade.Offers.Where(o => o.SettlementId == Town.Id).ToList();
        if (mine.Count == 0) Text(_mine, "You have no open offers. Post one in New offer.");
        foreach (var o in mine)
        {
            var row = Line(_mine, $"Give {Trading.Describe(o.Give)} for {Trading.Describe(o.Want)}  ({Fairness(o)})");
            var cancel = new Button { Text = "Cancel", TooltipText = "Take the offer down; the goods return to stock" };
            int id = o.Id;
            cancel.Pressed += () => Run(new CancelOfferCommand(Town.Id, id), "Offer cancelled; goods returned");
            row.AddChild(cancel);
        }
    }

    private void FillRoutes(bool hasPost)
    {
        Clear(_routes);
        foreach (var r in World.Trade.Routes.Where(r => r.FromId == Town.Id || r.ToId == Town.Id))
        {
            bool outgoing = r.FromId == Town.Id;
            var (send, receive) = outgoing ? (r.Give, r.Want) : (r.Want, r.Give);
            string partner = TownName(outgoing ? r.ToId : r.FromId);
            string status = r.Accepted ? $"running, {r.Rounds} rounds, {r.Skipped} skipped" : outgoing ? "waiting for them" : "proposed to you";
            var row = Line(_routes, $"With {partner}: send {Trading.Describe(send)}, receive {Trading.Describe(receive)} every {r.IntervalDays:0.#} days ({status})");
            int id = r.Id;
            if (!r.Accepted && !outgoing)
            {
                var accept = new Button { Text = "Accept", Disabled = !hasPost };
                accept.Pressed += () => Run(new AcceptRouteCommand(Town.Id, id), "Route started");
                row.AddChild(accept);
            }
            var end = new Button { Text = r.Accepted || outgoing ? "End" : "Decline" };
            end.Pressed += () => Run(new EndRouteCommand(Town.Id, id), "Route ended");
            row.AddChild(end);
        }

        var partners = World.Settlements.Where(s => s != Town).ToList();
        Text(_routes, partners.Count == 0
            ? "Trade routes need another town in the world (host or join a shared world)."
            : "Propose a route: each round both sides send their goods by caravan.", new Color(0.8f, 0.85f, 1f));
        if (partners.Count == 0) return;

        var grid = new GridContainer { Columns = 2 };
        _routes.AddChild(grid);
        void Row(string label, Control field)
        {
            grid.AddChild(new Label { Text = label });
            field.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            grid.AddChild(field);
        }
        _partner = new OptionButton();
        foreach (var s in partners) _partner.AddItem(s.Name);
        var routeResources = _resources.Append(Inventory.Coin).ToList();
        _routeGive = ResourcePicker(routeResources);
        _routeGiveAmount = new SpinBox { MinValue = 1, MaxValue = 10000, Value = 10 };
        _routeWant = ResourcePicker(routeResources);
        _routeWantAmount = new SpinBox { MinValue = 1, MaxValue = 10000, Value = 10 };
        _routeDays = new SpinBox { MinValue = 1, MaxValue = 60, Value = 5 };
        Row("Town", _partner);
        Row("You send", _routeGive);
        Row("Amount", _routeGiveAmount);
        Row("They send", _routeWant);
        Row("Amount", _routeWantAmount);
        Row("Every (days)", _routeDays);
        var propose = new Button { Text = "Propose route", Disabled = !hasPost };
        propose.Pressed += () => Run(new ProposeRouteCommand(Town.Id, partners[_partner.Selected].Id,
            Goods(routeResources[_routeGive.Selected], (int)_routeGiveAmount.Value),
            Goods(routeResources[_routeWant.Selected], (int)_routeWantAmount.Value), _routeDays.Value), "Route proposed");
        _routes.AddChild(propose);
    }

    private void FillCaravans()
    {
        Clear(_caravans);
        var mine = World.Trade.Caravans.Where(c => c.FromId == Town.Id || c.ToId == Town.Id).ToList();
        if (mine.Count == 0) Text(_caravans, "No caravans on the road for you.");
        foreach (var c in mine)
        {
            double days = (c.ArriveTick - World.Time.Tick) / (double)Sim.Core.Time.TimeScale.TicksPerDay;
            string what = c.Kind switch
            {
                CaravanKind.MerchantArriving => "Merchant on the way to trade",
                CaravanKind.MerchantLeaving => $"Merchant leaving with {Trading.Describe(c.Cargo)}",
                _ => c.ToId == Town.Id
                    ? $"Arriving from {TownName(c.FromId)} with {Trading.Describe(c.Cargo)}"
                    : $"Heading to {TownName(c.ToId)} with {Trading.Describe(c.Cargo)}",
            };
            Text(_caravans, $"{what} ({days:0.#} days)");
        }
    }
}
