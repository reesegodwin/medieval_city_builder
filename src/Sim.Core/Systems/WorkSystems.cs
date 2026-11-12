using Sim.Core.Citizens;
using Sim.Core.Events;
using Sim.Core.Settlements;
using Sim.Core.Simulation;
using Sim.Core.Time;

namespace Sim.Core.Systems;

/// <summary>
/// Hauling isn't simulated step by step. Instead each workplace loses efficiency based on the real
/// distance to the nearest stockpile, so town layout matters without per-citizen pathing.
/// </summary>
public static class Logistics
{
    /// <summary>Distance (in tiles) at which half of a worker's time goes to carrying goods.</summary>
    public const double HalfEfficiencyDistance = 40;

    public static void UpdateHaulFactors(Settlement settlement)
    {
        var stockpiles = settlement.Buildings.Where(b => b.IsOperational && b.Def.IsStockpile).ToList();
        foreach (var b in settlement.Buildings)
        {
            if (stockpiles.Count == 0)
            {
                b.HaulFactor = 0.5;
                continue;
            }
            int distance = stockpiles.Min(s => s.DistanceTo(b));
            b.HaulFactor = 1.0 / (1.0 + distance / HalfEfficiencyDistance);
        }
    }

    /// <summary>The operational stockpile closest to a building, or null if there is none.</summary>
    public static Building? NearestStockpile(Settlement settlement, Building from) =>
        settlement.Buildings
            .Where(b => b.IsOperational && b.Def.IsStockpile)
            .OrderBy(b => b.DistanceTo(from)).ThenBy(b => b.Id)
            .FirstOrDefault();
}

public static class Work
{
    /// <summary>Skill points gained per full day of work at skill 0 (slows down as skill rises).</summary>
    public const double SkillGainPerDay = 0.5;

    /// <summary>Worker-days of labour one citizen contributes per day. About 1.0 for an average worker.</summary>
    public static double DailyRate(Citizen c, string jobKind)
    {
        double skill = 0.75 + c.Skill(jobKind) / 200.0;
        double mood = 0.7 + 0.6 * (c.Mood / 100.0);
        double trait = c.Has(Trait.Hardworking) ? 1.15 : c.Has(Trait.Lazy) ? 0.85 : 1.0;
        double hunger = c.IsStarving ? 0.4 : 1.0;
        return skill * mood * trait * hunger;
    }

    /// <summary>Total labour contributed by a building's workers this tick, applying skill growth.</summary>
    public static double LabourThisTick(Settlement settlement, Building building, string jobKind)
    {
        double labour = 0;
        foreach (int id in building.WorkerIds)
        {
            var worker = settlement.FindCitizen(id);
            if (worker == null) continue;
            labour += DailyRate(worker, jobKind) / TimeScale.TicksPerDay;

            double skill = worker.Skill(jobKind);
            worker.Skills[jobKind] = Math.Min(Citizen.MaxSkill,
                skill + SkillGainPerDay / TimeScale.TicksPerDay * (1 - skill / Citizen.MaxSkill));
        }
        return labour;
    }

    public static void ReleaseWorkers(Settlement settlement, Building building)
    {
        foreach (int id in building.WorkerIds)
        {
            var c = settlement.FindCitizen(id);
            if (c != null) c.JobBuildingId = null;
        }
        building.WorkerIds.Clear();
    }
}

public static class ConstructionSystem
{
    public static void Tick(GameWorld world, Settlement settlement)
    {
        foreach (var b in settlement.Buildings)
        {
            if (b.State == BuildingState.AwaitingMaterials)
            {
                if (settlement.Stock.TryTakeAll(b.Def.Cost))
                {
                    b.State = BuildingState.UnderConstruction;
                    b.Problem = null;
                }
                else
                {
                    b.Problem = "Awaiting materials";
                }
                continue;
            }

            if (b.State != BuildingState.UnderConstruction) continue;

            b.ConstructionProgress += Work.LabourThisTick(settlement, b, JobKinds.Builder) * b.HaulFactor;
            b.Problem = b.WorkerIds.Count == 0 ? "No builders" : null;

            if (b.ConstructionProgress >= b.Def.BuildDays)
            {
                Complete(world, settlement, b);
            }
        }
    }

    public static void Complete(GameWorld world, Settlement settlement, Building b)
    {
        Work.ReleaseWorkers(settlement, b);
        b.State = BuildingState.Operational;
        b.ConstructionProgress = b.Def.BuildDays;
        b.Problem = null;
        if (b.Def.IsStockpile) Logistics.UpdateHaulFactors(settlement);
        world.Log(EventKind.BuildingCompleted, settlement, $"{b.Def.Name} completed");
    }
}

public static class ProductionSystem
{
    public static void Tick(GameWorld world, Settlement settlement)
    {
        foreach (var b in settlement.Buildings)
        {
            var recipe = b.Def.Recipe;
            if (!b.IsOperational || recipe == null || b.Def.JobKind == null) continue;

            if (settlement.IsAtProductionLimit(b) && !b.InputsLoaded)
            {
                b.Problem = "Stock limit reached";
                continue;
            }

            if (b.WorkerIds.Count == 0)
            {
                b.Problem = "No workers";
                continue;
            }

            if (!b.InputsLoaded)
            {
                string? missing = recipe.Inputs
                    .Where(kv => settlement.Stock.Get(kv.Key) - kv.Value < settlement.Rules.ReserveOf(kv.Key))
                    .Select(kv => kv.Key).FirstOrDefault();
                if (missing != null)
                {
                    b.Problem = $"Needs {missing}";
                    continue;
                }
                settlement.Stock.TryTakeAll(recipe.Inputs);
                b.InputsLoaded = true;
            }

            if (b.CycleProgress < recipe.WorkDays)
            {
                b.CycleProgress += Work.LabourThisTick(settlement, b, b.Def.JobKind) * b.HaulFactor;
            }

            if (b.CycleProgress < recipe.WorkDays)
            {
                b.Problem = null;
                continue;
            }

            var outputs = recipe.Outputs.ToDictionary(kv => kv.Key,
                kv => (int)Math.Round(kv.Value * b.TerrainMultiplier));
            if (outputs.Values.Sum() > settlement.FreeStorage)
            {
                b.Problem = "Storage full";
                continue;
            }

            foreach (var (resource, amount) in outputs) settlement.Stock.Add(resource, amount);
            b.CycleProgress -= recipe.WorkDays;
            b.InputsLoaded = false;
            b.Problem = null;
        }
    }
}

/// <summary>Assigns citizens to job slots according to the settlement's rules.</summary>
public static class JobSystem
{
    /// <summary>Workers moved between jobs per hour at most, to avoid constant churn.</summary>
    public const int MaxReassignmentsPerHour = 2;

    /// <summary>Bonus priority for food jobs when stores run low.</summary>
    public const int FoodEmergencyBonus = 10;

    /// <summary>Bonus priority for producers of a scarce resource.</summary>
    public const int ShortageBonus = 4;

    public static void Tick(GameWorld world, Settlement settlement)
    {
        // Drop assignments that are no longer valid (building finished construction, slots shrank, child...).
        foreach (var c in settlement.Citizens)
        {
            if (c.JobBuildingId is not int jobId) continue;
            var b = settlement.FindBuilding(jobId);
            if (b == null || !c.IsAdult || !b.WorkerIds.Contains(c.Id))
            {
                b?.WorkerIds.Remove(c.Id);
                c.JobBuildingId = null;
            }
        }
        foreach (var b in settlement.Buildings)
        {
            while (b.WorkerIds.Count > settlement.WantedWorkers(b))
            {
                var c = settlement.FindCitizen(b.WorkerIds[^1]);
                if (c != null) c.JobBuildingId = null;
                b.WorkerIds.RemoveAt(b.WorkerIds.Count - 1);
            }
        }

        bool foodEmergency = Needs.FoodDays(world, settlement) < 3;
        int Priority(Building b)
        {
            string kind = b.CurrentJobKind!;
            int p = settlement.Rules.PriorityOf(kind);
            if (foodEmergency && (kind == "farmer" || kind == "baker")) p += FoodEmergencyBonus;
            if (b.IsOperational && b.Def.Recipe is { } recipe &&
                recipe.Outputs.Keys.Any(r => settlement.Stock.Get(r) < settlement.Rules.LowStockThreshold))
                p += ShortageBonus;
            return p;
        }

        var openSlots = settlement.Buildings
            .Where(b => b.CurrentJobKind != null && b.WorkerIds.Count < settlement.WantedWorkers(b))
            .OrderByDescending(Priority).ThenBy(b => b.Id)
            .SelectMany(b => Enumerable.Repeat(b, settlement.WantedWorkers(b) - b.WorkerIds.Count))
            .ToList();

        var unemployed = settlement.Citizens.Where(c => c.IsAdult && c.JobBuildingId == null).ToList();
        int reassignments = 0;
        bool canSteal = true;
        foreach (var b in openSlots)
        {
            if (unemployed.Count == 0 && !canSteal) break;
            string kind = b.CurrentJobKind!;
            int priority = Priority(b);

            Citizen? candidate = null;
            foreach (var c in unemployed)
            {
                if (candidate == null || c.Skill(kind) > candidate.Skill(kind)) candidate = c;
            }
            if (candidate != null) unemployed.Remove(candidate);

            if (candidate == null && canSteal && reassignments < MaxReassignmentsPerHour)
            {
                // Pull someone from a less important job.
                candidate = settlement.Citizens
                    .Where(c => c.JobBuildingId != null)
                    .Select(c => (c, job: settlement.FindBuilding(c.JobBuildingId!.Value)!))
                    .Where(x => Priority(x.job) < priority)
                    .OrderBy(x => Priority(x.job)).ThenByDescending(x => x.c.Skill(kind)).ThenBy(x => x.c.Id)
                    .Select(x => x.c)
                    .FirstOrDefault();
                if (candidate != null)
                {
                    settlement.FindBuilding(candidate.JobBuildingId!.Value)!.WorkerIds.Remove(candidate.Id);
                    candidate.JobBuildingId = null;
                    reassignments++;
                }
                else
                {
                    canSteal = false; // slots are in priority order, so lower ones can't steal either
                }
            }

            if (candidate == null) continue;
            candidate.JobBuildingId = b.Id;
            b.WorkerIds.Add(candidate.Id);
        }
    }
}

/// <summary>Moves homeless citizens into free housing, keeping families together where possible.</summary>
public static class HousingSystem
{
    public static void Tick(GameWorld world, Settlement settlement)
    {
        foreach (var c in settlement.Citizens)
        {
            if (c.HomeBuildingId != null) continue;

            // Prefer the home of a spouse or parent.
            var relatives = (c.SpouseId is int s ? new[] { s } : Array.Empty<int>()).Concat(c.ParentIds);
            Building? home = relatives
                .Select(settlement.FindCitizen)
                .Where(r => r?.HomeBuildingId != null)
                .Select(r => settlement.FindBuilding(r!.HomeBuildingId!.Value))
                .FirstOrDefault(h => h != null && h.FreeHousing > 0);

            home ??= settlement.Buildings
                .Where(b => b.FreeHousing > 0)
                .OrderBy(b => b.ResidentIds.Count == 0 ? 1 : 0) // fill partly occupied homes first
                .ThenBy(b => b.Id)
                .FirstOrDefault();

            if (home == null) continue;
            home.ResidentIds.Add(c.Id);
            c.HomeBuildingId = home.Id;
        }
    }
}
