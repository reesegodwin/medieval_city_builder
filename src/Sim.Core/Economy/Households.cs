using Sim.Core.Citizens;
using Sim.Core.Settlements;
using Sim.Core.Simulation;
using Sim.Core.Systems;

namespace Sim.Core.Economy;

/// <summary>What one need costs a household a day (households.json "prices"), in the order families pay for them.</summary>
public sealed class NeedPrice
{
    public string Need { get; set; } = "";
    public double Price { get; set; }

    /// <summary>Paid once per home (heating), rather than per person.</summary>
    public bool PerHome { get; set; }
}

/// <summary>A way of running a town: how wages are spread, what is taxed, what the poor get for free.</summary>
public sealed class GovernmentDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>Share of every wage that goes to the treasury.</summary>
    public double TaxRate { get; set; } = 0.1;

    /// <summary>1 = pay by job tier and skill; 0 = everyone gets the same (the middle tier's wage).</summary>
    public double WageSpread { get; set; } = 1;

    /// <summary>Multiplies what households pay for what they use.</summary>
    public double PriceFactor { get; set; } = 1;

    /// <summary>Multiplies soldiers' pay.</summary>
    public double SoldierPay { get; set; } = 1;

    /// <summary>Needs the town gives for free to households that can't pay for them.</summary>
    public List<string> Relief { get; set; } = new();
}

/// <summary>The household economy (households.json).</summary>
public sealed class HouseholdRules
{
    /// <summary>Daily wage of a labourer, a skilled worker and a master (job tiers 1–3).</summary>
    public List<double> TierWages { get; set; } = new() { 0.12, 0.18, 0.26 };

    /// <summary>Wages range from (1 - spread/2) for a novice to (1 + spread/2) for a master of the job.</summary>
    public double SkillSpread { get; set; } = 0.4;

    /// <summary>Share of a wage paid while the workplace stands idle (stock limit reached).</summary>
    public double StandbyShare { get; set; } = 0.5;

    /// <summary>What an adult without a job earns from odd jobs (selling berries, kindling...).</summary>
    public double OddJobWage { get; set; } = 0.04;

    public double GeneralPay { get; set; } = 0.4;

    /// <summary>A child costs this share of what an adult does.</summary>
    public double ChildShare { get; set; } = 0.5;

    /// <summary>Coin each new adult (settler or newcomer) brings.</summary>
    public double StartingCoin { get; set; } = 4;

    /// <summary>To take up gear a recruit's family must have this share of its value in savings.</summary>
    public double GearAffordShare { get; set; } = 0.4;

    /// <summary>Jobs the treasury pays for (the rest are paid by what they make).</summary>
    public List<string> CityJobs { get; set; } = new() { "builder", "hauler", "roadworker" };

    /// <summary>Job kind → tier (1 labourer, 2 skilled, 3 master); unlisted jobs are tier 1.</summary>
    public Dictionary<string, int> JobTiers { get; set; } = new();

    public List<NeedPrice> Prices { get; set; } = new();
    public List<GovernmentDef> Governments { get; set; } = new() { new() { Id = "free_traders", Name = "Free Traders", Relief = { "food" } } };

    public GovernmentDef Government(string id) => Governments.FirstOrDefault(g => g.Id == id) ?? Governments[0];

    public int TierOf(string jobKind) => Math.Clamp(JobTiers.GetValueOrDefault(jobKind, 1), 1, TierWages.Count);
}

/// <summary>
/// The household economy, daily, after services are worked out:
/// <list type="number">
/// <item>Payday. Workers earn by their job's tier and their skill at it (the government evens this out); the jobless
/// earn a little from odd jobs; soldiers and the general draw pay. Income tax is kept back.</item>
/// <item>Each home pays, from its adults' purses, for what its people used today: food, warmth, then services and
/// goods in order. What a family can't afford it goes without (that need counts as unmet), unless the government
/// gives it to the poor for free.</item>
/// </list>
/// Wages for producing work are new coin (the value of what was made); the treasury pays city jobs (builders,
/// haulers, roadworkers) and soldiers as upkeep, and receives income tax and what households spend.
/// One pass over citizens and one over homes: cheap for thousands of people.
/// </summary>
public static class HouseholdSystem
{
    public const string SalesKey = "sales";

    /// <summary>A citizen's gross pay today, and whether the treasury pays it (city jobs, soldiers, the general).</summary>
    public static (double Pay, bool City) Wage(GameWorld world, Settlement s, Citizen c)
    {
        var rules = world.Data.Households;
        var gov = rules.Government(s.Rules.Government);
        if (c.SquadId is int squadId)
        {
            var squad = s.Squads.FirstOrDefault(q => q.Id == squadId);
            return ((world.Data.War.Unit(squad?.UnitId ?? "")?.PayPerSoldierDay ?? 0) * gov.SoldierPay, true);
        }
        if (s.General?.CitizenId == c.Id) return (rules.GeneralPay * gov.SoldierPay, true);
        if (!c.CanWork) return (0, false);
        if (c.JobBuildingId is not int jobId || s.FindBuilding(jobId) is not { } job || job.CurrentJobKind is not { } kind)
            return (rules.OddJobWage, false);

        double flat = rules.TierWages[rules.TierWages.Count / 2];
        double tierWage = rules.TierWages[rules.TierOf(kind) - 1];
        double skill = c.Skills.GetValueOrDefault(kind) / Citizen.MaxSkill;
        double skillFactor = 1 + rules.SkillSpread * (skill - 0.5);
        double pay = flat + gov.WageSpread * (tierWage * skillFactor - flat);
        if (s.IsOnStandby(job)) pay *= rules.StandbyShare;
        return (pay, rules.CityJobs.Contains(kind));
    }

    /// <summary>Income tax (soldiers and the general pay none).</summary>
    public static double TaxOn(GameWorld world, Settlement s, Citizen c, double pay) =>
        c.SquadId != null || s.General?.CitizenId == c.Id ? 0 : pay * world.Data.Households.Government(s.Rules.Government).TaxRate;

    /// <summary>What the treasury pays city workers a day (part of upkeep).</summary>
    public static double CityWages(GameWorld world, Settlement s)
    {
        double total = 0;
        foreach (var c in s.Citizens)
        {
            if (c.SquadId != null || s.General?.CitizenId == c.Id) continue; // soldiers' pay is the army's upkeep
            var (pay, city) = Wage(world, s, c);
            if (city) total += pay;
        }
        return total;
    }

    /// <summary>A family's savings: what its adults hold.</summary>
    public static double Purse(Settlement s, Building home) =>
        home.ResidentIds.Select(s.FindCitizen).OfType<Citizen>().Where(c => c.IsAdult).Sum(c => c.Coin);

    /// <summary>The family purse a citizen draws on: their home's, or their own if homeless.</summary>
    public static double FamilyPurse(Settlement s, Citizen c) =>
        c.HomeBuildingId is int h && s.FindBuilding(h) is { } home ? Purse(s, home) : c.Coin;

    public static void Daily(GameWorld world, Settlement s)
    {
        var rules = world.Data.Households;
        var gov = rules.Government(s.Rules.Government);

        // 1. Payday.
        foreach (var c in s.Citizens)
        {
            var (pay, _) = Wage(world, s, c);
            if (pay > 0) c.Coin += pay - TaxOn(world, s, c, pay);
        }

        // 2. Families pay for what they use.
        double sales = 0;
        int relief = 0, poor = 0;
        bool heating = SeasonSystem.HeatingSeason(world);
        var members = new List<Citizen>();
        foreach (var home in s.Buildings)
        {
            if (home.Def.Housing <= 0 || !home.IsOperational || home.ResidentIds.Count == 0) continue;
            members.Clear();
            foreach (int id in home.ResidentIds)
                if (s.FindCitizen(id) is { } c) members.Add(c);
            Pay(home, members);
        }
        foreach (var c in s.Citizens)
        {
            if (c.HomeBuildingId != null) continue;
            members.Clear();
            members.Add(c);
            Pay(null, members);
        }

        s.ReliefHouseholds = relief;
        s.PoorHouseholds = poor;
        s.SalesAccrued += sales;
        int whole = (int)s.SalesAccrued;
        if (whole > 0)
        {
            s.SalesAccrued -= whole;
            s.Stock.Add(Inventory.Coin, whole);
            s.Ledger.Record(Inventory.Coin, whole, SalesKey);
        }

        void Pay(Building? home, List<Citizen> family)
        {
            double purse = 0;
            foreach (var c in family)
                if (c.IsAdult) purse += c.Coin;
            double start = purse;
            bool helped = false, without = false;

            foreach (var price in rules.Prices)
            {
                if (price.Price <= 0) continue;
                double weight = 0;
                if (price.PerHome) weight = price.Need == "heating" && heating && home is { Cold: false } ? 1 : 0;
                else
                    foreach (var c in family)
                        if (price.Need == "food" || c.CoveredNeeds.Contains(price.Need))
                            weight += c.IsAdult ? 1 : rules.ChildShare;
                double cost = price.Price * gov.PriceFactor * weight;
                if (cost <= 0) continue;

                if (purse >= cost)
                {
                    purse -= cost;
                    sales += cost;
                }
                else if (gov.Relief.Contains(price.Need)) helped = true; // the town gives it to the poor
                else
                {
                    without = true;
                    if (price.Need == "heating") home!.Cold = true; // can't buy firewood: the home goes cold
                    else
                        foreach (var c in family) c.CoveredNeeds.Remove(price.Need);
                }
            }

            // Take what was spent from the adults' purses, in proportion to what each held.
            if (start > 0 && purse < start)
            {
                double keep = purse / start;
                foreach (var c in family)
                    if (c.IsAdult) c.Coin *= keep;
            }
            if (helped) relief++;
            if (without) poor++;
        }
    }

    /// <summary>When someone dies or leaves, their savings go to their spouse, else the other adults at home.</summary>
    public static void Inherit(Settlement s, Citizen c)
    {
        if (c.Coin <= 0) return;
        var heir = c.SpouseId is int sp ? s.FindCitizen(sp) : null;
        heir ??= c.HomeBuildingId is int h && s.FindBuilding(h) is { } home
            ? home.ResidentIds.Where(id => id != c.Id).Select(s.FindCitizen).OfType<Citizen>().FirstOrDefault(o => o.IsAdult)
            : null;
        if (heir != null) heir.Coin += c.Coin;
        else s.SalesAccrued += c.Coin; // no heir: it goes to the town
        c.Coin = 0;
    }

    /// <summary>What a family must have saved before one of its people can take up a unit's gear.</summary>
    public static double GearPrice(GameWorld world, War.UnitDef unit) =>
        unit.Gear.Sum(kv => kv.Value * (world.Data.Resources.GetValueOrDefault(kv.Key)?.Value ?? 0)) * world.Data.Households.GearAffordShare;
}

/// <summary>Changes how the town is run (households.json "governments").</summary>
public sealed record SetGovernmentCommand(int SettlementId, string GovernmentId) : Commands.ISettlementCommand
{
    public Commands.CommandResult Execute(GameWorld world)
    {
        var s = world.FindSettlement(SettlementId);
        if (s == null) return Commands.CommandResult.Fail("Unknown settlement");
        var gov = world.Data.Households.Governments.FirstOrDefault(g => g.Id == GovernmentId);
        if (gov == null) return Commands.CommandResult.Fail("Unknown government");
        if (s.Rules.Government == gov.Id) return Commands.CommandResult.Success();
        s.Rules.Government = gov.Id;
        world.Log(Events.EventKind.Government, s, $"The town is now run by the {gov.Name}");
        return Commands.CommandResult.Success();
    }
}
