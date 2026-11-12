using Sim.Core.AI;
using Sim.Core.Events;
using Sim.Core.Settlements;
using Sim.Core.Systems;
using Sim.Core.Time;
using Sim.Core.World;

namespace Sim.Core.Simulation;

/// <summary>
/// Advances the world one tick at a time. Systems run at different frequencies:
/// every tick (work, eating), every game hour (jobs, housing, mood) and every game day
/// (life events, migration, AI planning).
/// </summary>
public sealed class Simulator
{
    public GameWorld World { get; }

    /// <summary>Raised after the daily systems run at the start of each new game day.</summary>
    public event Action<GameWorld>? DayEnded;

    public Simulator(GameWorld world)
    {
        World = world;
    }

    public void Step()
    {
        foreach (var s in World.Settlements)
        {
            ConstructionSystem.Tick(World, s);
            ProductionSystem.Tick(World, s);
            RoadSystem.Tick(World, s);
            Needs.Tick(World, s);
        }

        World.AdvanceTime();

        if (World.Time.IsStartOfHour)
        {
            foreach (var s in World.Settlements)
            {
                HousingSystem.Tick(World, s);
                JobSystem.Tick(World, s);
                Needs.UpdateMood(World, s);
            }
            Trade.TradeSystem.Hourly(World);
        }

        if (World.Time.IsStartOfDay)
        {
            foreach (var s in World.Settlements)
            {
                LifeSystem.Daily(World, s);
                ServiceSystem.Daily(World, s);
                MigrationSystem.Daily(World, s);
                EconomySystem.Daily(World, s);
                Commands.TerritorySystem.Daily(World, s);
                SettlementGovernor.Daily(World, s);
            }
            DayEnded?.Invoke(World);
        }
    }

    public void RunTicks(long ticks)
    {
        for (long i = 0; i < ticks; i++) Step();
    }

    public void RunDays(int days) => RunTicks((long)days * TimeScale.TicksPerDay);
}

/// <summary>Creates new settlements: picks a site, lays out starting buildings and settlers.</summary>
public static class SettlementFounder
{
    public const int StartingPopulation = 20;

    public static Settlement Found(GameWorld world, string? name = null, int nearX = 0, int nearY = 0,
        int population = StartingPopulation)
    {
        var (cx, cy) = FindSite(world, nearX, nearY);
        var settlement = new Settlement(world.NextId(), name ?? Citizens.Names.Place(world.Rng), cx, cy);
        world.Settlements.Add(settlement);

        settlement.Stock.Add("wood", 80);
        settlement.Stock.Add("stone", 30);
        settlement.Stock.Add("grain", 120);
        settlement.Stock.Add("bread", 20);
        settlement.Stock.Add(Economy.Inventory.Coin, world.Data.Trade.StartingCoin);

        // Territory: the starting town centre's cap, claimed evenly around the centre.
        var centre = world.Data.StartingTownCentre;
        settlement.TerritoryCap = centre?.TownCentre!.TerritoryCap ?? Commands.TerritoryRules.DefaultCap;
        Commands.TerritoryRules.ExpandEvenly(world, settlement);

        var starting = new List<string>();
        if (centre != null) starting.Add(centre.Id);
        starting.AddRange(new[] { "stockpile", "well", "hovel", "hovel", "hovel", "hovel", "farm", "farm", "woodcutter" });
        foreach (string id in starting)
        {
            var def = world.Data.Building(id);
            if (SiteFinder.Find(world, settlement, def) is not var (x, y)) continue;
            // The town centre can't be placed by command (only upgraded), so founding places it directly.
            int? created = def.IsTownCentre
                ? Commands.Placement.Place(world, settlement, def, x, y).Id
                : world.Execute(new Commands.PlaceBuildingCommand(settlement.Id, id, x, y)).CreatedId;
            if (created is int bid)
            {
                // Founding buildings start finished and free.
                var b = settlement.FindBuilding(bid)!;
                b.State = BuildingState.UnderConstruction;
                ConstructionSystem.Complete(world, settlement, b);
                RoadPlanner.Connect(world, settlement, b);
            }
        }

        while (settlement.Citizens.Count < population)
        {
            foreach (var c in CitizenFactory.CreateHousehold(world, population - settlement.Citizens.Count))
            {
                c.Remember(world.Time, $"Helped found {settlement.Name}");
                settlement.AddCitizen(c);
            }
        }

        Commands.TerritoryRules.ExpandEvenly(world, settlement);
        Commands.Roads.FinishAllPlans(world, settlement); // a new town starts with its roads built
        HousingSystem.Tick(world, settlement);
        JobSystem.Tick(world, settlement);
        ServiceSystem.Daily(world, settlement);
        world.Log(EventKind.SettlementFounded, settlement,
            $"{settlement.Name} was founded at ({cx},{cy}) by {settlement.Citizens.Count} settlers");
        return settlement;
    }

    /// <summary>Searches for open, buildable land with forest and rock within reach.</summary>
    public static (int X, int Y) FindSite(GameWorld world, int nearX, int nearY, int searchRadius = 160)
    {
        const int step = 8;
        (int, int) best = (nearX, nearY);
        double bestScore = double.MinValue;

        for (int y = nearY - searchRadius; y <= nearY + searchRadius; y += step)
        for (int x = nearX - searchRadius; x <= nearX + searchRadius; x += step)
        {
            if (!world.Map.GetTile(x, y).IsBuildable) continue;

            int open = CountBuildable(world, x, y, 8);
            if (open < 200) continue; // need room for the town core (17x17 = 289 tiles)

            int forest = Math.Min(world.Map.CountTerrainInRadius(x, y, 16, Terrain.Forest), 60);
            int rock = Math.Min(world.Map.CountTerrainInRadius(x, y, 20, Terrain.Rock), 30);
            if (forest < 20 || rock < 8) continue;

            double distance = Math.Abs(x - nearX) + Math.Abs(y - nearY);
            double score = open + forest * 0.8 + rock * 1.5 - distance * 0.1;
            if (score > bestScore)
            {
                bestScore = score;
                best = (x, y);
            }
        }
        return best;
    }

    private static int CountBuildable(GameWorld world, int cx, int cy, int radius)
    {
        int n = 0;
        for (int y = cy - radius; y <= cy + radius; y++)
        for (int x = cx - radius; x <= cx + radius; x++)
        {
            if (world.Map.GetTile(x, y).IsBuildable) n++;
        }
        return n;
    }
}
