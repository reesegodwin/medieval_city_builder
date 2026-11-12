using Sim.Core.Commands;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sim.Core.Citizens;
using Sim.Core.Events;
using Sim.Core.Settlements;
using Sim.Core.Simulation;
using Sim.Core.Time;

namespace Sim.Core.Persistence;

/// <summary>
/// Saves and loads a whole world as JSON. Terrain isn't stored: it is regenerated from the seed, and
/// only what changed (buildings, roads, people, stock, rules, the clock and the random state) is saved.
/// A loaded world continues exactly as the original would have.
/// </summary>
public static class SaveGame
{
    /// <summary>Bump when the format changes, and add a migration in <see cref="Migrate"/>.</summary>
    public const int CurrentVersion = 5;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string ToJson(GameWorld world) => JsonSerializer.Serialize(Capture(world), Options);

    public static GameWorld FromJson(string json, Data.GameData? data = null)
    {
        var file = JsonSerializer.Deserialize<SaveFile>(json, Options) ?? throw new InvalidDataException("Empty save file");
        return Restore(Migrate(file), data);
    }

    public static void Save(GameWorld world, string path) => File.WriteAllText(path, ToJson(world));
    public static GameWorld Load(string path) => FromJson(File.ReadAllText(path));

    private static SaveFile Migrate(SaveFile file)
    {
        if (file.Version > CurrentVersion)
            throw new InvalidDataException($"Save is from a newer version ({file.Version}) of the game");
        // Version 1 had a fixed territory radius and no town centre. Mark those settlements so that, once
        // restored, they get the territory the old radius gave them (see RestoreSettlement).
        if (file.Version < 2)
            foreach (var s in file.Settlements) s.Territory = null;
        file.Version = CurrentVersion;
        return file;
    }

    // ---------- capture ----------

    private static SaveFile Capture(GameWorld world) => new()
    {
        Version = CurrentVersion,
        Seed = world.Seed,
        Tick = world.Time.Tick,
        LastId = world.LastId,
        Random = world.Rng.GetState(),
        TotalBirths = world.TotalBirths,
        TotalDeaths = world.TotalDeaths,
        Settings = world.Settings.Clone(),
        LastActiveUtc = world.LastActiveUtc,
        Settlements = world.Settlements.Select(CaptureSettlement).ToList(),
        Events = world.Events.Select(e => new EventSave(e.Time.Tick, e.Kind, e.SettlementId, e.Message)).ToList(),
    };

    private static SettlementSave CaptureSettlement(Settlement s) => new()
    {
        Id = s.Id,
        Name = s.Name,
        OwnerId = s.OwnerId,
        CenterX = s.CenterX,
        CenterY = s.CenterY,
        Stock = s.Stock.Items.ToDictionary(kv => kv.Key, kv => kv.Value),
        Rules = new RulesSave
        {
            JobPriority = new(s.Rules.JobPriority),
            Reserves = new(s.Rules.Reserves),
            ProductionLimits = new(s.Rules.ProductionLimits),
            PerCitizenLimits = new(s.Rules.PerCitizenLimits),
            LowStockThreshold = s.Rules.LowStockThreshold,
            AutoBuild = s.Rules.AutoBuild,
            AutoExpandTerritory = s.Rules.AutoExpandTerritory,
        },
        Buildings = s.Buildings.Select(b => new BuildingSave
        {
            Id = b.Id,
            Def = b.Def.Id,
            X = b.X,
            Y = b.Y,
            State = b.State,
            ConstructionProgress = b.ConstructionProgress,
            CycleProgress = b.CycleProgress,
            InputsLoaded = b.InputsLoaded,
            TerrainMultiplier = b.TerrainMultiplier,
            HaulFactor = b.HaulFactor,
            NoRoute = b.NoRoute,
            ServedCount = b.ServedCount,
            Problem = b.Problem,
            WorkerIds = b.WorkerIds.ToList(),
            ResidentIds = b.ResidentIds.ToList(),
        }).ToList(),
        Citizens = s.Citizens.Select(c => new CitizenSave
        {
            Id = c.Id,
            FirstName = c.FirstName,
            FamilyName = c.FamilyName,
            Sex = c.Sex,
            AgeDays = c.AgeDays,
            Traits = c.Traits.ToList(),
            Skills = new(c.Skills),
            JobBuildingId = c.JobBuildingId,
            HomeBuildingId = c.HomeBuildingId,
            SpouseId = c.SpouseId,
            ChildIds = c.ChildIds.ToList(),
            ParentIds = c.ParentIds.ToList(),
            CoveredNeeds = c.CoveredNeeds.ToList(),
            RecentFoods = new(c.RecentFoods),
            Satiation = c.Satiation,
            Mood = c.Mood,
            LastMealMood = c.LastMealMood,
            StarvingTicks = c.StarvingTicks,
            UnhappyDays = c.UnhappyDays,
            History = c.History.ToList(),
        }).ToList(),
        Roads = s.Roads.Select(r => new[] { r.X, r.Y }).ToList(),
        RoadVersion = s.RoadVersion,
        Territory = TerritoryRuns(s),
        TerritoryVersion = s.TerritoryVersion,
        TerritoryCap = s.TerritoryCap,
        HaulCoverage = s.HaulCoverage,
        ToolWear = new(s.ToolWear),
        GoodsUse = new(s.GoodsUse),
        FailedSiteSearches = new(s.FailedSiteSearches),
    };

    /// <summary>Territory as row runs [y, firstX, lastX], which is far smaller than one entry per tile.</summary>
    private static List<int[]> TerritoryRuns(Settlement s)
    {
        var runs = new List<int[]>();
        int[]? run = null;
        foreach (var (x, y) in s.Territory.OrderBy(t => t.Y).ThenBy(t => t.X))
        {
            if (run != null && run[0] == y && run[2] == x - 1) run[2] = x;
            else runs.Add(run = new[] { y, x, x });
        }
        return runs;
    }

    // ---------- restore ----------

    private static GameWorld Restore(SaveFile file, Data.GameData? data)
    {
        var world = new GameWorld(file.Seed, data);
        world.Restore(file.Tick, file.LastId);
        world.Rng.SetState(file.Random);
        world.TotalBirths = file.TotalBirths;
        world.TotalDeaths = file.TotalDeaths;
        // Version 4 and older had no world settings: they keep the defaults (1 day per hour, paused while away).
        world.Settings = file.Settings ?? new WorldSettings();
        world.LastActiveUtc = file.LastActiveUtc;

        foreach (var save in file.Settlements) world.Settlements.Add(RestoreSettlement(world, save));
        foreach (var e in file.Events)
            world.Events.Add(new GameEvent(new GameTime(e.Tick), e.Kind, e.SettlementId, e.Message));
        return world;
    }

    private static Settlement RestoreSettlement(GameWorld world, SettlementSave save)
    {
        var s = new Settlement(save.Id, save.Name, save.CenterX, save.CenterY) { OwnerId = save.OwnerId };
        foreach (var (resource, amount) in save.Stock) s.Stock.Add(resource, amount);

        CopyInto(save.Rules.JobPriority, s.Rules.JobPriority);
        CopyInto(save.Rules.Reserves, s.Rules.Reserves);
        CopyInto(save.Rules.ProductionLimits, s.Rules.ProductionLimits);
        CopyInto(save.Rules.PerCitizenLimits, s.Rules.PerCitizenLimits);
        s.Rules.LowStockThreshold = save.Rules.LowStockThreshold;
        s.Rules.AutoBuild = save.Rules.AutoBuild;
        s.Rules.AutoExpandTerritory = save.Rules.AutoExpandTerritory ?? true;

        foreach (var b in save.Buildings)
        {
            var building = new Building(b.Id, world.Data.Building(b.Def), b.X, b.Y)
            {
                State = b.State,
                ConstructionProgress = b.ConstructionProgress,
                CycleProgress = b.CycleProgress,
                InputsLoaded = b.InputsLoaded,
                TerrainMultiplier = b.TerrainMultiplier,
                HaulFactor = b.HaulFactor,
                NoRoute = b.NoRoute,
                ServedCount = b.ServedCount,
                Problem = b.Problem,
            };
            building.WorkerIds.AddRange(b.WorkerIds);
            building.ResidentIds.AddRange(b.ResidentIds);
            s.AddBuilding(building);

            for (int y = b.Y; y < b.Y + building.Def.Height; y++)
            for (int x = b.X; x < b.X + building.Def.Width; x++)
                world.Map.GetTile(x, y).BuildingId = b.Id;
        }

        foreach (var c in save.Citizens)
        {
            var citizen = new Citizen(c.Id, c.FirstName, c.FamilyName, c.Sex, c.AgeDays)
            {
                JobBuildingId = c.JobBuildingId,
                HomeBuildingId = c.HomeBuildingId,
                SpouseId = c.SpouseId,
                Satiation = c.Satiation,
                Mood = c.Mood,
                LastMealMood = c.LastMealMood,
                StarvingTicks = c.StarvingTicks,
                UnhappyDays = c.UnhappyDays,
            };
            citizen.Traits.AddRange(c.Traits);
            CopyInto(c.Skills, citizen.Skills);
            citizen.ChildIds.AddRange(c.ChildIds);
            citizen.ParentIds.AddRange(c.ParentIds);
            foreach (string need in c.CoveredNeeds) citizen.CoveredNeeds.Add(need);
            CopyInto(c.RecentFoods, citizen.RecentFoods);
            citizen.History.AddRange(c.History);
            s.AddCitizen(citizen);
        }

        foreach (var r in save.Roads)
        {
            world.Map.GetTile(r[0], r[1]).IsRoad = true;
            s.Roads.Add((r[0], r[1]));
        }
        s.RoadVersion = save.RoadVersion;
        foreach (var (key, day) in save.FailedSiteSearches) s.FailedSiteSearches[key] = day;

        // Version 2 and older had no haulers: coverage starts at 0 and is set at the next hour.
        s.HaulCoverage = save.HaulCoverage;
        // Version 3 and older had no tools or household goods; both start with nothing carried over.
        CopyInto(save.ToolWear, s.ToolWear);
        CopyInto(save.GoodsUse, s.GoodsUse);

        if (save.Territory is { } runs)
        {
            foreach (var run in runs)
                for (int x = run[1]; x <= run[2]; x++) TerritoryRules.Claim(world, s, x, run[0]);
            s.TerritoryCap = save.TerritoryCap;
            s.TerritoryVersion = save.TerritoryVersion;
        }
        else
        {
            // Old save: no town centre, so keep the old maximum, and claim the diamond its radius gave.
            s.TerritoryCap = TerritoryRules.DefaultCap;
            TerritoryRules.ExpandEvenly(world, s);
        }
        return s;
    }

    private static void CopyInto<TKey, TValue>(IDictionary<TKey, TValue> from, IDictionary<TKey, TValue> to)
    {
        to.Clear();
        foreach (var (k, v) in from) to[k] = v;
    }

    // ---------- file format ----------

    private sealed class SaveFile
    {
        public int Version { get; set; }
        public ulong Seed { get; set; }
        public long Tick { get; set; }
        public int LastId { get; set; }
        public ulong[] Random { get; set; } = Array.Empty<ulong>();
        public int TotalBirths { get; set; }
        public int TotalDeaths { get; set; }
        public WorldSettings? Settings { get; set; }
        public DateTimeOffset? LastActiveUtc { get; set; }
        public List<SettlementSave> Settlements { get; set; } = new();
        public List<EventSave> Events { get; set; } = new();
    }

    private sealed record EventSave(long Tick, EventKind Kind, int SettlementId, string Message);

    private sealed class SettlementSave
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string? OwnerId { get; set; }
        public int CenterX { get; set; }
        public int CenterY { get; set; }
        public Dictionary<string, int> Stock { get; set; } = new();
        public RulesSave Rules { get; set; } = new();
        public List<BuildingSave> Buildings { get; set; } = new();
        public List<CitizenSave> Citizens { get; set; } = new();
        public List<int[]> Roads { get; set; } = new();
        public int RoadVersion { get; set; }

        /// <summary>Row runs [y, firstX, lastX]; null in version-1 saves.</summary>
        public List<int[]>? Territory { get; set; }
        public int TerritoryVersion { get; set; }
        public int TerritoryCap { get; set; }
        public double HaulCoverage { get; set; }
        public SortedDictionary<string, double> ToolWear { get; set; } = new(StringComparer.Ordinal);
        public SortedDictionary<string, double> GoodsUse { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, long> FailedSiteSearches { get; set; } = new();
    }

    private sealed class RulesSave
    {
        public SortedDictionary<string, int> JobPriority { get; set; } = new(StringComparer.Ordinal);
        public SortedDictionary<string, int> Reserves { get; set; } = new(StringComparer.Ordinal);
        public SortedDictionary<string, int> ProductionLimits { get; set; } = new(StringComparer.Ordinal);
        public SortedDictionary<string, int> PerCitizenLimits { get; set; } = new(StringComparer.Ordinal);
        public int LowStockThreshold { get; set; }
        public bool AutoBuild { get; set; }
        public bool? AutoExpandTerritory { get; set; }
    }

    private sealed class BuildingSave
    {
        public int Id { get; set; }
        public string Def { get; set; } = "";
        public int X { get; set; }
        public int Y { get; set; }
        public BuildingState State { get; set; }
        public double ConstructionProgress { get; set; }
        public double CycleProgress { get; set; }
        public bool InputsLoaded { get; set; }
        public double TerrainMultiplier { get; set; }
        public double HaulFactor { get; set; }
        public bool NoRoute { get; set; }
        public int ServedCount { get; set; }
        public string? Problem { get; set; }
        public List<int> WorkerIds { get; set; } = new();
        public List<int> ResidentIds { get; set; } = new();
    }

    private sealed class CitizenSave
    {
        public int Id { get; set; }
        public string FirstName { get; set; } = "";
        public string FamilyName { get; set; } = "";
        public Sex Sex { get; set; }
        public long AgeDays { get; set; }
        public List<Trait> Traits { get; set; } = new();
        public SortedDictionary<string, double> Skills { get; set; } = new(StringComparer.Ordinal);
        public int? JobBuildingId { get; set; }
        public int? HomeBuildingId { get; set; }
        public int? SpouseId { get; set; }
        public List<int> ChildIds { get; set; } = new();
        public List<int> ParentIds { get; set; } = new();
        public List<string> CoveredNeeds { get; set; } = new();
        public SortedDictionary<string, long> RecentFoods { get; set; } = new(StringComparer.Ordinal);
        public double Satiation { get; set; }
        public double Mood { get; set; }
        public double LastMealMood { get; set; }
        public long StarvingTicks { get; set; }
        public int UnhappyDays { get; set; }
        public List<string> History { get; set; } = new();
    }
}
