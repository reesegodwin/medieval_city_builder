using System.Text;
using Godot;
using Sim.Core.Economy;
using Sim.Core.Settlements;
using Sim.Core.Simulation;
using Sim.Core.Systems;

namespace MedievalClient.Ui;

/// <summary>
/// The town ledger: who lives here and what bothers them, what each good is made and used for per day (and
/// what will run out), where coin comes from and goes, and how the work is staffed. Read-only.
/// </summary>
public partial class LedgerPanel : PanelContainer
{
    private const int AverageDays = 7;
    private const double RefreshSeconds = 1.0;

    private SimSession _session = null!;
    private TabContainer _tabs = null!;
    private RichTextLabel _overview = null!, _treasury = null!, _goodDetails = null!;
    private GridContainer _goods = null!, _work = null!;
    private LineChart _stockChart = null!, _flowChart = null!, _coinChart = null!, _treasuryChart = null!;
    private VBoxContainer _goodPanel = null!;

    /// <summary>The good whose history is shown under the Goods table (kept here: the table is rebuilt every second).</summary>
    private string? _selectedGood;
    private double _sinceRefresh = RefreshSeconds;

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
        OffsetRight = 600;
        OffsetTop = 108;
        OffsetBottom = -180;
        Visible = false;

        var column = new VBoxContainer();
        AddChild(column);
        var header = new HBoxContainer();
        column.AddChild(header);
        var title = new Label { Text = "Town ledger", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        title.AddThemeFontSizeOverride("font_size", 20);
        header.AddChild(title);
        var close = new Button { Text = "X", TooltipText = "Close (L)", FocusMode = FocusModeEnum.None };
        close.Pressed += () => Visible = false;
        header.AddChild(close);

        _tabs = new TabContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        column.AddChild(_tabs);
        _overview = TextTab("Overview");

        // The selected good's charts sit above the table, where they're seen without scrolling.
        var goodsColumn = ScrollTab("Goods");
        _goodPanel = new VBoxContainer { Visible = false };
        goodsColumn.AddChild(_goodPanel);
        _stockChart = new LineChart();
        _flowChart = new LineChart();
        _goodDetails = new RichTextLabel { BbcodeEnabled = true, FitContent = true, ScrollActive = false };
        _goodPanel.AddChild(_stockChart);
        _goodPanel.AddChild(_flowChart);
        _goodPanel.AddChild(_goodDetails);
        _goodPanel.AddChild(new HSeparator());
        goodsColumn.AddChild(new Label { Text = "Click a good to see its last 30 days.", Modulate = new Color(1, 1, 1, 0.7f) });
        _goods = Grid(goodsColumn, 8);

        var treasuryColumn = ScrollTab("Treasury");
        _treasury = new RichTextLabel { BbcodeEnabled = true, FitContent = true, ScrollActive = false, SelectionEnabled = true };
        _coinChart = new LineChart();
        _treasuryChart = new LineChart();
        treasuryColumn.AddChild(_treasuryChart);
        treasuryColumn.AddChild(_coinChart);
        treasuryColumn.AddChild(_treasury);

        _work = GridTab("Work", 4);
    }

    private RichTextLabel TextTab(string name)
    {
        var label = new RichTextLabel { Name = name, BbcodeEnabled = true, ScrollActive = true, SelectionEnabled = true };
        _tabs.AddChild(label);
        return label;
    }

    private GridContainer GridTab(string name, int columns) => Grid(ScrollTab(name), columns);

    /// <summary>A scrolling tab holding a column of controls.</summary>
    private VBoxContainer ScrollTab(string name)
    {
        var scroll = new ScrollContainer { Name = name, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _tabs.AddChild(scroll);
        var column = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        scroll.AddChild(column);
        return column;
    }

    private static GridContainer Grid(Container parent, int columns)
    {
        var grid = new GridContainer { Columns = columns, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        grid.AddThemeConstantOverride("h_separation", 14);
        parent.AddChild(grid);
        return grid;
    }

    public void Toggle()
    {
        Visible = !Visible;
        _sinceRefresh = RefreshSeconds;
    }

    /// <summary>Opens on a tab: 0 Overview, 1 Goods, 2 Treasury, 3 Work.</summary>
    public void ShowTab(int index) => _tabs.CurrentTab = Math.Clamp(index, 0, _tabs.GetTabCount() - 1);

    /// <summary>Shows a good's history on the Goods tab.</summary>
    public void SelectGood(string resource)
    {
        _selectedGood = resource;
        ShowTab(1);
        _sinceRefresh = RefreshSeconds;
    }

    public override void _Process(double delta)
    {
        if (!Visible || _session == null) return;
        _sinceRefresh += delta;
        if (_sinceRefresh < RefreshSeconds) return;
        _sinceRefresh = 0;
        FillOverview();
        FillGoods();
        FillGoodDetails();
        FillTreasury();
        FillWork();
    }

    // ---------- overview ----------

    private void FillOverview()
    {
        var s = Town;
        var sb = new StringBuilder();
        int adults = s.Citizens.Count(c => c.IsAdult);
        var jobs = s.Citizens.Where(c => c.IsAdult && c.JobBuildingId != null)
            .Select(c => s.FindBuilding(c.JobBuildingId!.Value)).ToList();
        int hauling = jobs.Count(b => b?.CurrentJobKind == JobKinds.Hauler);
        int standby = jobs.Count(b => b != null && s.IsOnStandby(b));
        int working = jobs.Count - hauling - standby;
        int sick = s.Citizens.Count(c => c.IsSick);
        int idle = s.Citizens.Count(c => c.CanWork && c.JobBuildingId == null);
        int homeless = s.Citizens.Count(c => c.HomeBuildingId == null);

        sb.AppendLine($"[b]{s.Name}[/b], {World.Time}: {s.Citizens.Count} people, {adults} adults, " +
                      $"mood {(s.Citizens.Count > 0 ? s.Citizens.Average(c => c.Mood) : 0):0}/100");
        sb.AppendLine("[b]People:[/b] " + string.Join(", ", s.Citizens.GroupBy(c => HomeSystem.ClassOf(World, s, c).Name)
            .OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Count()} {g.Key.ToLowerInvariant()}")));
        sb.AppendLine("[b]Homes:[/b] " + string.Join(", ", s.Buildings.Where(b => b.Def.Home != null && b.IsOperational)
            .GroupBy(b => b.Def.Name).Select(g => $"{g.Count()} {g.Key.ToLowerInvariant()}s")) +
                      (homeless > 0 ? $"; [color=#ff9070]{homeless} homeless[/color]" : ""));
        sb.AppendLine($"[b]Adults:[/b] {working} working, {standby} on standby (stock limits reached), {hauling} hauling, " +
                      (idle > 0 ? $"[color=#ffb060]{idle} idle[/color]" : "0 idle") +
                      (sick > 0 ? $", [color=#ff9070]{sick} sick[/color]" : ""));
        var season = SeasonSystem.Current(World);
        int coldHomes = s.Buildings.Count(b => b.Cold);
        sb.AppendLine($"[b]{season.Name}:[/b] farms and gardens yield {season.OutputOf("farm"):P0}, fishing {season.OutputOf("fishing_hut"):P0}" +
                      (season.HeatingWoodPerHomeDay > 0 ? $"; homes burn {season.HeatingWoodPerHomeDay:0.#} firewood a day" : "") +
                      (coldHomes > 0 ? $"; [color=#ff9070]{coldHomes} homes are cold[/color]" : ""));
        int food = SeasonSystem.FoodStock(World, s);
        double rotting = World.Data.FoodsByPreference.Sum(f => -s.Ledger.Average(f.Id, k => k == LedgerKeys.Spoiled, AverageDays));
        sb.AppendLine($"[b]Food store:[/b] {food} food, granaries keep {Math.Min(food, s.FoodStoreCapacity)} of it fresh" +
                      (rotting >= 0.5 ? $"; [color=#ffb060]{rotting:0} rotting a day[/color]" : "") +
                      $"; {s.Stock.Get(SeasonSystem.Firewood)} firewood");
        if (s.PoorHarvestDays > 0)
            sb.AppendLine($"[color=#ffb060]Poor harvest:[/color] farms and gardens yield less for {s.PoorHarvestDays} more days");
        foreach (string warning in s.ShortageWarnings)
        {
            double days = Forecast.DaysLeft(World, s, warning);
            if (days < Forecast.ClearDays)
                sb.AppendLine($"[color=#ff9070]{Forecast.NameOf(World, warning)} runs out in {days:0} days[/color] at the current rate");
        }
        if (!s.Rules.Disasters) sb.AppendLine("Disasters are switched off in the rules.");
        sb.AppendLine();
        sb.AppendLine("[b]Needs met[/b]");
        foreach (var need in World.Data.Needs)
        {
            double share = ServiceSystem.Coverage(s, need.Id);
            string color = share >= 0.9 ? "#90e090" : share >= 0.5 ? "#e0d070" : "#ff9070";
            sb.AppendLine($"  {need.Name}: [color={color}]{share:P0}[/color]" +
                          (s.Citizens.Count < need.MinPopulation ? $" (matters from {need.MinPopulation} people)" : ""));
        }

        // What weighs on people: negative mood factors added up across everyone.
        var complaints = new Dictionary<string, (int People, double Mood)>();
        foreach (var c in s.Citizens)
            foreach (var (label, mood) in Needs.MoodFactors(World, s, c))
                if (mood < 0)
                {
                    var (n, m) = complaints.GetValueOrDefault(label);
                    complaints[label] = (n + 1, m + mood);
                }
        sb.AppendLine();
        sb.AppendLine("[b]Complaints[/b]");
        if (complaints.Count == 0) sb.AppendLine("  None worth mentioning.");
        foreach (var (label, (people, mood)) in complaints.OrderBy(kv => kv.Value.Mood).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(8))
            sb.AppendLine($"  {label}: {people} people (costs the town {-mood / Math.Max(1, s.Citizens.Count):0.0#} mood on average)");
        _overview.Text = sb.ToString();
    }

    // ---------- goods ----------

    private void Cell(GridContainer grid, string text, Color? color = null, bool bold = false, bool left = false)
    {
        var label = new Label { Text = text, HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right };
        if (color is { } c) label.AddThemeColorOverride("font_color", c);
        if (bold) label.AddThemeColorOverride("font_color", new Color(1f, 0.9f, 0.6f));
        grid.AddChild(label);
    }

    private static void Clear(Node node)
    {
        foreach (var child in node.GetChildren()) child.QueueFree();
    }

    private void FillGoods()
    {
        Clear(_goods);
        foreach (string h in new[] { "Good", "Stock", "Limit", "Made", "Used", "Traded", "Net/day", "Runs out" }) Cell(_goods, h, bold: true, left: h == "Good");
        foreach (var r in World.Data.Resources.Values.OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            if (r.Id == Inventory.Coin) continue;
            var row = LedgerRow.Of(Town.Ledger, r.Id, AverageDays);
            int stock = Town.Stock.Get(r.Id);
            int limit = Town.LimitOf(r.Id);
            if (stock == 0 && Math.Abs(row.Made) + Math.Abs(row.Used) + Math.Abs(row.Traded) < 0.05) continue;
            double daysLeft = Forecast.DaysLeft(World, Town, r.Id);
            string runsOut = double.IsPositiveInfinity(daysLeft) ? "-" : $"{daysLeft:0} days";
            Color? warn = daysLeft < Forecast.WarnDays ? new Color(1f, 0.55f, 0.45f) : null;
            GoodButton(r.Id, r.Name);
            Cell(_goods, stock.ToString());
            Cell(_goods, limit == int.MaxValue ? "-" : limit.ToString());
            Cell(_goods, $"{row.Made:0.#}");
            Cell(_goods, $"{-row.Used:0.#}");
            Cell(_goods, $"{row.Traded:+0.#;-0.#;0}");
            Cell(_goods, $"{row.Net:+0.#;-0.#;0}", row.Net < -0.05 ? new Color(1f, 0.7f, 0.5f) : row.Net > 0.05 ? new Color(0.6f, 0.95f, 0.6f) : null);
            Cell(_goods, runsOut, warn);
        }
    }

    private void GoodButton(string id, string name)
    {
        var button = new Button
        {
            Text = name, Flat = true, FocusMode = FocusModeEnum.None, Alignment = HorizontalAlignment.Left,
            TooltipText = "Show this good's last 30 days", ToggleMode = true, ButtonPressed = id == _selectedGood,
        };
        button.AddThemeConstantOverride("h_separation", 0);
        button.Pressed += () =>
        {
            _selectedGood = _selectedGood == id ? null : id;
            _sinceRefresh = RefreshSeconds;
        };
        _goods.AddChild(button);
    }

    private static readonly Color MadeColor = new(0.55f, 0.9f, 0.55f);
    private static readonly Color UsedColor = new(1f, 0.6f, 0.45f);
    private static readonly Color TradedColor = new(0.55f, 0.75f, 1f);
    private static readonly Color StockColor = new(1f, 0.85f, 0.45f);

    private static bool IsMade(string k) => LedgerKeys.Category(k) is LedgerKeys.Made or LedgerKeys.Taxes or LedgerKeys.Refunds;
    private static bool IsUsed(string k) => LedgerKeys.Category(k) is LedgerKeys.Inputs or LedgerKeys.Eaten or LedgerKeys.Used
        or LedgerKeys.Building or LedgerKeys.Upkeep or LedgerKeys.Disaster or LedgerKeys.Heating or LedgerKeys.Spoiled;
    private static bool IsTraded(string k) => LedgerKeys.Category(k) is LedgerKeys.TradeIn or LedgerKeys.TradeOut or LedgerKeys.Offers;

    private void FillGoodDetails()
    {
        if (_selectedGood is not { } id || !World.Data.Resources.TryGetValue(id, out var def))
        {
            _goodPanel.Visible = false;
            return;
        }
        _goodPanel.Visible = true;
        var ledger = Town.Ledger;
        _stockChart.SetData($"{def.Name}: stock", new[]
        {
            new LineChart.Series("in store", StockColor, ledger.StockSeries(id, Town.Stock.Get(id))),
        });
        _flowChart.SetData("Per day", new[]
        {
            new LineChart.Series("made", MadeColor, ledger.Series(id, IsMade)),
            new LineChart.Series("used", UsedColor, ledger.Series(id, IsUsed).Select(v => -v).ToArray()),
            new LineChart.Series("traded", TradedColor, ledger.Series(id, IsTraded)),
        });

        var sb = new StringBuilder();
        sb.AppendLine($"[b]{def.Name}: where it came from and went, per day over the last {Math.Min(AverageDays, ledger.History.Count)} days[/b]");
        var breakdown = ledger.Breakdown(id, AverageDays);
        if (breakdown.Count == 0) sb.AppendLine("  Nothing moved.");
        foreach (var (key, amount) in breakdown.Where(kv => Math.Abs(kv.Value) >= 0.05).OrderByDescending(kv => kv.Value))
            sb.AppendLine($"  {(amount > 0 ? "[color=#90e090]+" : "[color=#ff9070]")}{amount:0.#}[/color]  {Pretty(key)}");
        _goodDetails.Text = sb.ToString();
    }

    // ---------- treasury ----------

    private static string Pretty(string key)
    {
        var parts = key.Split(':');
        string what = parts[0] switch
        {
            LedgerKeys.Taxes => "Taxes",
            LedgerKeys.Upkeep => "Upkeep",
            LedgerKeys.TradeIn => "Sales and deliveries",
            LedgerKeys.TradeOut => "Purchases",
            LedgerKeys.Offers => "Set aside for offers",
            LedgerKeys.Building => "Building and roads",
            LedgerKeys.Refunds => "Refunds",
            LedgerKeys.Made => "Made",
            LedgerKeys.Inputs => "Used by workshops",
            LedgerKeys.Eaten => "Eaten",
            LedgerKeys.Used => "Used up (services, households, tools)",
            LedgerKeys.Disaster => "Lost to disaster",
            LedgerKeys.Heating => "Firewood for homes",
            LedgerKeys.Spoiled => "Rotted (outside granaries)",
            _ => parts[0],
        };
        return parts.Length > 1 ? $"{what}: {parts[1]}" : what;
    }

    private void FillTreasury()
    {
        var s = Town;
        var flows = s.Ledger.Breakdown(Inventory.Coin, AverageDays);
        var sb = new StringBuilder();
        sb.AppendLine($"[b]Treasury:[/b] {s.Stock.Get(Inventory.Coin)} coin" +
                      (s.UpkeepUnpaid ? "  [color=#ff8060]Upkeep unpaid: buildings with upkeep run at half output[/color]" : ""));
        sb.AppendLine($"[b]Average per day over the last {Math.Min(AverageDays, s.Ledger.History.Count)} days[/b]");
        sb.AppendLine();
        sb.AppendLine("[b]Income[/b]");
        foreach (var (key, amount) in flows.Where(kv => kv.Value > 0.05).OrderByDescending(kv => kv.Value))
            sb.AppendLine($"  [color=#90e090]+{amount:0.#}[/color]  {Pretty(key)}");
        sb.AppendLine("[b]Spending[/b]");
        foreach (var (key, amount) in flows.Where(kv => kv.Value < -0.05).OrderBy(kv => kv.Value))
            sb.AppendLine($"  [color=#ff9070]{amount:0.#}[/color]  {Pretty(key)}");
        double net = flows.Values.Sum();
        sb.AppendLine();
        sb.AppendLine($"[b]Balance:[/b] {(net >= 0 ? "[color=#90e090]" : "[color=#ff9070]")}{net:+0.#;-0.#;0} coin a day[/color]");
        sb.AppendLine($"Taxes today would bring {EconomySystem.DailyTax(World, s):0.#}; upkeep costs {EconomySystem.DailyUpkeep(s):0.#}.");
        _treasury.Text = sb.ToString();

        // Income and spending per day, and the treasury itself, over the books' history.
        var income = s.Ledger.History.Select(d => d.Flows.TryGetValue(Inventory.Coin, out var k) ? k.Values.Where(v => v > 0).Sum() : 0).ToArray();
        var spending = s.Ledger.History.Select(d => d.Flows.TryGetValue(Inventory.Coin, out var k) ? -k.Values.Where(v => v < 0).Sum() : 0).ToArray();
        _coinChart.SetData("Coin per day", new[]
        {
            new LineChart.Series("income", MadeColor, income),
            new LineChart.Series("spending", UsedColor, spending),
        });
        _treasuryChart.SetData("Treasury", new[]
        {
            new LineChart.Series("coin", StockColor, s.Ledger.StockSeries(Inventory.Coin, s.Stock.Get(Inventory.Coin))),
        });
    }

    // ---------- work ----------

    private void FillWork()
    {
        Clear(_work);
        foreach (string h in new[] { "Job", "Workers", "On standby", "Problems" }) Cell(_work, h, bold: true, left: h is "Job" or "Problems");
        var byKind = Town.Buildings
            .Where(b => b.CurrentJobKind != null && b.JobSlots > 0)
            .GroupBy(b => b.CurrentJobKind!)
            .OrderBy(g => g.Key, StringComparer.Ordinal);
        foreach (var g in byKind)
        {
            int workers = g.Sum(b => b.WorkerIds.Count);
            int slots = g.Sum(b => Town.WantedWorkers(b));
            int standby = g.Where(Town.IsOnStandby).Sum(b => b.WorkerIds.Count);
            string problems = string.Join(", ", g.Where(b => b.Problem != null).GroupBy(b => b.Problem!)
                .Select(p => $"{p.Count()}x {p.Key}"));
            if (workers == 0 && slots == 0) continue;
            Cell(_work, char.ToUpper(g.Key[0]) + g.Key[1..], left: true);
            Cell(_work, $"{workers}/{slots}", workers < slots ? new Color(1f, 0.75f, 0.5f) : null);
            Cell(_work, standby > 0 ? standby.ToString() : "-");
            Cell(_work, problems.Length > 0 ? problems : "-", left: true);
        }
    }
}
