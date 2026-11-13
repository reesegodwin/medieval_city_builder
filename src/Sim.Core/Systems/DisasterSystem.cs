using Sim.Core.Economy;
using Sim.Core.Events;
using Sim.Core.Settlements;
using Sim.Core.Simulation;
using Sim.Core.Time;

namespace Sim.Core.Systems;

/// <summary>
/// Mild disasters: fires that put a building out of use until builders repair it, sickness that keeps a few
/// people off work for some days, and poor harvests. Nothing is lost for good except some goods in a burning
/// stockpile. Wells lower the chance of fire nearby; water and faith lower the chance of sickness. Each
/// settlement can switch disasters off (<see cref="SettlementRules.Disasters"/>).
/// </summary>
public static class DisasterSystem
{
    public static void Daily(GameWorld world, Settlement s)
    {
        // Recovery happens even with disasters switched off.
        // Herbalists help the sick they reach get well sooner.
        foreach (var c in s.Citizens)
            if (c.SickDays > 0) c.SickDays = Math.Max(0, c.SickDays - (c.CoveredNeeds.Contains("healing") ? world.Data.Disasters.HealingRecovery : 1));
        if (s.PoorHarvestDays > 0) s.PoorHarvestDays--;

        if (!s.Rules.Disasters) return;
        var rules = world.Data.Disasters;
        Fires(world, s, rules);
        Sickness(world, s, rules);
        if (world.Time.DayOfYear == TimeScale.DaysPerSeason * (int)Season.Autumn + 1 && s.Citizens.Count > 0 &&
            s.Buildings.Any(b => b.IsOperational && b.Def.FertileBonus > 0) && world.Rng.Chance(rules.PoorHarvestChance))
        {
            s.PoorHarvestDays = rules.PoorHarvestDays;
            world.Log(EventKind.Disaster, s,
                $"A poor harvest: farms and gardens yield {1 - rules.PoorHarvestOutput:P0} less for {rules.PoorHarvestDays} days");
        }
    }

    private static void Fires(GameWorld world, Settlement s, Data.DisasterRules rules)
    {
        var wells = s.Buildings.Where(b => b.IsOperational && b.Def.Service?.Need == "water").ToList();
        foreach (var b in s.Buildings.ToList())
        {
            if (!b.IsOperational || b.Def.IsTownCentre || b.Def.BuildDays <= 0) continue;
            double chance = rules.FireChancePerBuildingDay;
            if (wells.Any(w => w != b && w.DistanceTo(b) <= w.Def.Service!.Radius)) chance *= rules.WellFireFactor;
            if (world.Rng.Chance(chance)) Burn(world, s, b, rules);
        }
    }

    /// <summary>Sets a building on fire: it needs repairs (labour only) before it works again.</summary>
    public static void Burn(GameWorld world, Settlement s, Building b, Data.DisasterRules rules)
    {
        string lost = "";
        if (b.Def.IsStockpile && s.StorageCapacity > 0)
        {
            double share = rules.StockpileFireLoss * b.Def.Storage / s.StorageCapacity;
            var burned = new List<string>();
            foreach (var (resource, amount) in s.Stock.Items.Where(kv => kv.Key != Inventory.Coin).ToList())
            {
                int loss = (int)Math.Floor(amount * share);
                if (loss <= 0 || !s.Stock.TryTake(resource, loss)) continue;
                s.Ledger.Record(resource, -loss, LedgerKeys.Disaster_("fire"));
                burned.Add($"{loss} {world.Data.Resources.GetValueOrDefault(resource)?.Name.ToLowerInvariant() ?? resource}");
            }
            if (burned.Count > 0) lost = $"; lost {string.Join(", ", burned.Take(4))}{(burned.Count > 4 ? " and more" : "")}";
        }

        Work.ReleaseWorkers(s, b);
        foreach (int id in b.ResidentIds)
            if (s.FindCitizen(id) is { } resident) resident.HomeBuildingId = null;
        bool hadResidents = b.ResidentIds.Count > 0;
        b.ResidentIds.Clear();

        b.State = BuildingState.UnderConstruction;
        b.ConstructionProgress = b.Def.BuildDays * (1 - rules.RepairShare);
        b.CycleProgress = 0;
        b.InputsLoaded = false;
        b.UpgradeReadyDays = 0;
        b.Problem = null;
        if (b.Def.IsStockpile) Logistics.UpdateHaulFactors(world, s);

        world.Log(EventKind.Disaster, s,
            $"Fire! The {b.Def.Name} burned{(hadResidents ? " and its family needs a new home" : "")}; builders will repair it{lost}");
    }

    private static void Sickness(GameWorld world, Settlement s, Data.DisasterRules rules)
    {
        int population = s.Citizens.Count;
        if (population < rules.SicknessMinPopulation) return;
        if (s.Citizens.Any(c => c.IsSick)) return; // one outbreak at a time

        double crowding = Math.Clamp(population / (double)Math.Max(1, s.HousingCapacity), 0.5, 2.0);
        double chance = rules.SicknessChancePerDay * crowding *
                        (1 - rules.WaterProtection * ServiceSystem.Coverage(s, "water")) *
                        (1 - rules.FaithProtection * ServiceSystem.Coverage(s, "faith")) *
                        (1 - rules.HygieneProtection * ServiceSystem.Coverage(s, "hygiene")) *
                        (1 - rules.HealingProtection * ServiceSystem.Coverage(s, "healing"));
        if (!world.Rng.Chance(chance)) return;

        double share = rules.SickShareMin + world.Rng.NextDouble() * (rules.SickShareMax - rules.SickShareMin);
        int count = Math.Max(1, (int)Math.Round(population * share));
        var healthy = s.Citizens.ToList();
        for (int i = 0; i < count && healthy.Count > 0; i++)
        {
            var c = healthy[world.Rng.Next(healthy.Count)];
            healthy.Remove(c);
            c.SickDays = world.Rng.Next(rules.SickDaysMin, rules.SickDaysMax + 1);
        }
        world.Log(EventKind.Disaster, s, count == 1 ? "Sickness: one person is ill and can't work for a few days"
            : $"Sickness: {count} people are ill and can't work for a few days");
    }
}
