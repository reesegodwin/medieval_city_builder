using Sim.Core.Commands;
using System.Text;
using Godot;
using Sim.Core.Citizens;
using Sim.Core.Settlements;
using Sim.Core.Simulation;
using Sim.Core.Systems;

namespace MedievalClient.Ui;

/// <summary>Right-hand panel showing everything about the selected building or citizen.</summary>
public partial class InspectorPanel : PanelContainer
{
    private Label _title = null!;
    private RichTextLabel _body = null!;
    private Button _upgrade = null!;

    public event Action? Closed;

    /// <summary>The player asked to upgrade the town centre.</summary>
    public event Action? UpgradeRequested;

    public override void _Ready()
    {
        AnchorLeft = 1;
        AnchorRight = 1;
        AnchorTop = 0;
        AnchorBottom = 1;
        OffsetLeft = -380;
        OffsetRight = -10;
        OffsetTop = 108;
        OffsetBottom = -80;
        Visible = false;

        var column = new VBoxContainer();
        AddChild(column);

        var header = new HBoxContainer();
        column.AddChild(header);
        _title = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _title.AddThemeFontSizeOverride("font_size", 20);
        header.AddChild(_title);
        var close = new Button { Text = "X", TooltipText = "Close (Esc)" };
        close.Pressed += () => Closed?.Invoke();
        header.AddChild(close);

        _body = new RichTextLabel
        {
            BbcodeEnabled = true,
            ScrollActive = true,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SelectionEnabled = true,
        };
        column.AddChild(_body);

        _upgrade = new Button { Visible = false };
        _upgrade.Pressed += () => UpgradeRequested?.Invoke();
        column.AddChild(_upgrade);
    }

    public void ShowBuilding(GameWorld world, Settlement town, Building b)
    {
        Visible = true;
        _title.Text = b.Def.Name;
        var sb = new StringBuilder();
        _upgrade.Visible = false;

        string state = b.State switch
        {
            BuildingState.AwaitingMaterials =>
                $"Waiting for materials ({string.Join(", ", b.Def.Cost.Select(kv => $"{kv.Value} {kv.Key}"))})",
            BuildingState.UnderConstruction =>
                $"Under construction: {b.ConstructionProgress / b.Def.BuildDays:P0}",
            _ => "Operational",
        };
        sb.AppendLine($"[b]State:[/b] {state}");
        if (b.Problem != null) sb.AppendLine($"[color=#ffb060][b]Problem:[/b] {b.Problem}[/color]");
        sb.AppendLine($"[b]Location:[/b] ({b.X}, {b.Y}), {b.Def.Width}x{b.Def.Height}");

        if (b.Def.Recipe is { } recipe && b.IsOperational)
        {
            string inputs = recipe.Inputs.Count == 0 ? "" : string.Join(", ", recipe.Inputs.Select(kv => $"{kv.Value} {kv.Key}")) + " -> ";
            string outputs = string.Join(", ", recipe.Outputs.Select(kv => $"{kv.Value} {kv.Key}"));
            sb.AppendLine($"[b]Makes:[/b] {inputs}{outputs} per worker-day");
            sb.AppendLine($"[b]Current batch:[/b] {Math.Min(1, b.CycleProgress / recipe.WorkDays):P0}");
            if (b.TerrainMultiplier > 1.001) sb.AppendLine($"[b]Soil bonus:[/b] +{b.TerrainMultiplier - 1:P0}");
        }
        sb.AppendLine(b.NoRoute
            ? "[color=#ff8060][b]Hauling:[/b] no walkable route to a stockpile![/color]"
            : $"[b]Hauling efficiency:[/b] {Logistics.EffectiveHaulFactor(town, b):P0} " +
              $"({b.HaulFactor:P0} from walking distance to a stockpile, the rest won back by haulers; roads help)");

        if (b.Def.Service is { } service)
        {
            sb.AppendLine();
            sb.AppendLine($"[b]Serves:[/b] {service.Need}, {b.ServedCount}/{service.Capacity} people");
            sb.AppendLine($"[b]Reach:[/b] homes within {service.Radius} tiles");
            foreach (var (resource, perPerson) in service.ConsumesPerPersonDay)
                sb.AppendLine($"[b]Uses:[/b] {perPerson:0.##} {resource} per person per day (in stock: {town.Stock.Get(resource)})");
        }

        if (b.JobSlots > 0 || b.WorkerIds.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"[b]Workers {b.WorkerIds.Count}/{b.JobSlots}[/b] ({b.CurrentJobKind})");
            foreach (int id in b.WorkerIds)
                if (town.FindCitizen(id) is { } c)
                    sb.AppendLine($"  {c.FullName}, skill {c.Skill(b.CurrentJobKind ?? ""):0}");
        }
        if (b.Def.Housing > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"[b]Residents {b.ResidentIds.Count}/{b.Def.Housing}[/b]");
            foreach (int id in b.ResidentIds)
                if (town.FindCitizen(id) is { } c)
                    sb.AppendLine($"  {c.FullName} ({(int)c.AgeYears})");
        }
        if (b.Def.Storage > 0) sb.AppendLine($"[b]Town storage:[/b] {town.Stock.Total}/{town.StorageCapacity}");
        if (b.CurrentJobKind == JobKinds.Hauler)
            sb.AppendLine($"[b]Hauling:[/b] haulers here carry goods for other workplaces. Across town they cover " +
                          $"{town.HaulCoverage:P0} of the carrying workers would otherwise do themselves.");
        else if (b.IsOperational && b.WorkerIds.Count > 0 && town.IsOnStandby(b))
            sb.AppendLine("[b]Standby:[/b] stock limit reached; workers wait here and help elsewhere when needed.");
        if (b.Def.Home is { } home && b.IsOperational) DescribeHome(world, town, b, home, sb);
        if (b.Def.Upkeep > 0)
            sb.AppendLine(town.UpkeepUnpaid
                ? $"[color=#ff8060][b]Upkeep:[/b] {b.Def.Upkeep:0.#} coin a day, unpaid: running at half output[/color]"
                : $"[b]Upkeep:[/b] {b.Def.Upkeep:0.#} coin a day");
        if (b.Def.TownCentre != null) DescribeTownCentre(world, town, b, sb);
        if (b.Def.Trade)
        {
            int offers = world.Trade.Offers.Count(o => o.SettlementId == town.Id);
            int caravans = world.Trade.Caravans.Count(c => c.FromId == town.Id || c.ToId == town.Id);
            sb.AppendLine();
            sb.AppendLine($"[b]Trade:[/b] {offers} open offers, {caravans} caravans on the road, {town.Stock.Get("coin")} coin.");
            sb.AppendLine(b.WorkerIds.Count > 0
                ? "Merchants visit while a trader works here. Open the trade panel with J."
                : "[color=#ffb060]Needs a trader before merchants will visit.[/color]");
        }

        SetBody(sb);
    }

    private static void DescribeHome(GameWorld world, Settlement town, Building b, Sim.Core.Data.HomeDef home, StringBuilder sb)
    {
        var cls = world.Data.ClassOf(home.Class);
        sb.AppendLine($"[b]Home of {cls.Name.ToLowerInvariant()}[/b] (tax {cls.TaxPerAdultDay:0.##} coin per adult a day)");
        if (home.Requires.Count > 0 && !HomeSystem.NeedsMet(town, b, home.Requires))
            sb.AppendLine($"[color=#ffb060]Needs unmet for {b.NeedsUnmetDays} days; slips back after {HomeSystem.DowngradeAfterDays}.[/color]");
        if (home.UpgradeTo is not { } nextId)
        {
            sb.AppendLine("Top tier.");
            return;
        }
        var next = world.Data.Building(nextId);
        string Mark(string need) => b.ResidentIds.Count > 0 && b.ResidentIds.All(id => town.FindCitizen(id)?.CoveredNeeds.Contains(need) == true)
            ? $"[color=#90e090]{need}[/color]" : $"[color=#ff9070]{need}[/color]";
        sb.AppendLine($"[b]Becomes a {next.Name}[/b] ({next.Housing} people) when every resident has: " +
                      string.Join(", ", next.Home!.Requires.Select(Mark)));
        sb.AppendLine($"Ready {b.UpgradeReadyDays}/{HomeSystem.UpgradeAfterDays} days; costs " +
                      string.Join(", ", next.Cost.Select(kv => $"{kv.Value} {kv.Key}")) +
                      (town.Rules.AutoUpgradeHomes ? "." : ". (Automatic upgrades are off in the rules.)"));
    }

    private void DescribeTownCentre(GameWorld world, Settlement town, Building b, StringBuilder sb)
    {
        sb.AppendLine();
        sb.AppendLine($"[b]Territory:[/b] {town.Territory.Count} tiles held");
        sb.AppendLine($"[b]Allowance:[/b] {town.TerritoryAllowance} tiles " +
                      $"(population allows {TerritoryRules.PopulationAllowance(town.Citizens.Count)}, cap {town.TerritoryCap})");
        sb.AppendLine("Paint territory with the Territory tool (T), or tick \"Auto land\" to grow it evenly.");
        sb.AppendLine($"[b]Roads:[/b] {town.RoadPlans.Count} tiles planned; roadworkers here build them (free, but they take time).");
        sb.AppendLine($"[b]Treasury:[/b] {town.Stock.Get("coin")} coin, taxes {EconomySystem.DailyTax(world, town):0.#} and upkeep " +
                      $"{EconomySystem.DailyUpkeep(town):0.#} a day.");

        if (b.Def.TownCentre!.UpgradeTo is not { } nextId)
        {
            sb.AppendLine("[b]Highest tier.[/b]");
            return;
        }
        var next = world.Data.Building(nextId);
        string cost = string.Join(", ", next.Cost.Select(kv => $"{kv.Value} {kv.Key}"));
        sb.AppendLine();
        sb.AppendLine($"[b]Next tier:[/b] {next.Name}, territory cap {next.TownCentre!.TerritoryCap} tiles");
        sb.AppendLine($"[b]Needs:[/b] {next.TownCentre.MinPopulation} people; costs {cost}; {next.BuildDays:0.#} worker-days to build");

        string? problem = UpgradeTownCentreCommand.Check(world, town, out _);
        _upgrade.Visible = true;
        _upgrade.Disabled = problem != null;
        _upgrade.Text = $"Upgrade to {next.Name}";
        _upgrade.TooltipText = problem ?? $"Rebuild as a {next.Name} ({cost}). The current cap stays until it's finished.";
    }

    public void ShowCitizen(GameWorld world, Settlement town, Citizen c)
    {
        Visible = true;
        _title.Text = c.FullName;
        var sb = new StringBuilder();

        sb.AppendLine($"{c.Sex}, age {(int)c.AgeYears}{(c.IsAdult ? "" : " (child)")}");
        sb.AppendLine($"[b]Traits:[/b] {(c.Traits.Count > 0 ? string.Join(", ", c.Traits) : "none")}");
        sb.AppendLine($"[b]Mood:[/b] {MoodWord(c.Mood)} ({c.Mood:0}/100)");
        sb.AppendLine($"[b]Fed:[/b] {(c.IsStarving ? "[color=#ff6060]starving[/color]" : $"{c.Satiation:P0}")}" +
                      $", last meal {(c.LastMealMood > 0 ? "bread" : c.LastMealMood < 0 ? "raw grain" : "-")}");

        string job = c.JobBuildingId is int j && town.FindBuilding(j) is { } jb
            ? $"{jb.CurrentJobKind} at {jb.Def.Name}"
            : c.IsAdult ? "idle" : "too young to work";
        sb.AppendLine($"[b]Job:[/b] {job}");
        string home = c.HomeBuildingId is int h && town.FindBuilding(h) is { } hb ? $"{hb.Def.Name} at ({hb.X}, {hb.Y})" : "[color=#ff6060]homeless[/color]";
        sb.AppendLine($"[b]Home:[/b] {home}");

        sb.AppendLine();
        sb.AppendLine("[b]What affects their mood[/b]");
        foreach (var (label, mood) in Needs.MoodFactors(world, town, c).OrderByDescending(f => f.Mood))
        {
            string color = mood > 0 ? "#90e090" : "#ff9070";
            sb.AppendLine($"  [color={color}]{(mood > 0 ? "+" : "")}{mood:0}[/color] {label}");
        }

        sb.AppendLine();
        if (c.SpouseId is int s) sb.AppendLine($"[b]Spouse:[/b] {CitizenName(town, s)}");
        if (c.ParentIds.Count > 0) sb.AppendLine($"[b]Parents:[/b] {string.Join(", ", c.ParentIds.Select(id => CitizenName(town, id)))}");
        if (c.ChildIds.Count > 0) sb.AppendLine($"[b]Children:[/b] {string.Join(", ", c.ChildIds.Select(id => CitizenName(town, id)))}");

        sb.AppendLine();
        sb.AppendLine("[b]Skills[/b]");
        if (c.Skills.Count == 0) sb.AppendLine("  none yet");
        foreach (var (kind, value) in c.Skills.OrderByDescending(kv => kv.Value))
            sb.AppendLine($"  {kind}: {value:0}");

        sb.AppendLine();
        sb.AppendLine("[b]Life story[/b]");
        foreach (string line in c.History.AsEnumerable().Reverse().Take(15))
            sb.AppendLine($"  {line}");

        SetBody(sb);
    }

    private void SetBody(StringBuilder sb)
    {
        // Keep the scroll position while the panel refreshes.
        int line = _body.GetVisibleLineCount() > 0 ? (int)_body.GetVScrollBar().Value : 0;
        _body.Text = sb.ToString();
        _body.GetVScrollBar().Value = line;
    }

    private static string CitizenName(Settlement town, int id) =>
        town.FindCitizen(id)?.FullName ?? "(gone)";

    private static string MoodWord(double mood) => mood switch
    {
        >= 75 => "joyful",
        >= 60 => "content",
        >= 45 => "fine",
        >= 30 => "unhappy",
        _ => "miserable",
    };
}
