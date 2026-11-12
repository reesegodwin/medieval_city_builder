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

public sealed class NearTerrainRequirement
{
    public Terrain Terrain { get; set; }
    public int Radius { get; set; }
    public int Min { get; set; }
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

    public bool IsStockpile => Storage > 0;
    public bool IsWorkplace => Jobs > 0 && JobKind != null;
}

/// <summary>All data-driven definitions, loaded from the JSON files embedded in this assembly.</summary>
public sealed class GameData
{
    public IReadOnlyDictionary<string, ResourceDef> Resources { get; }
    public IReadOnlyDictionary<string, BuildingDef> Buildings { get; }

    /// <summary>Food resources ordered best meal first.</summary>
    public IReadOnlyList<ResourceDef> FoodsByPreference { get; }

    public GameData(IEnumerable<ResourceDef> resources, IEnumerable<BuildingDef> buildings)
    {
        Resources = resources.ToDictionary(r => r.Id);
        Buildings = buildings.ToDictionary(b => b.Id);
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
        return new GameData(resources, buildings);
    }

    private static T LoadEmbedded<T>(string fileName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        string name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("." + fileName, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        return JsonSerializer.Deserialize<T>(stream, JsonOptions)
               ?? throw new InvalidDataException($"Could not parse {fileName}");
    }

    private void Validate()
    {
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
        }
    }
}
