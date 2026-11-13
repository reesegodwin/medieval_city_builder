using Sim.Core.Commands;
using Sim.Core.Data;
using Sim.Core.Settlements;
using Sim.Core.Simulation;
using Sim.Core.Systems;
using Sim.Core.Time;
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

        int searchRadius = maxRadius ??
            settlement.TerritoryExtent + Math.Max(Math.Abs(ox - settlement.CenterX), Math.Abs(oy - settlement.CenterY));
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

    /// <summary>
    /// Largest cut-off area <see cref="SealedLandPocket"/> maps. Roads may leave the territory, so the land
    /// across a lake can be big; mapping it once a day is cheap next to the route searches it saves.
    /// </summary>
    public const int MaxPocketTiles = 60000;

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
        var start = RouteStart(world, settlement, building);
        if (start.X == int.MinValue) return null;

        var stockpileEntrances = settlement.Buildings
            .Where(b => b.Def.IsStockpile && b.Id != building.Id)
            .SelectMany(b => b.AccessTiles())
            .ToHashSet();

        // The first building (usually the stockpile) has nothing to connect to yet.
        if (settlement.Roads.Count == 0 && stockpileEntrances.Count == 0) return new();

        bool IsGoal(int x, int y) =>
            (x, y) != start && !building.Covers(x, y) &&
            (world.Map.GetTile(x, y) is { IsRoad: true } or { RoadPlanned: true } || stockpileEntrances.Contains((x, y)));

        if (IsGoal(start.X, start.Y) || world.Map.GetTile(start.X, start.Y) is { IsRoad: true } or { RoadPlanned: true }) return new();

        // Roads may only be laid inside the territory (see Roads.Check), so plan within it.
        bool Blocked(int x, int y) => building.Covers(x, y) || !CanLayRoad(world, settlement, x, y);
        var path = Pathfinder.FindPathToAny(world.Map, start, IsGoal, Pathfinder.Mode.BuildRoad, MaxSearch, Blocked);
        if (path == null) return null;

        var tiles = path.Where(t => world.Map.GetTile(t.X, t.Y) is { IsRoad: false, RoadPlanned: false }).ToList();
        if (tiles.Count(t => world.Map.GetTile(t.X, t.Y).Terrain == Terrain.Water) > MaxBridgeTiles) return null;
        return tiles;
    }

    /// <summary>
    /// After <see cref="PlanRoute"/> failed for a building: the land tiles road-connected to its route start
    /// (without crossing water, including its own footprint), if that land holds no road or stockpile entrance. Any site whose access lies only
    /// on that land needs the same too-long bridge. Returns null if it isn't such a pocket or is too big to map.
    /// </summary>
    public static List<(int X, int Y)>? SealedLandPocket(GameWorld world, Settlement settlement, Building building)
    {
        static bool IsLand(in Tile t) =>
            t.Terrain != Terrain.Water && Pathfinder.StepCost(t, Pathfinder.Mode.BuildRoad) != null;

        var start = RouteStart(world, settlement, building);
        if (start.X == int.MinValue || world.Map.GetTile(start.X, start.Y).Terrain == Terrain.Water) return null;

        var stockpileEntrances = settlement.Buildings
            .Where(b => b.Def.IsStockpile)
            .SelectMany(b => b.AccessTiles())
            .ToHashSet();

        var pocket = new List<(int X, int Y)> { start };
        var seen = new HashSet<(int, int)> { start };
        for (int i = 0; i < pocket.Count; i++)
        {
            var (x, y) = pocket[i];
            if (world.Map.GetTile(x, y) is { IsRoad: true } or { RoadPlanned: true } || stockpileEntrances.Contains((x, y))) return null;
            if (pocket.Count > MaxPocketTiles) return null;
            foreach (var (dx, dy) in Neighbours)
            {
                var n = (x + dx, y + dy);
                if (!seen.Add(n)) continue;
                // The candidate's own footprint is land too (it isn't built), so neighbouring candidates are covered.
                // Roads count as land: reaching one means this isn't a sealed pocket.
                if (IsLand(world.Map.GetTile(n.Item1, n.Item2)) && CanLayRoad(world, settlement, n.Item1, n.Item2)) pocket.Add(n);
            }
        }
        return pocket;
    }

    /// <summary>The tile a building's connecting road starts from, or (int.MinValue, int.MinValue) if none.</summary>
    private static (int X, int Y) RouteStart(GameWorld world, Settlement settlement, Building building) =>
        building.AccessTiles()
            .Prepend(building.DoorTile)
            .FirstOrDefault(t => Pathfinder.StepCost(world.Map.GetTile(t.X, t.Y), Pathfinder.Mode.BuildRoad) != null &&
                                 CanLayRoad(world, settlement, t.X, t.Y),
                (X: int.MinValue, Y: int.MinValue));

    /// <summary>An existing road, or a tile where this settlement may lay one.</summary>
    private static bool CanLayRoad(GameWorld world, Settlement settlement, int x, int y) =>
        world.Map.GetTile(x, y) is { IsRoad: true } or { RoadPlanned: true } || Roads.Check(world, settlement, x, y) == null;

    private static readonly (int Dx, int Dy)[] Neighbours = { (0, -1), (1, 0), (0, 1), (-1, 0) };
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

    /// <summary>Governed towns build a Trading Post once they have this many people.</summary>
    public const int TradingPostPopulation = 80;

    /// <summary>The governor upgrades the town centre once population would claim this share of its territory cap.</summary>
    public const double UpgradeAtCapFraction = 0.85;

    /// <summary>Candidate sites per search that get a full road-route check.</summary>
    public const int MaxRouteChecksPerSearch = 10;

    /// <summary>Skip candidate sites within this many tiles of one whose road couldn't be planned.</summary>
    public const int UnconnectableSpread = 6;

    /// <summary>Labour a worker is assumed to give per day when sizing food production (a cautious <see cref="Work.DailyRate"/>).</summary>
    public const double ExpectedWorkerRate = 1.2;

    /// <summary>Towns this size and up store food in granaries rather than more stockpiles.</summary>
    public const int GranaryPopulation = 60;

    /// <summary>Towns this size and up keep sheep for mutton, one pasture per <see cref="PeoplePerPasture"/> people.</summary>
    public const int PasturePopulation = 40;
    public const int PeoplePerPasture = 70;
    public const int PeoplePerComfortBuilding = 150;
    public const double BreweriesPerTavern = 1.25;

    /// <summary>Towns this size and up gather firewood (the first winter comes at day 271).</summary>
    public const int FirewoodPopulation = 15;

    /// <summary>Build more producers of wood, stone or food when the books say they'd run out within this many days.</summary>
    public const double ForecastDays = 15;

    /// <summary>Planned food production must exceed the town's daily need by this factor.</summary>
    public const double FoodSupplyMargin = 1.2;

    public static void Daily(GameWorld world, Settlement settlement)
    {
        if (!settlement.Rules.AutoBuild) return;
        TradeGovernor.Daily(world, settlement);
        UpgradeRoads(world, settlement);
        BuildWalls(world, settlement);
        War.MilitaryGovernor.Daily(world, settlement);
        int maxSites = MaxActiveConstructionSites + settlement.Citizens.Count / 100;
        if (settlement.Buildings.Count(b => b.IsConstructionSite) >= maxSites) return;

        if (TryUpgradeTownCentre(world, settlement)) return;

        // Try wishes in priority order; the first affordable one with a usable site gets built.
        long today = world.Time.Day;
        int attempts = 0;
        CostGrid? field = null, landField = null;
        bool fieldReady = false;
        // Land found to be cut off from the road network (beyond bridge range): skipped for the rest of today.
        var deadLand = new HashSet<(int X, int Y)>();
        // A kind of building with no site today: its other spots are skipped too, so the day's few searches go to
        // other wishes (a crowded old town needing taverns shouldn't stop a guardhouse being built at all).
        var noSiteToday = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (defId, near) in Wishes(world, settlement))
        {
            var def = world.Data.Building(defId);
            if (!settlement.Stock.Has(def.Cost) || noSiteToday.Contains(defId)) continue;

            string key = $"{defId}@{near}";
            if (settlement.FailedSiteSearches.TryGetValue(key, out long failedDay) && today - failedDay < FailedSiteRetryDays)
                continue;
            if (!fieldReady)
            {
                // Where a road (or bridge) from a stockpile could reach: the same rules the road planner uses.
                field = Logistics.StockpileField(world, settlement, Pathfinder.Mode.BuildRoad);
                // Land a stockpile reaches without a new bridge. Sites there are tried first: the cheapest
                // road elsewhere may need a bridge too long to build, and those sites would use up the searches.
                landField = Logistics.StockpileField(world, settlement, Pathfinder.Mode.Walk);
                fieldReady = true;
            }
            if (landField != null && TryBuild(world, settlement, def, near, landField, deadLand)) return;
            if (TryBuild(world, settlement, def, near, field, deadLand)) return;

            settlement.FailedSiteSearches[key] = today;
            noSiteToday.Add(defId);
            if (++attempts >= MaxAttemptsPerDay) return;
        }
    }

    /// <summary>Towns ring themselves with a palisade at this size, stone at the next, a large wall at the last.</summary>
    public static readonly int[] WallPopulation = { 150, 400, 700 };

    /// <summary>A tower every this many ring tiles, once the ring is built.</summary>
    public const int TowerSpacing = 16;

    /// <summary>Wall tiles planned per batch.</summary>
    public const int WallBatch = 30;

    /// <summary>A gate every this many ring tiles, so later roads can get out.</summary>
    public const int RingGateSpacing = 24;

    /// <summary>Margin around the town's buildings that the ring encloses (within the territory).</summary>
    public const int RingMargin = 4;

    /// <summary>
    /// Rings the town with walls, in batches while materials are plentiful (three quarters of the stock limit,
    /// keeping half): a palisade first, upgraded to stone and then a large stone wall as the town grows.
    /// </summary>
    private static void BuildWalls(GameWorld world, Settlement s)
    {
        int population = s.Citizens.Count;
        int tier = -1;
        for (int i = 0; i < Math.Min(WallPopulation.Length, world.Data.WallTiers.Count); i++)
            if (population >= WallPopulation[i]) tier = i;
        if (tier < 0) return;
        if (s.WallRing.Count == 0) PlanRing(world, s);
        if (s.WallRing.Count == 0 || s.WallPlans.Count > 0 || s.RoadPlans.Count > 0) return;

        var def = world.Data.WallTiers[tier];
        // How much of each material may be spent: three quarters of the limit in store, keeping half.
        var budget = new Dictionary<string, int>();
        foreach (string r in def.Cost.Keys.Concat(def.GateCost.Keys).Concat(def.Tower.Cost.Keys).Concat(def.Gatehouse.Cost.Keys).Distinct())
        {
            int limit = s.LimitOf(r);
            if (limit == int.MaxValue || s.Stock.Get(r) < limit * 0.75) return;
            budget[r] = s.Stock.Get(r) - limit / 2;
        }
        var spend = new Dictionary<string, int>();
        bool Afford(Dictionary<string, int> cost)
        {
            if (cost.Any(kv => spend.GetValueOrDefault(kv.Key) + kv.Value > budget.GetValueOrDefault(kv.Key))) return false;
            foreach (var (r, n) in cost) spend[r] = spend.GetValueOrDefault(r) + n;
            return true;
        }

        // First the ring itself, at this tier: plain walls and gates first, then the dearer towers and gatehouses.
        var batch = new List<(int X, int Y)>();
        var gates = new List<(int X, int Y)>();
        bool ringDone = true;
        bool Strong((int X, int Y, bool Gate) r) => s.Walls.TryGetValue((r.X, r.Y), out var w) && w.Piece is WallPiece.Tower or WallPiece.Gatehouse;
        foreach (var (x, y, gate) in s.WallRing.Where(r => !Strong(r)).Concat(s.WallRing.Where(Strong)))
        {
            var tile = world.Map.GetTile(x, y);
            WallPiece? want = gate ? WallPiece.Gate : null;
            if (!s.Walls.ContainsKey((x, y)) && Walls.Check(world, s, x, y) != null) continue;
            if (!Walls.NeedsWork(s, tile, x, y, (byte)tier, want)) continue;
            ringDone = false;
            if (batch.Count >= WallBatch) break;
            s.Walls.TryGetValue((x, y), out var existing);
            var piece = Walls.Resolve(tile, existing?.Piece, want);
            if (!Afford(Walls.PieceCost(world, Math.Max(tier, existing?.Tier ?? 0), piece))) break;
            batch.Add((x, y));
            if (gate) gates.Add((x, y));
        }
        if (batch.Count > 0)
        {
            world.Execute(new PlaceWallCommand(s.Id, batch, (byte)tier, gates));
            return;
        }
        if (!ringDone) return;

        // Then strong points on the finished ring: a tower every few tiles, a gatehouse on each gate a road runs through.
        for (int i = 0; i < s.WallRing.Count; i++)
        {
            var (x, y, _) = s.WallRing[i];
            if (!s.Walls.TryGetValue((x, y), out var wall)) continue;
            var tile = world.Map.GetTile(x, y);
            WallPiece? want = wall.Piece == WallPiece.Wall && i % TowerSpacing == TowerSpacing / 4 ? WallPiece.Tower
                : wall.Piece == WallPiece.Gate && tile.IsRoad ? WallPiece.Gatehouse : null;
            if (want is not { } piece || !Afford(Walls.PieceCost(world, wall.Tier, piece))) continue;
            world.Execute(piece == WallPiece.Tower ? new PlaceTowerCommand(s.Id, x, y) : new PlaceGatehouseCommand(s.Id, x, y));
            return; // one strong point at a time
        }
    }

    /// <summary>
    /// Lays out the ring once: the edge of the town's land around its buildings (the territory, cut to the buildings'
    /// extent plus a margin). Being the edge of a region it closes on itself; water and buildings fill any gaps.
    /// Ordered around the town centre (with whole-number maths, so every machine agrees), with gates spaced out.
    /// </summary>
    private static void PlanRing(GameWorld world, Settlement s)
    {
        if (s.Buildings.Count == 0) return;
        int x0 = s.Buildings.Min(b => b.X) - RingMargin, x1 = s.Buildings.Max(b => b.X + b.Def.Width - 1) + RingMargin;
        int y0 = s.Buildings.Min(b => b.Y) - RingMargin, y1 = s.Buildings.Max(b => b.Y + b.Def.Height - 1) + RingMargin;
        bool InRegion(int x, int y) => x >= x0 && x <= x1 && y >= y0 && y <= y1 && s.OwnsTile(world.Map, x, y);

        var ring = new List<(int X, int Y)>();
        foreach (var (x, y) in s.Territory)
        {
            if (!InRegion(x, y) || world.Map.GetTile(x, y).Terrain == Terrain.Water) continue;
            if (!InRegion(x + 1, y) || !InRegion(x - 1, y) || !InRegion(x, y + 1) || !InRegion(x, y - 1)) ring.Add((x, y));
        }
        int cx = s.CenterX, cy = s.CenterY;
        int Half(int dx, int dy) => dy < 0 || (dy == 0 && dx >= 0) ? 0 : 1;
        ring.Sort((a, b) =>
        {
            int ax = a.X - cx, ay = a.Y - cy, bx = b.X - cx, by = b.Y - cy;
            int ha = Half(ax, ay), hb = Half(bx, by);
            if (ha != hb) return ha.CompareTo(hb);
            long cross = (long)ax * by - (long)ay * bx;
            if (cross != 0) return cross > 0 ? -1 : 1;
            return (Math.Abs(ax) + Math.Abs(ay)).CompareTo(Math.Abs(bx) + Math.Abs(by));
        });
        for (int i = 0; i < ring.Count; i++)
            s.WallRing.Add((ring[i].X, ring[i].Y, i % RingGateSpacing == RingGateSpacing / 2));
    }

    /// <summary>Roads within this distance of a stockpile carry the most hauling, so they're resurfaced first.</summary>
    public const int RoadUpgradeRadius = 12;

    /// <summary>Tiles resurfaced per batch.</summary>
    public const int RoadUpgradeBatch = 25;

    /// <summary>Towns this size and up pave with stone rather than gravel.</summary>
    public const int StoneRoadPopulation = 300;

    /// <summary>
    /// With stone to spare (three quarters of its stock limit), resurface the roads nearest the stockpiles: gravel,
    /// or stone in big towns. Stays above half the limit so building never runs short.
    /// </summary>
    private static void UpgradeRoads(GameWorld world, Settlement s)
    {
        if (s.RoadPlans.Count > 0 || world.Data.RoadTiers.Count < 2) return;
        int limit = s.LimitOf("stone");
        if (limit == int.MaxValue || s.Stock.Get("stone") < limit * 0.75) return;

        byte target = (byte)Math.Min(world.Data.RoadTiers.Count - 1, s.Citizens.Count >= StoneRoadPopulation ? 2 : 1);
        var stockpiles = s.Buildings.Where(b => b.Def.IsStockpile && b.IsOperational).ToList();
        if (stockpiles.Count == 0) return;
        int Distance((int X, int Y) t) => stockpiles.Min(b => Math.Abs(b.CenterX - t.X) + Math.Abs(b.CenterY - t.Y));

        var tiles = s.Roads
            .Where(t => Roads.NeedsWork(world, s, t.X, t.Y, target) && Distance(t) <= RoadUpgradeRadius)
            .OrderBy(Distance).ThenBy(t => t.X).ThenBy(t => t.Y)
            .Take(RoadUpgradeBatch).ToList();
        if (tiles.Count == 0) return;
        var cost = Roads.PlanCost(world, s, tiles, target);
        if (s.Stock.Get("stone") - cost.GetValueOrDefault("stone") < limit / 2) return;
        world.Execute(new PlaceRoadCommand(s.Id, tiles, target));
    }

    /// <summary>Upgrade the town centre once its territory cap is close to holding the town back and it's affordable.</summary>
    private static bool TryUpgradeTownCentre(GameWorld world, Settlement settlement)
    {
        if (UpgradeTownCentreCommand.Check(world, settlement, out var next) != null || next == null) return false;
        int wanted = TerritoryRules.PopulationAllowance(settlement.Citizens.Count);
        if (wanted < settlement.TerritoryCap * UpgradeAtCapFraction) return false;
        if (!settlement.Stock.Has(next.Cost)) return false;
        return world.Execute(new UpgradeTownCentreCommand(settlement.Id)).Ok;
    }

    private static bool TryBuild(GameWorld world, Settlement settlement, BuildingDef def, (int X, int Y)? near, CostGrid? field,
        HashSet<(int X, int Y)> deadLand)
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

        bool OnlyOnDeadLand(Building candidate)
        {
            if (deadLand.Count == 0) return false;
            bool any = false;
            foreach (var t in candidate.AccessTiles().Prepend(candidate.DoorTile))
            {
                var tile = world.Map.GetTile(t.X, t.Y);
                // Dead land is land only: water next to the site leads nowhere without the too-long bridge.
                if (tile.Terrain == Terrain.Water || Pathfinder.StepCost(tile, Pathfinder.Mode.BuildRoad) == null) continue;
                if (!deadLand.Contains(t)) return false;
                any = true;
            }
            return any;
        }

        // Plan the connecting road (and any bridge) before committing, so nothing gets built and torn
        // down. Route planning is expensive, so only a limited number of candidate sites get one.
        int routeChecks = 0;
        // Sites next to one that couldn't be connected almost always fail the same way, so the limited
        // route checks are spread out instead of being spent on one cut-off corner.
        var unconnectable = new List<(int X, int Y)>();
        bool Connectable(int left, int top)
        {
            if (!Reachable(left, top)) return false;
            var candidate = new Building(0, def, left, top);
            if (OnlyOnDeadLand(candidate)) return false;
            if (unconnectable.Any(f => Math.Max(Math.Abs(f.X - left), Math.Abs(f.Y - top)) <= UnconnectableSpread)) return false;
            if (routeChecks++ >= MaxRouteChecksPerSearch) return false;
            var route = RoadPlanner.PlanRoute(world, settlement, candidate);
            if (route == null) unconnectable.Add((left, top));
            if (route == null && RoadPlanner.SealedLandPocket(world, settlement, candidate) is { } pocket)
                deadLand.UnionWith(pocket);
            return route != null &&
                   settlement.Stock.Get("wood") >= Roads.BridgeCost(world, route) + def.Cost.GetValueOrDefault("wood");
        }

        var site = SiteFinder.Find(world, settlement, def, near, maxRadius, Connectable);
        if (site == null && def.Service is { } reach && near != null)
        {
            // Crowded around that home: anywhere within the service's reach of it still serves it.
            routeChecks = 0;
            site = SiteFinder.Find(world, settlement, def, near, reach.Radius - 1, Connectable);
        }
        if (site is not var (x, y)) return false;

        var result = world.Execute(new PlaceBuildingCommand(settlement.Id, def.Id, x, y));
        if (result.CreatedId is not int id || settlement.FindBuilding(id) is not { } placed) return false;

        // The road (and any bridge) is planned and roadworkers will build it, so only a failed plan means cut off.
        if (!RoadPlanner.Connect(world, settlement, placed) && !def.IsStockpile)
        {
            // Cut off (e.g. across a river with no bridge): take it back.
            world.Execute(new DemolishBuildingCommand(settlement.Id, placed.Id));
            return false;
        }
        return true;
    }

    /// <summary>
    /// Nutrition per day the settlement's food buildings (including planned ones) should produce when
    /// staffed: farm grain, baked into bread as far as bakeries can take it, minus grain for breweries,
    /// plus garden vegetables. Recipes and nutrition come from the game data.
    /// </summary>
    public static double EstimatedFoodSupply(GameWorld world, Settlement s)
    {
        double Rate(string defId, out RecipeDef? recipe)
        {
            var def = world.Data.Buildings.GetValueOrDefault(defId);
            recipe = def?.Recipe;
            if (def == null || recipe == null || recipe.WorkDays <= 0) return 0;
            return s.CountIncludingPlanned(defId) * def.Jobs * ExpectedWorkerRate / recipe.WorkDays *
                   world.Data.Seasons.AverageOutputOf(defId); // farms are planned for the whole year
        }
        double Nutrition(string resource) => world.Data.Resources.GetValueOrDefault(resource)?.Nutrition ?? 0;

        double farmCycles = Rate("farm", out var farm);
        double grain = farmCycles * (farm?.Outputs.GetValueOrDefault("grain") ?? 0);

        double breweryCycles = Rate("brewery", out var brewery);
        grain = Math.Max(0, grain - breweryCycles * (brewery?.Inputs.GetValueOrDefault("grain") ?? 0));

        double supply = 0;
        double bakeryCycles = Rate("bakery", out var bakery);
        int grainPerBake = bakery?.Inputs.GetValueOrDefault("grain") ?? 0;
        if (grainPerBake > 0)
        {
            double baked = Math.Min(grain, bakeryCycles * grainPerBake);
            supply += baked / grainPerBake * bakery!.Outputs.GetValueOrDefault("bread") * Nutrition("bread");
            grain -= baked;
        }
        supply += grain * Nutrition("grain");

        double gardenCycles = Rate("garden", out var garden);
        supply += gardenCycles * (garden?.Outputs.GetValueOrDefault("vegetables") ?? 0) * Nutrition("vegetables");
        double fishingCycles = Rate("fishing_hut", out var fishing);
        supply += fishingCycles * (fishing?.Outputs.GetValueOrDefault("fish") ?? 0) * Nutrition("fish");
        double pastureCycles = Rate("pasture", out var pasture);
        supply += pastureCycles * (pasture?.Outputs.GetValueOrDefault("mutton") ?? 0) * Nutrition("mutton");
        return supply;
    }

    /// <summary>What the settlement would like to build next, most important first.</summary>
    public static List<(string DefId, (int X, int Y)? Near)> Wishes(GameWorld world, Settlement s)
    {
        int population = s.Citizens.Count;
        int plannedHousing = s.Buildings.Sum(b => b.Def.Housing) - population;
        double foodDays = Needs.FoodDays(world, s);
        // Goods the books say are falling fast get producers before they run out, not after.
        bool RunningOut(string resource) => Economy.Forecast.DaysLeft(world, s, resource) < ForecastDays;
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
            // Unpaid upkeep halves what services can serve; more of them would only add upkeep.
            if (s.UpkeepUnpaid && world.Data.BuildingList.Any(d => d.Service?.Need == need && d.Upkeep > 0)) return none;
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
            // Otherwise homes whose nearby providers are all full: busy taverns in the old town aren't
            // helped by quiet ones on the edge.
            var candidates = outOfReach.Count > 0
                ? outOfReach
                : uncoveredHomes.Where(h => providers
                    .Where(p => p.DistanceTo(h) <= p.Def.Service!.Radius)
                    .All(p => p.ServedCount >= p.Def.Service!.Capacity)).ToList();

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

        // Size farming to the town's need (counting bread and gardens), with an emergency top-up.
        double dailyFoodNeed = s.Citizens.Sum(Needs.DailyFoodNeed);
        bool grainSurplus = s.Stock.Get("grain") >= s.LimitOf("grain") * 0.8;
        var season = world.Time.Season;
        // Food falls every winter; more farms then would only lie idle in summer.
        bool foodFalling = RunningOut(Economy.Forecast.Food) && season != Season.Winter;
        if (((foodDays < 3 || foodFalling) && !grainSurplus) ||
            EstimatedFoodSupply(world, s) < dailyFoodNeed * FoodSupplyMargin)
        {
            if (!Understaffed("farm")) Wish("farm");
        }
        if ((bakeries == 0 && population >= 18) || (grainSurplus && bakeries < farms * 0.6 && !Understaffed("bakery")))
            Wish("bakery");
        if ((s.Stock.Get("wood") < 40 || RunningOut("wood")) && s.CountIncludingPlanned("woodcutter") < 1 + population / 15)
            Wish("woodcutter");
        int quarries = s.CountIncludingPlanned("quarry");
        if (quarries == 0 || ((s.Stock.Get("stone") < 20 || RunningOut("stone")) && quarries < 1 + population / 30))
            Wish("quarry");
        // Firewood: gathered all year until stores are full, so homes stay warm through winter.
        string firewood = SeasonSystem.Firewood;
        bool firewoodLow = s.Stock.Get(firewood) < (season is Season.Summer or Season.Autumn ? s.LimitOf(firewood) / 2 : 20) ||
                           RunningOut(firewood);
        if (population >= FirewoodPopulation && firewoodLow && !Understaffed("firewood_hut") &&
            s.CountIncludingPlanned("firewood_hut") < 1 + population / 50)
            Wish("firewood_hut");

        // Storage, and before winter room for a reserve of food: granaries once the town can afford them. Granaries
        // also keep food from rotting, so one is wanted whenever more food is stored than they hold.
        double reserveRoom = season is Season.Summer or Season.Autumn
            ? SeasonSystem.WinterReserve(world, s) - SeasonSystem.FoodStock(world, s)
            : 0;
        bool granaryWanted = population >= GranaryPopulation && s.CountIncludingPlanned("granary") < 1 + population / 40;
        bool foodRotting = SeasonSystem.FoodStock(world, s) > s.FoodStoreCapacity + 100;
        // Build storage ahead of need: a tenth of it free, not just the last few places.
        if (s.FreeStorage < Math.Max(80, s.StorageCapacity / 10) || s.FreeStorage < reserveRoom || (granaryWanted && foodRotting))
            Wish(granaryWanted ? "granary" : "stockpile");
        if (population >= TradingPostPopulation && !s.Buildings.Any(b => b.Def.Trade)) Wish("trading_post");
        // Barracks for the standing garrison.
        int squads = War.MilitaryGovernor.WantedSquads(world, s);
        if (squads > 0 && s.CountIncludingPlanned("barracks") * world.Data.War.SquadsPerBarracks < squads) Wish("barracks");

        // Production chains (mine -> smithy, pasture -> weaver, carpenter...), from each building's governor hint.
        foreach (var def in world.Data.BuildingList)
        {
            if (def.Governor is not { } hint || def.Recipe is not { } recipe) continue;
            if (population < hint.MinPopulation || Understaffed(def.Id)) continue;
            int count = s.CountIncludingPlanned(def.Id);
            if (count >= 1 + population / hint.PopulationPerBuilding) continue;
            if (count > 0 && recipe.Outputs.Keys.Any(r => s.Stock.Get(r) >= hint.LowStock)) continue;
            // A converter is only worth building once its inputs are in stock or something makes them.
            bool inputsAvailable = recipe.InputOptions.Any(option => option.All(kv =>
                s.Stock.Get(kv.Key) >= kv.Value ||
                s.Buildings.Any(b => b.Def.Recipe?.Outputs.ContainsKey(kv.Key) == true)));
            if (inputsAvailable) Wish(def.Id);
        }

        // Services and variety as the town grows.
        if (farms >= 2 && s.CountIncludingPlanned("garden") < 1 + population / 40 && !Understaffed("garden"))
            Wish("garden");
        // Pastures for mutton (and wool) once the town is past its first harvests.
        if (population >= PasturePopulation && s.CountIncludingPlanned("pasture") < 1 + population / PeoplePerPasture &&
            s.Stock.Get("mutton") < s.LimitOf("mutton") / 2 && !Understaffed("pasture"))
            Wish("pasture");
        if (population >= 30)
            foreach (var spot in ServiceGaps("faith")) Wish("chapel", spot);
        if (population >= 40)
        {
            int taverns = s.CountIncludingPlanned("tavern");
            int breweries = s.CountIncludingPlanned("brewery");
            // A brewery makes about 8 ale a day and a busy tavern pours 10 (15 on winter nights), so keep
            // about one and a half per tavern while the cellars aren't full.
            int wantBreweries = (int)Math.Ceiling(taverns * BreweriesPerTavern);
            bool aleLow = s.Stock.Get("ale") < 20 || (breweries < wantBreweries && s.Stock.Get("ale") < s.LimitOf("ale") / 2);
            if ((breweries == 0 || aleLow) && breweries <= wantBreweries && !Understaffed("brewery"))
                Wish("brewery");
            if (s.Stock.Get("ale") >= 10)
                foreach (var spot in ServiceGaps("drink")) Wish("tavern", spot);
            foreach (var spot in ServiceGaps("market")) Wish("market", spot);
        }
        // Health, cleanliness and the watch as the town grows: a few of each, so they don't drain the workforce.
        // The first of each may go anywhere; later ones go where people aren't reached.
        void Comfort(string id, string need)
        {
            int count = s.CountIncludingPlanned(id);
            if (count >= 1 + population / PeoplePerComfortBuilding) return;
            var spots = ServiceGaps(need);
            if (spots.Count > 0) Wish(id, count == 0 ? null : spots[0]);
        }
        if (population >= 80) Comfort("herbalist", "healing");
        if (population >= 150)
        {
            if (s.Stock.Get("firewood") >= 20) Comfort("bathhouse", "hygiene");
            Comfort("guardhouse", "law");
        }

        // When wood is what's holding everything back, a cheap woodcutter comes last as a fallback.
        if (wishes.Count > 0 && s.CountIncludingPlanned("woodcutter") < 2) Wish("woodcutter");
        return wishes;
    }
}
