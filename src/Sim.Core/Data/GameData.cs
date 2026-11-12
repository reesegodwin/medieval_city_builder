using System.Reflection;
using System.Text.Json;
using Sim.Core.World;

namespace Sim.Core.Data;

public sealed class ResourceDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>Food value of one unit (0 = not edible). A citizen needs 1.0 per day.</summary>
    public double Nutrition { get; set; }

    /// <summary>Mood effect of a meal of this food.</summary>
    public double MealMood { get; set; }

    public bool IsFood => Nutrition > 0;
}

public sealed class RecipeDef
{
    public Dictionary<string, int> Inputs { get; set; } = new();
    public Dictionary<string, int> Outputs { get; set; } = new();

    /// <summary>Worker-days of labour per production cycle (at average skill and mood).</summary>
    public double WorkDays { get; set; } = 1;
}

/// <summary>A citizen need met by service buildings (water, faith, drink, market...).</summary>
public sealed class NeedDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>Mood effect while the need is covered.</summary>
    public double CoveredMood { get; set; }

    /// <summary>Mood effect while it isn't (only once the settlement reaches <see cref="MinPopulation"/>).</summary>
    public double MissingMood { get; set; }

    public int MinPopulation { get; set; }
}

/// <summary>Makes a building serve a need to homes within a radius.</summary>
public sealed class ServiceDef
{
    public string Need { get; set; } = "";
    public int Radius { get; set; }
    public int Capacity { get; set; }

    /// <summary>Goods used up per person served per day (e.g. ale at a tavern).</summary>
    public Dictionary<string, double> ConsumesPerPersonDay { get; set; } = new();
}

public sealed class NearTerrainRequirement
{
    public Terrain Terrain { get; set; }
    public int Radius { get; set; }
    public int Min { get; set; }
}

/// <summary>
/// Marks a town-centre tier (keep, town hall, castle...). Each settlement has exactly one centre; it can't be
/// placed or demolished, only upgraded in place. Its tier caps how much territory the settlement may hold.
/// </summary>
public sealed class TownCentreDef
{
    /// <summary>Most territory tiles a settlement with this centre may claim (population grows it up to this).</summary>
    public int TerritoryCap { get; set; }

    /// <summary>The next tier, or null at the top.</summary>
    public string? UpgradeTo { get; set; }

    /// <summary>Population needed before the settlement may upgrade into this tier.</summary>
    public int MinPopulation { get; set; }
}

public sealed class BuildingDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Width { get; set; } = 1;
    public int Height { get; set; } = 1;

    /// <summary>Worker-days of construction labour.</summary>
    public double BuildDays { get; set; }

    public Dictionary<string, int> Cost { get; set; } = new();
    public int Storage { get; set; }
    public int Housing { get; set; }
    public int Jobs { get; set; }
    public string? JobKind { get; set; }
    public int BuilderSlots { get; set; } = 2;
    public RecipeDef? Recipe { get; set; }

    /// <summary>Extra output multiplier scaled by the fraction of fertile tiles under the footprint.</summary>
    public double FertileBonus { get; set; }

    public NearTerrainRequirement? NearTerrain { get; set; }

    public ServiceDef? Service { get; set; }

    public TownCentreDef? TownCentre { get; set; }

    public bool IsTownCentre => TownCentre != null;
    public bool IsStockpile => Storage > 0;
    public bool IsWorkplace => Jobs > 0 && JobKind != null;
}

/// <summary>All data-driven definitions, loaded from the JSON files embedded in this assembly.</summary>
public sealed class GameData
{
    public IReadOnlyDictionary<string, ResourceDef> Resources { get; }
    public IReadOnlyDictionary<string, BuildingDef> Buildings { get; }

    /// <summary>Needs in id order (deterministic iteration).</summary>
    public IReadOnlyList<NeedDef> Needs { get; }

    /// <summary>Food resources ordered best meal first.</summary>
    public IReadOnlyList<ResourceDef> FoodsByPreference { get; }

    public GameData(IEnumerable<ResourceDef> resources, IEnumerable<BuildingDef> buildings, IEnumerable<NeedDef>? needs = null)
    {
        Resources = resources.ToDictionary(r => r.Id);
        Buildings = buildings.ToDictionary(b => b.Id);
        Needs = (needs ?? Enumerable.Empty<NeedDef>()).OrderBy(n => n.Id, StringComparer.Ordinal).ToList();
        FoodsByPreference = Resources.Values.Where(r => r.IsFood)
            .OrderByDescending(r => r.MealMood).ThenBy(r => r.Id, StringComparer.Ordinal).ToList();
        Validate();
    }

    public BuildingDef Building(string id) =>
        Buildings.TryGetValue(id, out var def) ? def : throw new KeyNotFoundException($"Unknown building '{id}'");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public static GameData LoadDefault()
    {
        var resources = LoadEmbedded<List<ResourceDef>>("resources.json");
        var buildings = LoadEmbedded<List<BuildingDef>>("buildings.json");
        var needs = LoadEmbedded<List<NeedDef>>("needs.json");
        return new GameData(resources, buildings, needs);
    }

    private static T LoadEmbedded<T>(string fileName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        string name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("." + fileName, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        return JsonSerializer.Deserialize<T>(stream, JsonOptions)
               ?? throw new InvalidDataException($"Could not parse {fileName}");
    }

    /// <summary>The first town-centre tier (the one a new settlement starts with), or null if the data has none.</summary>
    public BuildingDef? StartingTownCentre { get; private set; }

    private void Validate()
    {
        var centres = Buildings.Values.Where(b => b.IsTownCentre).OrderBy(b => b.Id, StringComparer.Ordinal).ToList();
        foreach (var c in centres)
        {
            if (c.TownCentre!.UpgradeTo is not { } next) continue;
            if (!Buildings.TryGetValue(next, out var up) || !up.IsTownCentre)
                throw new InvalidDataException($"Town centre '{c.Id}' upgrades to '{next}', which isn't a town centre");
            if (up.Width != c.Width || up.Height != c.Height)
                throw new InvalidDataException($"Town centre '{c.Id}' and its upgrade '{next}' must have the same size");
        }
        var upgraded = centres.Select(c => c.TownCentre!.UpgradeTo).Where(u => u != null).ToHashSet();
        var roots = centres.Where(c => !upgraded.Contains(c.Id)).ToList();
        if (roots.Count > 1) throw new InvalidDataException("There must be only one starting town-centre tier");
        StartingTownCentre = roots.FirstOrDefault();

        foreach (var b in Buildings.Values)
        {
            var referenced = b.Cost.Keys
                .Concat(b.Recipe?.Inputs.Keys ?? Enumerable.Empty<string>())
                .Concat(b.Recipe?.Outputs.Keys ?? Enumerable.Empty<string>());
            foreach (var id in referenced)
            {
                if (!Resources.ContainsKey(id))
                    throw new InvalidDataException($"Building '{b.Id}' references unknown resource '{id}'");
            }
            if (b.Jobs > 0 && b.JobKind == null)
                throw new InvalidDataException($"Building '{b.Id}' has jobs but no jobKind");
            if (b.Service is { } service)
            {
                if (Needs.All(n => n.Id != service.Need))
                    throw new InvalidDataException($"Building '{b.Id}' serves unknown need '{service.Need}'");
                foreach (var id in service.ConsumesPerPersonDay.Keys)
                    if (!Resources.ContainsKey(id))
                        throw new InvalidDataException($"Building '{b.Id}' consumes unknown resource '{id}'");
            }
        }
    }
}
