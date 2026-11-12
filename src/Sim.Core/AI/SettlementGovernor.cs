using Sim.Core.Commands;
using Sim.Core.Data;
using Sim.Core.Settlements;
using Sim.Core.Simulation;
using Sim.Core.Systems;
using Sim.Core.World;

namespace Sim.Core.AI;

/// <summary>Finds good spots for new buildings around a settlement.</summary>
public static class SiteFinder
{
    /// <summary>
    /// Nearest valid spot to <paramref name="near"/> (default: the settlement centre), keeping a one-tile
    /// lane around buildings and not building over existing roads.
    /// </summary>
    public static (int X, int Y)? Find(GameWorld world, Settlement settlement, BuildingDef def, (int X, int Y)? near = null,
        int? maxRadius = null, Func<int, int, bool>? accept = null)
    {
        var (ox, oy) = near ?? (settlement.CenterX, settlement.CenterY);
        bool preferFertile = def.FertileBonus > 0;
        (int, int)? best = null;
        double bestScore = double.MinValue;
        int validFound = 0;

        int searchRadius = maxRadius ?? settlement.TerritoryRadius;
        for (int r = 0; r <= searchRadius; r++)
        {
            foreach (var (x, y) in Ring(ox, oy, r))
            {
                int left = x - def.Width / 2, top = y - def.Height / 2;
                if (Placement.Check(world, settlement, def, left, top) != null) continue;
                if (!HasClearBorder(world, left, top, def)) continue;
                if (CoversRoad(world, left, top, def)) continue;
                if (accept != null && !accept(left, top)) continue;

                if (!preferFertile) return (left, top);

                double score = FertileFraction(world, left, top, def) * 10 - r * 0.15;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = (left, top);
                }
                if (++validFound >= 60) return best;
            }
        }
        return best;
    }

    /// <summary>Keep a one-tile gap around buildings so the town has lanes between them.</summary>
    private static bool HasClearBorder(GameWorld world, int left, int top, BuildingDef def)
    {
        for (int y = top - 1; y <= top + def.Height; y++)
        for (int x = left - 1; x <= left + def.Width; x++)
        {
            if (world.Map.GetTile(x, y).BuildingId != 0) return false;
        }
        return true;
    }

    private static bool CoversRoad(GameWorld world, int left, int top, BuildingDef def)
    {
        for (int y = top; y < top + def.Height; y++)
        for (int x = left; x < left + def.Width; x++)
        {
            if (world.Map.GetTile(x, y).IsRoad) return true;
        }
        return false;
    }

    private static double FertileFraction(GameWorld world, int left, int top, BuildingDef def)
    {
        int fertile = 0;
        for (int y = top; y < top + def.Height; y++)
        for (int x = left; x < left + def.Width; x++)
        {
            if (world.Map.GetTile(x, y).Terrain == Terrain.Fertile) fertile++;
        }
        return fertile / (double)(def.Width * def.Height);
    }

    /// <summary>Points on the square ring at Chebyshev distance r, in a fixed order.</summary>
    private static IEnumerable<(int, int)> Ring(int cx, int cy, int r)
    {
        if (r == 0)
        {
            yield return (cx, cy);
            yield break;
        }
        for (int x = cx - r; x <= cx + r; x++) yield return (x, cy - r);
        for (int y = cy - r + 1; y <= cy + r; y++) yield return (cx + r, y);
        for (int x = cx + r - 1; x >= cx - r; x--) yield return (x, cy + r);
        for (int y = cy + r - 1; y > cy - r; y--) yield return (cx - r, y);
    }
}

/// <summary>Lays roads so new buildings are connected to the rest of the town.</summary>
public static class RoadPlanner
{
    public const int MaxSearch = 6000;

    /// <summary>Longest bridge the planner will build in one go.</summary>
    public const int MaxBridgeTiles = 8;

    /// <summary>
    /// Builds a road from the building's door to the nearest existing road or stockpile entrance.
    /// Returns false when there's no buildable route.
    /// </summary>
    public static bool Connect(GameWorld world, Settlement settlement, Building building)
    {
        var tiles = PlanRoute(world, settlement, building);
        if (tiles == null) return false;
        return tiles.Count == 0 || world.Execute(new PlaceRoadCommand(settlement.Id, tiles)).Ok;
    }

    /// <summary>
    /// The new road tiles needed to connect a building (possibly a planned one that isn't placed yet),
    /// an empty list if it's already connected or there's nothing to connect to, or null if it can't be done
    /// (no route, or a bridge longer than <see cref="MaxBridgeTiles"/>).
    /// </summary>
    public static List<(int X, int Y)>? PlanRoute(GameWorld world, Settlement settlement, Building building)
    {
        var start = building.AccessTiles()
            .Prepend(building.DoorTile)
            .FirstOrDefault(t => Pathfinder.StepCost(world.Map.GetTile(t.X, t.Y), Pathfinder.Mode.BuildRoad) != null,
                (X: int.MinValue, Y: int.MinValue));
        if (start.X == int.MinValue) return null;

        var stockpileEntrances = settlement.Buildings
            .Where(b => b.Def.IsStockpile && b.Id != building.Id)
            .SelectMany(b => b.AccessTiles())
            .ToHashSet();

        // The first building (usually the stockpile) has nothing to connect to yet.
        if (settlement.Roads.Count == 0 && stockpileEntrances.Count == 0) return new();

        bool IsGoal(int x, int y) =>
            (x, y) != start && !building.Covers(x, y) &&
            (world.Map.GetTile(x, y).IsRoad || stockpileEntrances.Contains((x, y)));

        if (IsGoal(start.X, start.Y) || world.Map.GetTile(start.X, start.Y).IsRoad) return new();

        var path = Pathfinder.FindPathToAny(world.Map, start, IsGoal, Pathfinder.Mode.BuildRoad, MaxSearch, building.Covers);
        if (path == null) return null;

        var tiles = path.Where(t => !world.Map.GetTile(t.X, t.Y).IsRoad).ToList();
        if (tiles.Count(t => world.Map.GetTile(t.X, t.Y).Terrain == Terrain.Water) > MaxBridgeTiles) return null;
        return tiles;
    }
}

/// <summary>
/// Simple rule-based planner that grows a settlement when its rules allow auto-building.
/// This is the seed of the NPC faction AI; it issues the same commands a player would.
/// </summary>
public static class SettlementGovernor
{
    public const int MaxActiveConstructionSites = 2;

    /// <summary>Site searches that come up empty are not retried for this many days.</summary>
    public const int FailedSiteRetryDays = 10;

    public const int MaxAttemptsPerDay = 3;

    /// <summary>Candidate sites per search that get a full road-route check.</summary>
    public const int MaxRouteChecksPerSearch = 10;

    public static void Daily(GameWorld world, Settlement settlement)
    {
        if (!settlement.Rules.AutoBuild) return;
        int maxSites = MaxActiveConstructionSites + settlement.Citizens.Count / 100;
        if (settlement.Buildings.Count(b => b.IsConstructionSite) >= maxSites) return;

        // Try wishes in priority order; the first affordable one with a usable site gets built.
        long today = world.Time.Day;
        int attempts = 0;
        CostGrid? field = null;
        bool fieldReady = false;
        foreach (var (defId, near) in Wishes(world, settlement))
        {
            var def = world.Data.Building(defId);
            if (!settlement.Stock.Has(def.Cost)) continue;

            string key = $"{defId}@{near}";
            if (settlement.FailedSiteSearches.TryGetValue(key, out long failedDay) && today - failedDay < FailedSiteRetryDays)
                continue;
            if (!fieldReady)
            {
                // Where a road (or bridge) from a stockpile could reach: the same rules the road planner uses.
                field = Logistics.StockpileField(world, settlement, Pathfinder.Mode.BuildRoad);
                fieldReady = true;
            }
            if (TryBuild(world, settlement, def, near, field)) return;

            settlement.FailedSiteSearches[key] = today;
            if (++attempts >= MaxAttemptsPerDay) return;
        }
    }

    private static bool TryBuild(GameWorld world, Settlement settlement, BuildingDef def, (int X, int Y)? near, CostGrid? field)
    {
        // A service building must actually reach the homes it was meant for, so only look nearby.
        int? maxRadius = def.Service is { } service && near != null ? service.Radius / 2 : null;
        // Only sites people can reach from a stockpile, possibly over a bridge the road planner will build.
        bool Reachable(int left, int top)
        {
            if (field == null) return true;
            for (int x = left; x < left + def.Width; x++)
                if (!double.IsPositiveInfinity(field[x, top - 1]) || !double.IsPositiveInfinity(field[x, top + def.Height])) return true;
            for (int y = top; y < top + def.Height; y++)
                if (!double.IsPositiveInfinity(field[left - 1, y]) || !double.IsPositiveInfinity(field[left + def.Width, y])) return true;
            return false;
        }

        // Plan the connecting road (and any bridge) before committing, so nothing gets built and torn
        // down. Route planning is expensive, so only a limited number of candidate sites get one.
        int routeChecks = 0;
        bool Connectable(int left, int top)
        {
            if (!Reachable(left, top) || routeChecks++ >= MaxRouteChecksPerSearch) return false;
            var route = RoadPlanner.PlanRoute(world, settlement, new Building(0, def, left, top));
            return route != null &&
                   settlement.Stock.Get("wood") >= Roads.BridgeCost(world, route) + def.Cost.GetValueOrDefault("wood");
        }

        var site = SiteFinder.Find(world, settlement, def, near, maxRadius, Connectable);
        if (site is not var (x, y)) return false;

        var result = world.Execute(new PlaceBuildingCommand(settlement.Id, def.Id, x, y));
        if (result.CreatedId is not int id || settlement.FindBuilding(id) is not { } placed) return false;

        RoadPlanner.Connect(world, settlement, placed);
        if (placed.NoRoute && !def.IsStockpile)
        {
            // Cut off (e.g. across a river with no bridge): take it back.
            world.Execute(new DemolishBuildingCommand(settlement.Id, placed.Id));
            return false;
        }
        return true;
    }

    /// <summary>What the settlement would like to build next, most important first.</summary>
    public static List<(string DefId, (int X, int Y)? Near)> Wishes(GameWorld world, Settlement s)
    {
        int population = s.Citizens.Count;
        int plannedHousing = s.Buildings.Sum(b => b.Def.Housing) - population;
        double foodDays = Needs.FoodDays(world, s);
        int farms = s.CountIncludingPlanned("farm");
        int bakeries = s.CountIncludingPlanned("bakery");

        // Don't build more workplaces of a kind while existing ones are short of workers.
        bool Understaffed(string defId) => s.Buildings.Any(b =>
            b.Def.Id == defId && (b.IsConstructionSite || b.WorkerIds.Count < s.WantedWorkers(b)));

        // Where new service buildings could go, or nothing if the existing ones can cope: some home must
        // be out of reach of every building of that kind, or they must all be full. Unstaffed or
        // unfinished ones get a chance first. Returns a few well-spread candidate homes.
        List<(int, int)> ServiceGaps(string need)
        {
            var none = new List<(int, int)>();
            var providers = s.Buildings.Where(b => b.Def.Service?.Need == need).ToList();
            if (providers.Any(b => b.IsConstructionSite)) return none;
            if (providers.Any(b => b.Def.Jobs > 0 && b.WorkerIds.Count == 0)) return none;

            var uncoveredHomes = s.Citizens
                .Where(c => c.HomeBuildingId != null && !c.CoveredNeeds.Contains(need))
                .Select(c => s.FindBuilding(c.HomeBuildingId!.Value))
                .Where(h => h != null && h.IsOperational)
                .Select(h => h!)
                .Distinct()
                .ToList();
            if (uncoveredHomes.Count == 0) return none;

            var outOfReach = uncoveredHomes
                .Where(h => providers.All(p => p.DistanceTo(h) > p.Def.Service!.Radius)).ToList();
            bool allFull = providers.Count > 0 && providers.All(p => p.ServedCount >= p.Def.Service!.Capacity);
            var candidates = outOfReach.Count > 0 ? outOfReach : allFull ? uncoveredHomes : new List<Building>();

            var picked = new List<(int, int)>();
            foreach (var h in candidates)
            {
                if (picked.Any(p => Math.Abs(p.Item1 - h.CenterX) + Math.Abs(p.Item2 - h.CenterY) < 8)) continue;
                picked.Add((h.CenterX, h.CenterY));
                if (picked.Count == 4) break;
            }
            return picked;
        }

        var wishes = new List<(string, (int, int)?)>();
        void Wish(string id, (int, int)? near = null) => wishes.Add((id, near));

        int homeless = s.Citizens.Count(c => c.HomeBuildingId == null);
        if (plannedHousing < 3 + homeless) Wish("hovel");
        foreach (var spot in ServiceGaps("water")) Wish("well", spot);

        // Each farm (two farmers) reliably covers about 6 nutrition a day.
        double dailyFoodNeed = s.Citizens.Sum(Needs.DailyFoodNeed);
        bool grainSurplus = s.Stock.Get("grain") >= s.LimitOf("grain") * 0.8;
        if ((foodDays < 3 && !grainSurplus) || farms * 6.0 < dailyFoodNeed)
        {
            if (!Understaffed("farm")) Wish("farm");
        }
        if ((bakeries == 0 && population >= 18) || (grainSurplus && bakeries < farms * 0.6 && !Understaffed("bakery")))
            Wish("bakery");
        if (s.Stock.Get("wood") < 40 && s.CountIncludingPlanned("woodcutter") < 1 + population / 15)
            Wish("woodcutter");
        int quarries = s.CountIncludingPlanned("quarry");
        if (quarries == 0 || (s.Stock.Get("stone") < 20 && quarries < 1 + population / 30))
            Wish("quarry");
        if (s.FreeStorage < 80) Wish("stockpile");

        // Services and variety as the town grows.
        if (farms >= 2 && s.CountIncludingPlanned("garden") < 1 + population / 40 && !Understaffed("garden"))
            Wish("garden");
        if (population >= 30)
            foreach (var spot in ServiceGaps("faith")) Wish("chapel", spot);
        if (population >= 40)
        {
            int taverns = s.CountIncludingPlanned("tavern");
            int breweries = s.CountIncludingPlanned("brewery");
            if ((breweries == 0 || (s.Stock.Get("ale") < 20 && breweries <= taverns)) && !Understaffed("brewery"))
                Wish("brewery");
            if (s.Stock.Get("ale") >= 10)
                foreach (var spot in ServiceGaps("drink")) Wish("tavern", spot);
            foreach (var spot in ServiceGaps("market")) Wish("market", spot);
        }

        // When wood is what's holding everything back, a cheap woodcutter comes last as a fallback.
        if (wishes.Count > 0 && s.CountIncludingPlanned("woodcutter") < 2) Wish("woodcutter");
        return wishes;
    }
}
