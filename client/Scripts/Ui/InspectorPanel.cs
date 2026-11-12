using System.Text;
using Godot;
using Sim.Core.Citizens;
using Sim.Core.Settlements;

namespace MedievalClient.Ui;

/// <summary>Right-hand panel showing everything about the selected building or citizen.</summary>
public partial class InspectorPanel : PanelContainer
{
    private Label _title = null!;
    private RichTextLabel _body = null!;

    public event Action? Closed;

    public override void _Ready()
    {
        AnchorLeft = 1;
        AnchorRight = 1;
        AnchorTop = 0;
        AnchorBottom = 1;
        OffsetLeft = -380;
        OffsetRight = -10;
        OffsetTop = 84;
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
    }

    public void ShowBuilding(Settlement town, Building b)
    {
        Visible = true;
        _title.Text = b.Def.Name;
        var sb = new StringBuilder();

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
        sb.AppendLine($"[b]Hauling efficiency:[/b] {b.HaulFactor:P0} (distance to stockpile)");

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

        SetBody(sb);
    }

    public void ShowCitizen(Settlement town, Citizen c)
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
