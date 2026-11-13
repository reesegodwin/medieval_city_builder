using Sim.Core.Citizens;
using Sim.Core.Economy;

namespace Sim.Core.Settlements;

/// <summary>Player- or AI-set rules the settlement follows on its own (the "tasks and rules" design).</summary>
public sealed class SettlementRules
{
    /// <summary>Higher number = filled first. Unlisted job kinds default to 0.</summary>
    public SortedDictionary<string, int> JobPriority { get; } = new(StringComparer.Ordinal)
    {
        [JobKinds.Builder] = 6,
        ["farmer"] = 5,
        ["baker"] = 4,
        ["gardener"] = 4,
        ["fisher"] = 4,
        ["priest"] = 4,
        ["woodcutter"] = 3,
        ["firewood"] = 3,
        ["barkeep"] = 3,
        ["trader"] = 3,
        ["smith"] = 3,
        ["quarrier"] = 2,
        ["brewer"] = 2,
        ["miner"] = 2,
        ["shepherd"] = 4, // mutton is food
        ["smoker"] = 3,
        ["weaver"] = 2,
        ["carpenter"] = 2,
        [JobKinds.Roadworker] = 4,
        [JobKinds.Hauler] = 1, // only people nobody else needs; the first to be pulled into other work
    };

    /// <summary>Production may not consume a resource below this stock (e.g. keep grain for eating).</summary>
    public SortedDictionary<string, int> Reserves { get; } = new(StringComparer.Ordinal)
    {
        ["grain"] = 20,
    };

    /// <summary>
    /// Stock targets (like Dwarf Fortress work orders): workplaces stop and release their workers
    /// once everything they make is at or above its limit.
    /// </summary>
    public SortedDictionary<string, int> ProductionLimits { get; } = new(StringComparer.Ordinal)
    {
        ["grain"] = 500,
        ["bread"] = 300,
        ["vegetables"] = 200,
        ["fish"] = 200,
        ["mutton"] = 150,
        ["smoked_meat"] = 300,
        ["berries"] = 100,
        ["ale"] = 150,
        ["wood"] = 250,
        ["firewood"] = 200,
        ["stone"] = 200,
        ["iron_ore"] = 80,
        ["tools"] = 60,
        ["wool"] = 60,
        ["cloth"] = 60,
        ["furniture"] = 40,
    };

    /// <summary>Per-citizen stock targets; the effective limit is the larger of this and the flat limit.</summary>
    public SortedDictionary<string, int> PerCitizenLimits { get; } = new(StringComparer.Ordinal)
    {
        // Room for a winter's reserve: farms yield little in winter (see seasons.json).
        ["grain"] = 10,
        ["bread"] = 10,
        ["vegetables"] = 6,
        ["fish"] = 5,
        ["mutton"] = 4,
        ["smoked_meat"] = 6, // keeps for months: a store for winter
        ["berries"] = 1,
        ["ale"] = 1,
        ["firewood"] = 6, // a winter's heating
    };

    /// <summary>Below this stock a resource counts as scarce and its producers get extra job priority.</summary>
    public int LowStockThreshold { get; set; } = 40;

    /// <summary>Whether the settlement's governor AI may place new buildings on its own.</summary>
    public bool AutoBuild { get; set; } = true;

    /// <summary>Whether homes upgrade on their own once their residents' needs are met (paying the upgrade cost).</summary>
    public bool AutoUpgradeHomes { get; set; } = true;

    /// <summary>Whether fires, sickness and poor harvests can strike this settlement.</summary>
    public bool Disasters { get; set; } = true;

    /// <summary>
    /// Whether spare territory allowance is claimed automatically, evenly around the centre. Turn it off to
    /// paint territory by hand (e.g. only along one bank of a river).
    /// </summary>
    public bool AutoExpandTerritory { get; set; } = true;

    public int PriorityOf(string jobKind) => JobPriority.TryGetValue(jobKind, out int p) ? p : 0;
    public int ReserveOf(string resource) => Reserves.TryGetValue(resource, out int r) ? r : 0;
    public int LimitOf(string resource, int population)
    {
        int flat = ProductionLimits.TryGetValue(resource, out int l) ? l : int.MaxValue;
        int perCitizen = PerCitizenLimits.TryGetValue(resource, out int p) ? p * population : 0;
        return Math.Max(flat, perCitizen);
    }
}

public sealed class Settlement
{
    public int Id { get; }
    public string Name { get; }

    /// <summary>The player who controls this settlement (their account name), or null for single-player / NPC towns.</summary>
    public string? OwnerId { get; set; }
    public int CenterX { get; }
    public int CenterY { get; }

    public Inventory Stock { get; } = new();

    /// <summary>Daily books of what came in and went out, by source (see <see cref="Ledger"/>).</summary>
    public Ledger Ledger { get; set; } = new();
    public SettlementRules Rules { get; } = new();

    private readonly List<Building> _buildings = new();
    private readonly List<Citizen> _citizens = new();
    private readonly Dictionary<int, Building> _buildingsById = new();
    private readonly Dictionary<int, Citizen> _citizensById = new();

    /// <summary>Buildings in creation order (deterministic iteration).</summary>
    public IReadOnlyList<Building> Buildings => _buildings;

    /// <summary>Living citizens in arrival order.</summary>
    public IReadOnlyList<Citizen> Citizens => _citizens;

    /// <summary>Road tiles belonging to this settlement, in sorted order.</summary>
    public SortedSet<(int X, int Y)> Roads { get; } = new();

    /// <summary>Governor memory: day a site search last failed, per "building@spot", so it isn't retried daily.</summary>
    public Dictionary<string, long> FailedSiteSearches { get; } = new();

    /// <summary>Road tiles planned but not yet built, in the order they'll be built.</summary>
    public List<RoadPlan> RoadPlans { get; } = new();

    /// <summary>Bumped whenever roads change, so renderers know to rebuild.</summary>
    public int RoadVersion { get; set; }

    /// <summary>Finished walls and gates by tile.</summary>
    public SortedDictionary<(int X, int Y), WallSegment> Walls { get; } = new();

    /// <summary>Wall tiles (new walls, upgrades, gates) waiting to be built, in build order.</summary>
    public List<WallPlan> WallPlans { get; } = new();

    /// <summary>Bumped whenever walls change (renderers and the enclosure check rebuild on it).</summary>
    public int WallVersion { get; set; }

    /// <summary>The ring of walls the governor has laid out for this town (tile, gate?), built in batches.</summary>
    public List<(int X, int Y, bool Gate)> WallRing { get; } = new();

    /// <summary>Derived enclosure (see Systems.WallSystem); never saved, rebuilt when walls or territory change.</summary>
    internal Systems.WallSystem.Enclosure? EnclosureCache { get; set; }

    public Settlement(int id, string name, int centerX, int centerY)
    {
        Id = id;
        Name = name;
        CenterX = centerX;
        CenterY = centerY;
    }

    /// <summary>Tiles this settlement has claimed, in sorted order. Buildings and roads may only go on them.</summary>
    public SortedSet<(int X, int Y)> Territory { get; } = new();

    /// <summary>Bumped whenever territory changes, so renderers know to rebuild.</summary>
    public int TerritoryVersion { get; set; }

    /// <summary>
    /// Most territory the settlement may hold, set by its best town-centre tier so far (it doesn't drop while
    /// the centre is being upgraded).
    /// </summary>
    public int TerritoryCap { get; set; }

    /// <summary>Territory the settlement may hold right now: grows with population, up to <see cref="TerritoryCap"/>.</summary>
    public int TerritoryAllowance => Math.Min(TerritoryCap, Commands.TerritoryRules.PopulationAllowance(Citizens.Count));

    /// <summary>Farthest claimed tile from the centre (Chebyshev distance), for bounding searches.</summary>
    public int TerritoryExtent { get; internal set; }

    public bool OwnsTile(World.WorldMap map, int x, int y) => map.GetTile(x, y).TerritoryId == Id;

    /// <summary>The settlement's town centre (keep, town hall...), or null if it has none.</summary>
    public Building? TownCentre => Buildings.FirstOrDefault(b => b.Def.IsTownCentre);

    /// <summary>General storage: stockpiles and granaries.</summary>
    public int StorageCapacity => Buildings.Where(b => b.IsOperational).Sum(b => b.Def.Storage);

    /// <summary>
    /// General storage still free. Goods first fill the stores of the workplaces that make them (see
    /// <see cref="Data.BuildingDef.OwnStorageCapacity"/>); only the rest takes stockpile and granary space.
    /// </summary>
    public int FreeStorage => Math.Max(0, StorageCapacity - (Stock.Total - StoredAtWorkplaces));

    /// <summary>Space for each good at the workplaces that make it (operational ones), by resource.</summary>
    public Dictionary<string, int> OwnStorage()
    {
        var room = new Dictionary<string, int>();
        foreach (var b in Buildings)
        {
            if (!b.IsOperational || b.Def.Recipe is not { } recipe || b.Def.OwnStorageCapacity <= 0) continue;
            foreach (string r in recipe.Outputs.Keys) room[r] = room.GetValueOrDefault(r) + b.Def.OwnStorageCapacity;
        }
        return room;
    }

    public int OwnStorageOf(string resource) => OwnStorage().GetValueOrDefault(resource);

    /// <summary>Goods held at workplaces rather than in stockpiles.</summary>
    public int StoredAtWorkplaces => OwnStorage().Sum(kv => Math.Min(kv.Value, Stock.Get(kv.Key)));

    /// <summary>How much more of a good the town can store: its free workplace space plus general space.</summary>
    public int RoomFor(string resource) => FreeStorage + Math.Max(0, OwnStorageOf(resource) - Stock.Get(resource));

    public int HousingCapacity => Buildings.Where(b => b.IsOperational).Sum(b => b.Def.Housing);
    public int FreeHousing => Buildings.Sum(b => b.FreeHousing);

    public Building? FindBuilding(int id) => _buildingsById.GetValueOrDefault(id);
    public Citizen? FindCitizen(int id) => _citizensById.GetValueOrDefault(id);

    public void AddBuilding(Building b)
    {
        _buildings.Add(b);
        _buildingsById.Add(b.Id, b);
        BuildingsVersion++;
    }

    /// <summary>Bumped when buildings are added or removed (only to invalidate derived caches; not saved).</summary>
    internal int BuildingsVersion { get; private set; }

    public bool RemoveBuilding(Building b)
    {
        BuildingsVersion++;
        _buildingsById.Remove(b.Id);
        return _buildings.Remove(b);
    }

    public void AddCitizen(Citizen c)
    {
        _citizens.Add(c);
        _citizensById.Add(c.Id, c);
    }

    public bool RemoveCitizen(Citizen c)
    {
        _citizensById.Remove(c.Id);
        return _citizens.Remove(c);
    }

    public bool HasCitizen(Citizen c) => _citizensById.ContainsKey(c.Id);

    public IEnumerable<Building> Operational(string defId) =>
        Buildings.Where(b => b.IsOperational && b.Def.Id == defId);

    public int CountIncludingPlanned(string defId) => Buildings.Count(b => b.Def.Id == defId);

    /// <summary>True if every output of this workplace has reached its production limit.</summary>
    public bool IsAtProductionLimit(Building b)
    {
        if (!b.IsOperational || b.Def.Recipe is not { } recipe || recipe.Outputs.Count == 0) return false;
        foreach (var resource in recipe.Outputs.Keys)
        {
            if (Stock.Get(resource) < LimitOf(resource)) return false;
        }
        return true;
    }

    public int LimitOf(string resource) => Rules.LimitOf(resource, Citizens.Count);

    /// <summary>Haulers each stockpile should have, sized to the carrying there is to do (rebuilt hourly).</summary>
    public Dictionary<int, int> HaulerQuota { get; } = new();

    /// <summary>
    /// Share (0..1) of the carrying that workplaces would otherwise do themselves that haulers currently cover.
    /// Set hourly by the job system.
    /// </summary>
    public double HaulCoverage { get; set; }

    /// <summary>Wear on tools not yet amounting to a whole tool, per tool resource.</summary>
    public SortedDictionary<string, double> ToolWear { get; } = new(StringComparer.Ordinal);

    /// <summary>Taxes collected but not yet a whole coin.</summary>
    public double TaxAccrued { get; set; }

    /// <summary>Upkeep owed but not yet paid (fractions, or what the treasury couldn't cover).</summary>
    public double UpkeepOwed { get; set; }

    /// <summary>The treasury couldn't pay upkeep at the last daily reckoning; buildings with upkeep run at reduced output.</summary>
    public bool UpkeepUnpaid { get; set; }

    /// <summary>Goods the player has been warned are running out (see <see cref="Economy.Forecast"/>).</summary>
    public SortedSet<string> ShortageWarnings { get; } = new(StringComparer.Ordinal);

    /// <summary>Days left of a poor harvest (farms and gardens produce less).</summary>
    public int PoorHarvestDays { get; set; }

    /// <summary>Firewood burned but not yet taken from stock (less than one unit).</summary>
    public double HeatingOwed { get; set; }

    /// <summary>Food rotted but not yet taken from stock (less than one unit per food).</summary>
    public SortedDictionary<string, double> SpoilageOwed { get; } = new(StringComparer.Ordinal);

    /// <summary>Food granaries can hold safe from rot.</summary>
    public int FoodStoreCapacity => Buildings.Where(b => b.IsOperational && b.Def.KeepsFood).Sum(b => b.Def.Storage);

    /// <summary>Household goods used but not yet amounting to a whole unit, per resource.</summary>
    public SortedDictionary<string, double> GoodsUse { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Job slots the settlement wants filled. Workplaces that reached their stock limit keep their workers on
    /// standby (they keep the job, and any other job may take them at once); haulers follow the hauling quota.
    /// </summary>
    public int WantedWorkers(Building b) => b.CurrentJobKind switch
    {
        JobKinds.Hauler => Math.Min(b.JobSlots, HaulerQuota.GetValueOrDefault(b.Id)),
        // About one roadworker per ten planned tiles, none when there's nothing to build.
        // Roadworkers also build walls.
        JobKinds.Roadworker => RoadPlans.Count + WallPlans.Count == 0 ? 0 : Math.Min(b.JobSlots, 1 + (RoadPlans.Count + WallPlans.Count) / 10),
        _ => b.JobSlots,
    };

    /// <summary>Workers here have nothing to do right now and can be moved to other work freely.</summary>
    public bool IsOnStandby(Building b) => IsAtProductionLimit(b) && !b.InputsLoaded;

    public override string ToString() => Name;
}

/// <summary>A finished wall tile.</summary>
public sealed class WallSegment
{
    /// <summary>Index into walls.json (0 palisade, 1 stone wall, 2 large stone wall).</summary>
    public byte Tier { get; set; }

    public bool Gate { get; set; }

    /// <summary>Strength left, for sieges (not used yet).</summary>
    public int Hp { get; set; }
}

/// <summary>A wall tile waiting to be built (new, upgraded or turned into a gate) and the work done so far.</summary>
public sealed class WallPlan
{
    public int X { get; set; }
    public int Y { get; set; }

    /// <summary>Index into walls.json.</summary>
    public byte Tier { get; set; }

    public bool Gate { get; set; }
    public double Progress { get; set; }
}

/// <summary>A road tile waiting to be built, and the roadworker-days spent on it so far.</summary>
public sealed class RoadPlan
{
    public int X { get; set; }
    public int Y { get; set; }
    public double Progress { get; set; }

    /// <summary>Surface to build: a new road at this tier, or an upgrade of the existing one.</summary>
    public byte Tier { get; set; }
}
