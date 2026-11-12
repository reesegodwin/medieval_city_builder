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
        ["priest"] = 4,
        ["woodcutter"] = 3,
        ["barkeep"] = 3,
        ["trader"] = 3,
        ["smith"] = 3,
        ["quarrier"] = 2,
        ["brewer"] = 2,
        ["miner"] = 2,
        ["shepherd"] = 2,
        ["weaver"] = 2,
        ["carpenter"] = 2,
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
        ["ale"] = 150,
        ["wood"] = 250,
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
        ["grain"] = 3,
        ["bread"] = 3,
        ["vegetables"] = 2,
        ["ale"] = 1,
    };

    /// <summary>Below this stock a resource counts as scarce and its producers get extra job priority.</summary>
    public int LowStockThreshold { get; set; } = 40;

    /// <summary>Whether the settlement's governor AI may place new buildings on its own.</summary>
    public bool AutoBuild { get; set; } = true;

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
    public int CenterX { get; }
    public int CenterY { get; }

    public Inventory Stock { get; } = new();
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

    /// <summary>Bumped whenever roads change, so renderers know to rebuild.</summary>
    public int RoadVersion { get; set; }

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

    public int StorageCapacity => Buildings.Where(b => b.IsOperational).Sum(b => b.Def.Storage);
    public int FreeStorage => Math.Max(0, StorageCapacity - Stock.Total);

    public int HousingCapacity => Buildings.Where(b => b.IsOperational).Sum(b => b.Def.Housing);
    public int FreeHousing => Buildings.Sum(b => b.FreeHousing);

    public Building? FindBuilding(int id) => _buildingsById.GetValueOrDefault(id);
    public Citizen? FindCitizen(int id) => _citizensById.GetValueOrDefault(id);

    public void AddBuilding(Building b)
    {
        _buildings.Add(b);
        _buildingsById.Add(b.Id, b);
    }

    public bool RemoveBuilding(Building b)
    {
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

    /// <summary>Household goods used but not yet amounting to a whole unit, per resource.</summary>
    public SortedDictionary<string, double> GoodsUse { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Job slots the settlement wants filled. Workplaces that reached their stock limit keep their workers on
    /// standby (they keep the job, and any other job may take them at once); haulers follow the hauling quota.
    /// </summary>
    public int WantedWorkers(Building b) =>
        b.CurrentJobKind == JobKinds.Hauler ? Math.Min(b.JobSlots, HaulerQuota.GetValueOrDefault(b.Id)) : b.JobSlots;

    /// <summary>Workers here have nothing to do right now and can be moved to other work freely.</summary>
    public bool IsOnStandby(Building b) => IsAtProductionLimit(b) && !b.InputsLoaded;

    public override string ToString() => Name;
}
