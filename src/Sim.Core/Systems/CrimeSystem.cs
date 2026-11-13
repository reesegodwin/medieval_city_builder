using Sim.Core.Citizens;
using Sim.Core.Economy;
using Sim.Core.Events;
using Sim.Core.Settlements;
using Sim.Core.Simulation;

namespace Sim.Core.Systems;

/// <summary>
/// Law and crime, daily. Troubled adults (unhappy, or from a family with nothing saved) who live out of the town
/// watch's reach may steal: each one adds a little to the day's chance of a theft. A thief robs the richest home the
/// watch doesn't cover (or, if no family has much, the treasury) and the loot goes to the thief's family. A theft
/// unsettles everyone the watch doesn't protect for some days. Guardhouses (the "law" need) prevent both.
/// </summary>
public static class CrimeSystem
{
    public static bool Troubled(GameWorld world, Settlement s, Citizen c) =>
        c.IsAdult && c.SquadId == null && !c.CoveredNeeds.Contains("law") &&
        (c.Mood < world.Data.Disasters.TroubledMood || HouseholdSystem.FamilyPurse(s, c) < world.Data.Disasters.TroubledPurse);

    public static void Daily(GameWorld world, Settlement s)
    {
        if (s.CrimeDays > 0) s.CrimeDays--;
        var rules = world.Data.Disasters;
        if (!s.Rules.Crime || s.Citizens.Count < rules.CrimeMinPopulation) return;

        var troubled = s.Citizens.Where(c => Troubled(world, s, c)).ToList();
        if (troubled.Count == 0) return;
        double chance = Math.Min(rules.CrimeMaxChance, troubled.Count * rules.CrimeChancePerTroubled);
        if (!world.Rng.Chance(chance)) return;

        var thief = troubled[world.Rng.Next(troubled.Count)];
        var thiefHome = thief.HomeBuildingId is int th ? s.FindBuilding(th) : null;

        // The richest family the watch doesn't protect (in list order on ties).
        Building? victim = null;
        double best = 0;
        foreach (var home in s.Buildings)
        {
            if (home.Def.Housing <= 0 || home == thiefHome || home.ResidentIds.Count == 0) continue;
            if (home.ResidentIds.Any(id => s.FindCitizen(id)?.CoveredNeeds.Contains("law") == true)) continue;
            double purse = HouseholdSystem.Purse(s, home);
            if (purse > best)
            {
                best = purse;
                victim = home;
            }
        }

        double loot;
        string whom;
        if (victim != null && best >= 1)
        {
            loot = Math.Min(rules.MaxTheft, best * rules.TheftShare);
            double keep = 1 - loot / best;
            foreach (int id in victim.ResidentIds)
                if (s.FindCitizen(id) is { IsAdult: true } c) c.Coin *= keep;
            var head = victim.ResidentIds.Select(s.FindCitizen).OfType<Citizen>().FirstOrDefault(c => c.IsAdult);
            whom = $"the {head?.FamilyName ?? "unknown"} family";
        }
        else
        {
            // Nobody worth robbing at home: the treasury, unless the watch guards the town centre.
            if (s.Buildings.FirstOrDefault(b => b.Def.TownCentre != null) is { } centre &&
                s.Buildings.Any(b => b.Def.Service?.Need == "law" && b.IsOperational && b.DistanceTo(centre) <= b.Def.Service.Radius))
                return;
            int coin = s.Stock.Get(Inventory.Coin);
            int taken = (int)Math.Min(rules.MaxTheft, coin * rules.TheftShare * 0.1);
            if (taken < 1) return;
            s.Stock.TryTake(Inventory.Coin, taken);
            s.Ledger.Record(Inventory.Coin, -taken, LedgerKeys.Disaster_("theft"));
            loot = taken;
            whom = "the treasury";
        }

        thief.Coin += loot;
        s.Crimes++;
        s.CrimeDays = rules.CrimeMoodDays;
        thief.Remember(world.Time, "Stole to get by");
        world.Log(EventKind.Crime, s, $"Theft: {loot:0} coin stolen from {whom}. A town watch (guardhouse) keeps homes safe");
    }
}

/// <summary>Turns crime on or off for a town.</summary>
public sealed record SetCrimeCommand(int SettlementId, bool Enabled) : Commands.ISettlementCommand
{
    public Commands.CommandResult Execute(GameWorld world)
    {
        var s = world.FindSettlement(SettlementId);
        if (s == null) return Commands.CommandResult.Fail("Unknown settlement");
        s.Rules.Crime = Enabled;
        if (!Enabled) s.CrimeDays = 0;
        return Commands.CommandResult.Success();
    }
}
