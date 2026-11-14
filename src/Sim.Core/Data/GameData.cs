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

    /// <summary>Share of the stock that rots per day outside a granary (scaled by season; see seasons.json).</summary>
    public double SpoilPerDay { get; set; }

    /// <summary>
    /// Tools: while the settlement has any in stock, workers at workplaces and building sites get this much
    /// extra output (0.25 = +25%).
    /// </summary>
    public double WorkBonus { get; set; }

    /// <summary>Tools used up per worker-day of work done with them.</summary>
    public double WearPerWorkerDay { get; set; }

    public bool IsTool => WorkBonus > 0;

    /// <summary>Fair value in coin of one unit, used for fair prices and by merchants.</summary>
    public double Value { get; set; }
}

/// <summary>A social class (classes.json): who lives in a tier of home, and what they pay.</summary>
public sealed class SocialClassDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>Coin each adult of this class pays in taxes per game day.</summary>
    public double TaxPerAdultDay { get; set; }
}

/// <summary>
/// Makes a building a home tier. Homes upgrade in place to <see cref="UpgradeTo"/> once their residents have
/// had that tier's <see cref="Requires"/> needs met for a while (and the upgrade's cost is in stock), and slip back
/// when their own tier's needs go unmet.
/// </summary>
public sealed class HomeDef
{
    public int Level { get; set; }

    /// <summary>The social class of the people living here.</summary>
    public string Class { get; set; } = "peasant";

    public string? UpgradeTo { get; set; }

    /// <summary>Needs every resident must have covered to reach and keep this tier.</summary>
    public List<string> Requires { get; set; } = new();

    /// <summary>Mood bonus for living in this tier.</summary>
    public double Mood { get; set; }
}

/// <summary>A road surface (roads.json); the list index is the tier stored on each road tile.</summary>
public sealed class RoadTierDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>Walking cost per tile (open ground is 1): lower means faster walking and better hauling.</summary>
    public double WalkCost { get; set; } = 0.4;

    /// <summary>Caravan speed relative to a dirt road.</summary>
    public double CaravanSpeed { get; set; } = 1;

    /// <summary>Roadworker-days to build (or upgrade to) one tile next to the town centre.</summary>
    public double WorkDays { get; set; } = 0.05;

    /// <summary>Materials per tile, paid when the road is planned.</summary>
    public Dictionary<string, int> Cost { get; set; } = new();
}

/// <summary>A tower or gatehouse in a kind of wall (walls.json).</summary>
public sealed class WallPieceDef
{
    public string Name { get; set; } = "";
    public Dictionary<string, int> Cost { get; set; } = new();
    public double WorkDays { get; set; } = 2;
    public int HitPoints { get; set; } = 300;

    /// <summary>Soldiers it holds (for the coming battle phase).</summary>
    public int Defenders { get; set; }
}

/// <summary>A kind of town wall (walls.json): palisade, stone wall, large stone wall.</summary>
public sealed class WallTierDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>Materials per tile, paid when the wall is planned.</summary>
    public Dictionary<string, int> Cost { get; set; } = new();

    /// <summary>Crew-days to build (or upgrade to) one tile next to the town centre.</summary>
    public double WorkDays { get; set; } = 0.4;

    /// <summary>Strength per tile, for sieges (not used yet).</summary>
    public int HitPoints { get; set; } = 100;

    /// <summary>Mood of people whose home lies inside a closed ring of walls at least this strong.</summary>
    public double SafetyMood { get; set; }

    /// <summary>Materials for a gate in this kind of wall (instead of the wall's own cost).</summary>
    public Dictionary<string, int> GateCost { get; set; } = new();

    /// <summary>Wide enough on top for soldiers to stand and walk along behind a parapet (stone walls).</summary>
    public bool Walkway { get; set; }

    /// <summary>Soldiers a tile of wall-walk holds (for the coming battle phase).</summary>
    public int DefendersPerTile { get; set; }

    /// <summary>A tower raised on this kind of wall.</summary>
    public WallPieceDef Tower { get; set; } = new() { Name = "Tower" };

    /// <summary>A gate fortified with flanking towers.</summary>
    public WallPieceDef Gatehouse { get; set; } = new() { Name = "Gatehouse" };
}

/// <summary>Trade tuning (trade.json).</summary>
public sealed class TradeRules
{
    /// <summary>Open offers expire after this many game days; set-aside goods go back to stock.</summary>
    public double OfferDays { get; set; } = 10;

    /// <summary>How far a caravan travels per game day.</summary>
    public double CaravanTilesPerDay { get; set; } = 40;

    /// <summary>Buyers pay this times the fair value (what merchants charge); sellers get the fair value.</summary>
    public double MerchantMarkup { get; set; } = 1.25;

    public double MerchantVisitDays { get; set; } = 10;

    /// <summary>Coin a visiting merchant can spend per visit.</summary>
    public int MerchantPurse { get; set; } = 300;

    /// <summary>Units of goods a visiting merchant can bring per visit.</summary>
    public int MerchantCargo { get; set; } = 200;

    /// <summary>How far away merchants come from.</summary>
    public int MerchantDistance { get; set; } = 120;

    /// <summary>Goods merchants sell to towns.</summary>
    public List<string> MerchantGoods { get; set; } = new();

    public int StartingCoin { get; set; } = 50;

    /// <summary>Output of buildings with upkeep while the treasury can't pay it.</summary>
    public double UnpaidOutput { get; set; } = 0.5;
    public int MaxOpenOffers { get; set; } = 8;
    public int MaxRoutes { get; set; } = 6;
}

public sealed class RecipeDef
{
    public Dictionary<string, int> Inputs { get; set; } = new();
    public Dictionary<string, int> Outputs { get; set; } = new();

    /// <summary>
    /// Alternative sets of inputs, any one of which makes the outputs (a smokehouse smokes mutton or fish).
    /// When given, they replace <see cref="Inputs"/>.
    /// </summary>
    public List<Dictionary<string, int>> InputChoices { get; set; } = new();

    /// <summary>Every set of inputs this recipe accepts (just <see cref="Inputs"/> unless there are choices).</summary>
    public IReadOnlyList<Dictionary<string, int>> InputOptions => InputChoices.Count > 0 ? InputChoices : new[] { Inputs };

    /// <summary>Whether a good is an input in any of the recipe's options.</summary>
    public bool Uses(string resource) => InputOptions.Any(o => o.ContainsKey(resource));

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

    /// <summary>
    /// For household goods (clothing, furniture...): the service need people must have covered to get them,
    /// e.g. "market". Null for needs met directly by service buildings.
    /// </summary>
    public string? Via { get; set; }

    /// <summary>Goods each person supplied uses up per day.</summary>
    public Dictionary<string, double> Goods { get; set; } = new();

    public bool IsGoodsNeed => Via != null;
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

/// <summary>When the governor AI builds a production-chain building (data-driven, so new chains need no code).</summary>
public sealed class GovernorHint
{
    /// <summary>Not before the settlement has this many people.</summary>
    public int MinPopulation { get; set; }

    /// <summary>At most one more of these per this many people.</summary>
    public int PopulationPerBuilding { get; set; } = 100;

    /// <summary>Only add more while every output is below this stock.</summary>
    public int LowStock { get; set; } = 20;
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

    /// <summary>Food held here (up to <see cref="Storage"/>) doesn't spoil.</summary>
    public bool KeepsFood { get; set; }
    public int Housing { get; set; }
    public int Jobs { get; set; }
    public string? JobKind { get; set; }
    public int BuilderSlots { get; set; } = 2;
    public RecipeDef? Recipe { get; set; }

    /// <summary>Units of each of its own products a workplace keeps on site (data override; see OwnStorageCapacity).</summary>
    public int? OwnStorage { get; set; }

    /// <summary>
    /// How much of each good it makes a workplace holds itself (a farm's grain, a bakery's bread), outside the
    /// town's stockpiles and granaries. 30 unless the data says otherwise; 0 for buildings that make nothing.
    /// </summary>
    public int OwnStorageCapacity => OwnStorage ?? (Recipe != null ? DefaultOwnStorage : 0);

    public const int DefaultOwnStorage = 30;

    /// <summary>Extra output multiplier scaled by the fraction of fertile tiles under the footprint.</summary>
    public double FertileBonus { get; set; }

    /// <summary>Outdoor work (woodcutting, quarrying...): slowed by short winter days (see SeasonDef.Daylight).</summary>
    public bool Outdoor { get; set; }

    public NearTerrainRequirement? NearTerrain { get; set; }

    public ServiceDef? Service { get; set; }

    public TownCentreDef? TownCentre { get; set; }

    public GovernorHint? Governor { get; set; }

    /// <summary>Set for homes that are part of the upgrade ladder (hovel, cottage, townhouse).</summary>
    public HomeDef? Home { get; set; }

    /// <summary>Coin per game day the treasury pays to keep this building running once finished.</summary>
    public double Upkeep { get; set; }

    /// <summary>A Trading Post: needed to post or accept offers and routes; caravans leave from it.</summary>
    public bool Trade { get; set; }

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

    /// <summary>Buildings in data-file order (deterministic iteration).</summary>
    public IReadOnlyList<BuildingDef> BuildingList { get; }

    /// <summary>Tool resources (see <see cref="ResourceDef.WorkBonus"/>) in id order.</summary>
    public IReadOnlyList<ResourceDef> Tools { get; }

    /// <summary>Food resources ordered best meal first.</summary>
    public IReadOnlyList<ResourceDef> FoodsByPreference { get; }

    public TradeRules Trade { get; }

    /// <summary>Social classes, in data-file order.</summary>
    public IReadOnlyList<SocialClassDef> Classes { get; }

    public SocialClassDef ClassOf(string id) => Classes.FirstOrDefault(c => c.Id == id) ?? Classes[0];

    /// <summary>The tier a home downgrades to (the one whose upgrade is <paramref name="homeId"/>), or null.</summary>
    public BuildingDef? HomeBelow(string homeId) => BuildingList.FirstOrDefault(b => b.Home?.UpgradeTo == homeId);

    /// <summary>Road surfaces from dirt (tier 0) upwards.</summary>
    public IReadOnlyList<RoadTierDef> RoadTiers { get; }

    /// <summary>Fires, sickness and poor harvests.</summary>
    public DisasterRules Disasters { get; }

    /// <summary>What changes with the seasons (spring, summer, autumn, winter).</summary>
    public SeasonRules Seasons { get; }

    /// <summary>What adults without a job do (oddjobs.json).</summary>
    public OddJobRules OddJobs { get; }

    /// <summary>Town walls from palisade (index 0) upwards.</summary>
    public IReadOnlyList<WallTierDef> WallTiers { get; }

    /// <summary>Soldiers, squads and generals (units.json).</summary>
    public War.WarRules War { get; }

    /// <summary>Wages, prices and governments (households.json).</summary>
    public Economy.HouseholdRules Households { get; }

    /// <summary>NPC factions (factions.json).</summary>
    public Diplomacy.FactionRules Factions { get; }

    public GameData(IEnumerable<ResourceDef> resources, IEnumerable<BuildingDef> buildings, IEnumerable<NeedDef>? needs = null,
        TradeRules? trade = null, IEnumerable<RoadTierDef>? roadTiers = null, IEnumerable<SocialClassDef>? classes = null,
        DisasterRules? disasters = null, SeasonRules? seasons = null, OddJobRules? oddJobs = null,
        IEnumerable<WallTierDef>? wallTiers = null, War.WarRules? war = null, Economy.HouseholdRules? households = null,
        Diplomacy.FactionRules? factions = null)
    {
        Factions = factions ?? new Diplomacy.FactionRules();
        Households = households ?? new Economy.HouseholdRules();
        War = war ?? new War.WarRules();
        WallTiers = wallTiers?.ToList() ?? new List<WallTierDef> { new() { Id = "palisade", Name = "Palisade" } };
        OddJobs = oddJobs ?? new OddJobRules();
        Disasters = disasters ?? new DisasterRules();
        Seasons = seasons ?? new SeasonRules();
        Classes = classes?.ToList() ?? new List<SocialClassDef> { new() { Id = "peasant", Name = "Peasants", TaxPerAdultDay = 0.06 } };
        Trade = trade ?? new TradeRules();
        RoadTiers = roadTiers?.ToList() ?? new List<RoadTierDef> { new() { Id = "dirt", Name = "Dirt road" } };
        // The pathfinder works on tiles alone, so it gets the surface costs here (the same data for every world).
        World.Pathfinder.SetRoadTiers(RoadTiers.Select(t => t.WalkCost).ToArray(), RoadTiers.Select(t => t.CaravanSpeed).ToArray());
        Resources = resources.ToDictionary(r => r.Id);
        BuildingList = buildings.ToList();
        Buildings = BuildingList.ToDictionary(b => b.Id);
        Needs = (needs ?? Enumerable.Empty<NeedDef>()).OrderBy(n => n.Id, StringComparer.Ordinal).ToList();
        Tools = Resources.Values.Where(r => r.IsTool).OrderBy(r => r.Id, StringComparer.Ordinal).ToList();
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
        var trade = LoadEmbedded<TradeRules>("trade.json");
        var roads = LoadEmbedded<List<RoadTierDef>>("roads.json");
        var classes = LoadEmbedded<List<SocialClassDef>>("classes.json");
        var disasters = LoadEmbedded<DisasterRules>("disasters.json");
        var seasons = LoadEmbedded<SeasonRules>("seasons.json");
        var oddJobs = LoadEmbedded<OddJobRules>("oddjobs.json");
        var walls = LoadEmbedded<List<WallTierDef>>("walls.json");
        var war = LoadEmbedded<War.WarRules>("units.json");
        var households = LoadEmbedded<Economy.HouseholdRules>("households.json");
        var factions = LoadEmbedded<Diplomacy.FactionRules>("factions.json");
        return new GameData(resources, buildings, needs, trade, roads, classes, disasters, seasons, oddJobs, walls, war, households, factions);
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
        foreach (var home in BuildingList.Where(b => b.Home != null))
        {
            if (home.Home!.UpgradeTo is { } up)
            {
                if (!Buildings.TryGetValue(up, out var next) || next.Home == null)
                    throw new InvalidDataException($"Home '{home.Id}' upgrades to '{up}', which isn't a home");
                if (next.Width != home.Width || next.Height != home.Height)
                    throw new InvalidDataException($"Home '{home.Id}' and its upgrade '{up}' must be the same size");
            }
            foreach (var need in home.Home.Requires)
                if (Needs.All(n => n.Id != need)) throw new InvalidDataException($"Home '{home.Id}' requires unknown need '{need}'");
        }
        foreach (var r in Trade.MerchantGoods)
            if (!Resources.ContainsKey(r)) throw new InvalidDataException($"Merchants sell unknown resource '{r}'");

        foreach (var n in Needs)
        {
            if (n.Via != null && !Needs.Any(other => other.Id == n.Via && !other.IsGoodsNeed))
                throw new InvalidDataException($"Need '{n.Id}' is supplied via unknown service need '{n.Via}'");
            foreach (var r in n.Goods.Keys)
                if (!Resources.ContainsKey(r)) throw new InvalidDataException($"Need '{n.Id}' uses unknown resource '{r}'");
        }

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
                .Concat(b.Recipe?.InputOptions.SelectMany(o => o.Keys) ?? Enumerable.Empty<string>())
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

/// <summary>Chances and effects of disasters (disasters.json). They are mild: nothing is lost for good.</summary>
public sealed class DisasterRules
{
    /// <summary>Daily chance that a working building catches fire.</summary>
    public double FireChancePerBuildingDay { get; set; } = 0.0001;

    /// <summary>Fire chance multiplier for buildings within reach of a working well.</summary>
    public double WellFireFactor { get; set; } = 0.5;

    /// <summary>Share of the building's construction work needed to repair it after a fire.</summary>
    public double RepairShare { get; set; } = 0.5;

    /// <summary>Share of the goods a burning stockpile holds (its part of the town's storage) that is lost.</summary>
    public double StockpileFireLoss { get; set; } = 0.25;

    /// <summary>Daily chance of an outbreak in a town at normal crowding with no water or faith.</summary>
    public double SicknessChancePerDay { get; set; } = 0.012;

    public int SicknessMinPopulation { get; set; } = 30;

    /// <summary>How much full water coverage lowers the chance of sickness (0.5 = halves it).</summary>
    public double WaterProtection { get; set; } = 0.5;

    public double FaithProtection { get; set; } = 0.3;
    public double SickShareMin { get; set; } = 0.02;
    public double SickShareMax { get; set; } = 0.05;
    public int SickDaysMin { get; set; } = 5;
    public int SickDaysMax { get; set; } = 10;
    public double SickMood { get; set; } = -10;

    /// <summary>Chance, at the start of each autumn, that the harvest is poor.</summary>
    public double PoorHarvestChance { get; set; } = 0.15;

    public int PoorHarvestDays { get; set; } = 30;

    /// <summary>Output of farms and gardens (buildings with a fertile bonus) during a poor harvest.</summary>
    public double PoorHarvestOutput { get; set; } = 0.7;

    /// <summary>Bathhouses: how much full hygiene coverage lowers the chance of sickness.</summary>
    public double HygieneProtection { get; set; } = 0.4;

    /// <summary>Herbalists: how much full coverage lowers the chance of sickness, and days of illness shed per day.</summary>
    public double HealingProtection { get; set; } = 0.2;
    public int HealingRecovery { get; set; } = 2;

    // Crime (CrimeSystem): troubled adults (unhappy, or from a penniless family) out of the watch's reach may steal.
    public int CrimeMinPopulation { get; set; } = 100;
    public double CrimeChancePerTroubled { get; set; } = 0.002;
    public double CrimeMaxChance { get; set; } = 0.4;
    public double TroubledMood { get; set; } = 40;
    public double TroubledPurse { get; set; } = 0.5;

    /// <summary>A thief takes this share of a family's savings (at most <see cref="MaxTheft"/>).</summary>
    public double TheftShare { get; set; } = 0.25;
    public double MaxTheft { get; set; } = 20;
    public double CrimeMood { get; set; } = -2;
    public int CrimeMoodDays { get; set; } = 10;
}

/// <summary>One season's effects (seasons.json).</summary>
public sealed class SeasonDef
{
    public string Name { get; set; } = "";

    /// <summary>Output multiplier by building id (e.g. farms grow little in winter). Unlisted buildings: 1.</summary>
    public Dictionary<string, double> Output { get; set; } = new();

    /// <summary>Firewood each occupied home burns per day.</summary>
    public double HeatingWoodPerHomeDay { get; set; }

    /// <summary>How fast food rots outside granaries (1 = each food's own rate; 0 in winter, when it freezes).</summary>
    public double Spoilage { get; set; } = 1;

    /// <summary>Work speed outdoors (building sites, roads, outdoor workplaces): short winter days, long summer ones.</summary>
    public double Daylight { get; set; } = 1;

    /// <summary>Extra mood from a covered need this season (a warm tavern on winter nights), by need id.</summary>
    public Dictionary<string, double> ServiceMood { get; set; } = new();

    /// <summary>Goods a service uses per person, multiplied by this season (more ale in winter), by need id.</summary>
    public Dictionary<string, double> ServiceUse { get; set; } = new();

    /// <summary>Mood of people with none of the <see cref="ServiceMood"/> needs covered ("Long winter nights").</summary>
    public double LongNightsMood { get; set; }

    public double OutputOf(string buildingId) => Output.TryGetValue(buildingId, out double f) ? f : 1;
}

/// <summary>The seasons, in order from spring (seasons.json).</summary>
public sealed class SeasonRules
{
    /// <summary>Spring, summer, autumn and winter. With none, nothing changes through the year.</summary>
    public List<SeasonDef> Seasons { get; set; } = new();

    /// <summary>Mood of people whose home has no firewood (or who have no home) while homes need heating.</summary>
    public double ColdMood { get; set; } = -12;

    /// <summary>Days of food the governor makes room for before winter.</summary>
    public int WinterReserveDays { get; set; } = 15;

    public SeasonDef Of(Time.Season season) =>
        (int)season < Seasons.Count ? Seasons[(int)season] : new SeasonDef { Name = season.ToString() };

    /// <summary>A building's output averaged over the year (for planning).</summary>
    public double AverageOutputOf(string buildingId) =>
        Seasons.Count == 0 ? 1 : Seasons.Average(s => s.OutputOf(buildingId));
}

/// <summary>A good adults without a job can gather (oddjobs.json).</summary>
public sealed class GatherDef
{
    public string Resource { get; set; } = "";
    public double PerDay { get; set; }

    /// <summary>Nothing to find in winter (berries and mushrooms).</summary>
    public bool NotInWinter { get; set; }
}

/// <summary>What adults without a job do: help at building sites and on roads, or gather.</summary>
public sealed class OddJobRules
{
    /// <summary>A helper's labour as a share of an average worker's.</summary>
    public double HelperRate { get; set; } = 0.5;

    /// <summary>One road helper per this many planned road tiles...</summary>
    public int PlannedRoadsPerHelper { get; set; } = 10;

    /// <summary>...up to this many.</summary>
    public int MaxRoadHelpers { get; set; } = 6;

    /// <summary>What gatherers bring in, in order of preference.</summary>
    public List<GatherDef> Gather { get; set; } = new();
}
