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
    private RichTextLabel _overview = null!, _treasury = null!;
    private GridContainer _goods = null!, _work = null!;
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
        _goods = GridTab("Goods", 8);
        _treasury = TextTab("Treasury");
        _work = GridTab("Work", 4);
    }

    private RichTextLabel TextTab(string name)
    {
        var label = new RichTextLabel { Name = name, BbcodeEnabled = true, ScrollActive = true, SelectionEnabled = true };
        _tabs.AddChild(label);
        return label;
    }

    private GridContainer GridTab(string name, int columns)
    {
        var scroll = new ScrollContainer { Name = name, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _tabs.AddChild(scroll);
        var grid = new GridContainer { Columns = columns, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        grid.AddThemeConstantOverride("h_separation", 14);
        scroll.AddChild(grid);
        return grid;
    }

    public void Toggle()
    {
        Visible = !Visible;
        _sinceRefresh = RefreshSeconds;
    }

    /// <summary>Opens on a tab: 0 Overview, 1 Goods, 2 Treasury, 3 Work.</summary>
    public void ShowTab(int index) => _tabs.CurrentTab = Math.Clamp(index, 0, _tabs.GetTabCount() - 1);

    public override void _Process(double delta)
    {
        if (!Visible || _session == null) return;
        _sinceRefresh += delta;
        if (_sinceRefresh < RefreshSeconds) return;
        _sinceRefresh = 0;
        FillOverview();
        FillGoods();
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
        int idle = adults - jobs.Count;
        int homeless = s.Citizens.Count(c => c.HomeBuildingId == null);

        sb.AppendLine($"[b]{s.Name}[/b], {World.Time}: {s.Citizens.Count} people, {adults} adults, " +
                      $"mood {(s.Citizens.Count > 0 ? s.Citizens.Average(c => c.Mood) : 0):0}/100");
        sb.AppendLine("[b]People:[/b] " + string.Join(", ", s.Citizens.GroupBy(c => HomeSystem.ClassOf(World, s, c).Name)
            .OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Count()} {g.Key.ToLowerInvariant()}")));
        sb.AppendLine("[b]Homes:[/b] " + string.Join(", ", s.Buildings.Where(b => b.Def.Home != null && b.IsOperational)
            .GroupBy(b => b.Def.Name).Select(g => $"{g.Count()} {g.Key.ToLowerInvariant()}s")) +
                      (homeless > 0 ? $"; [color=#ff9070]{homeless} homeless[/color]" : ""));
        sb.AppendLine($"[b]Adults:[/b] {working} working, {standby} on standby (stock limits reached), {hauling} hauling, " +
                      (idle > 0 ? $"[color=#ffb060]{idle} idle[/color]" : "0 idle"));
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
            string runsOut = row.Net < -0.05 ? $"{stock / -row.Net:0} days" : "-";
            Color? warn = row.Net < -0.05 && stock / -row.Net < 10 ? new Color(1f, 0.55f, 0.45f) : null;
            Cell(_goods, r.Name, left: true);
            Cell(_goods, stock.ToString());
            Cell(_goods, limit == int.MaxValue ? "-" : limit.ToString());
            Cell(_goods, $"{row.Made:0.#}");
            Cell(_goods, $"{-row.Used:0.#}");
            Cell(_goods, $"{row.Traded:+0.#;-0.#;0}");
            Cell(_goods, $"{row.Net:+0.#;-0.#;0}", row.Net < -0.05 ? new Color(1f, 0.7f, 0.5f) : row.Net > 0.05 ? new Color(0.6f, 0.95f, 0.6f) : null);
            Cell(_goods, runsOut, warn);
        }
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
