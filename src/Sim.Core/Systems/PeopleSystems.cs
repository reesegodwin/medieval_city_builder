using Sim.Core.Citizens;
using Sim.Core.Events;
using Sim.Core.Settlements;
using Sim.Core.Simulation;
using Sim.Core.Time;
using Sim.Core.Util;

namespace Sim.Core.Systems;

/// <summary>Hunger, eating and mood.</summary>
public static class Needs
{
    public const double EatBelow = 0.5;
    public const double EatUntil = 0.95;

    public static double DailyFoodNeed(Citizen c)
    {
        double need = c.IsAdult ? 1.0 : 0.6;
        return c.Has(Trait.Glutton) ? need * 1.25 : need;
    }

    /// <summary>How many days the current food stock would feed the settlement.</summary>
    public static double FoodDays(GameWorld world, Settlement settlement)
    {
        double stock = world.Data.FoodsByPreference.Sum(f => settlement.Stock.Get(f.Id) * f.Nutrition);
        double need = settlement.Citizens.Sum(DailyFoodNeed);
        return need <= 0 ? double.PositiveInfinity : stock / need;
    }

    /// <summary>Foods eaten within this many days count toward the variety bonus.</summary>
    public const int VarietyWindowDays = 3;
    public const double VarietyMoodPerFood = 3;
    public const double MaxVarietyMood = 6;

    public static void Tick(GameWorld world, Settlement settlement)
    {
        long today = world.Time.Day;
        foreach (var c in settlement.Citizens)
        {
            c.Satiation = Math.Max(0, c.Satiation - DailyFoodNeed(c) / TimeScale.TicksPerDay);

            if (c.Satiation < EatBelow)
            {
                foreach (var food in MealOrder(world, c))
                {
                    while (c.Satiation < EatUntil && settlement.Stock.TryTake(food.Id, 1))
                    {
                        settlement.Ledger.Record(food.Id, -1, Economy.LedgerKeys.Eaten);
                        c.Satiation += food.Nutrition;
                        c.LastMealMood = food.MealMood;
                        c.RecentFoods[food.Id] = today;
                    }
                    if (c.Satiation >= EatUntil) break;
                }
            }

            if (c.IsStarving) c.StarvingTicks++;
            else c.StarvingTicks = 0;
        }
    }

    /// <summary>
    /// Which foods a citizen reaches for first. Without a market they take the tastiest food in the
    /// stockpile; with a market nearby they can shop around and pick what they haven't had lately.
    /// </summary>
    private static IEnumerable<Data.ResourceDef> MealOrder(GameWorld world, Citizen c)
    {
        if (!c.CoveredNeeds.Contains("market")) return world.Data.FoodsByPreference;
        return world.Data.FoodsByPreference
            .OrderBy(f => c.RecentFoods.TryGetValue(f.Id, out long day) ? day : long.MinValue)
            .ThenByDescending(f => f.MealMood);
    }

    public static int RecentFoodCount(GameWorld world, Citizen c)
    {
        int count = 0;
        foreach (long day in c.RecentFoods.Values)
        {
            if (world.Time.Day - day <= VarietyWindowDays) count++;
        }
        return count;
    }

    /// <summary>Everything pushing a citizen's mood up or down, with labels (for the inspector).</summary>
    public static List<(string Label, double Mood)> MoodFactors(GameWorld world, Settlement settlement, Citizen c)
    {
        var factors = new List<(string, double)>();
        MoodSum(world, settlement, c, factors);
        return factors;
    }

    /// <summary>
    /// Sum of all mood effects. Labels are only built when <paramref name="labels"/> is given, since
    /// this runs for every citizen every game hour.
    /// </summary>
    public static double MoodSum(GameWorld world, Settlement settlement, Citizen c, List<(string, double)>? labels = null)
    {
        double sum = 0;
        void Add(string label, double mood)
        {
            if (mood == 0) return;
            sum += mood;
            labels?.Add((label, mood));
        }

        Add(c.LastMealMood > 0 ? "Tasty meals" : "Plain meals", c.LastMealMood);
        int foods = RecentFoodCount(world, c);
        double variety = Math.Min(MaxVarietyMood, Math.Max(0, foods - 1) * VarietyMoodPerFood);
        if (variety > 0) Add(labels != null ? $"Food variety ({foods} kinds)" : "", variety);
        Add(c.HomeBuildingId != null ? "Has a home" : "Homeless", c.HomeBuildingId != null ? 5 : -20);
        if (c.HomeBuildingId is int homeId && settlement.FindBuilding(homeId)?.Def is { Home.Mood: > 0 } fine)
            Add(labels != null ? $"Lives in a {fine.Name.ToLowerInvariant()}" : "", fine.Home!.Mood);
        if (c.IsAdult) Add(c.JobBuildingId != null ? "Has work" : "Idle", c.JobBuildingId != null ? 3 : -3);

        int population = settlement.Citizens.Count;
        foreach (var need in world.Data.Needs)
        {
            if (c.CoveredNeeds.Contains(need.Id)) Add(need.Name, need.CoveredMood);
            else if (population >= need.MinPopulation) Add(labels != null ? $"No {need.Name.ToLowerInvariant()}" : "", need.MissingMood);
        }

        if (c.Has(Trait.Cheerful)) Add("Cheerful", 8);
        if (c.Has(Trait.Gloomy)) Add("Gloomy", -8);
        if (c.IsStarving) Add("Starving", -35);
        else if (c.Satiation < 0.25) Add("Hungry", -10);
        return sum;
    }

    /// <summary>Hourly: mood drifts toward a target set by how well the citizen's needs are met.</summary>
    public static void UpdateMood(GameWorld world, Settlement settlement)
    {
        foreach (var c in settlement.Citizens)
        {
            double target = 50 + MoodSum(world, settlement, c);
            c.Mood += (Math.Clamp(target, 0, 100) - c.Mood) * 0.1;
        }
    }
}

/// <summary>
/// Daily: decides which homes each service building reaches (Songs of Syx-style coverage).
/// Each citizen uses the nearest staffed building of each kind that has room for them.
/// </summary>
public static class ServiceSystem
{
    public static void Daily(GameWorld world, Settlement settlement)
    {
        foreach (var c in settlement.Citizens) c.CoveredNeeds.Clear();

        foreach (var need in world.Data.Needs)
        {
            if (need.IsGoodsNeed) continue; // supplied after the services they come through
            var providers = settlement.Buildings
                .Where(b => b.Def.Service?.Need == need.Id)
                .ToList();
            foreach (var b in providers) b.ServedCount = 0;

            var open = providers.Where(b => b.IsOperational && (b.Def.Jobs == 0 || b.WorkerIds.Count > 0)).ToList();
            foreach (var b in providers.Where(b => b.IsOperational && !open.Contains(b))) b.Problem = "No staff";
            if (open.Count == 0) continue;

            // Goods a service uses up (e.g. ale): how many people the stock can serve today.
            var consumes = open[0].Def.Service!.ConsumesPerPersonDay;
            int canServe = consumes.Count == 0
                ? int.MaxValue
                : consumes.Min(kv => (int)(settlement.Stock.Get(kv.Key) / kv.Value));

            int served = 0;
            foreach (var c in settlement.Citizens)
            {
                if (served >= canServe) break;
                if (c.HomeBuildingId is not int homeId || settlement.FindBuilding(homeId) is not { } home) continue;

                Building? best = null;
                int bestDistance = int.MaxValue;
                foreach (var b in open)
                {
                    int d = b.DistanceTo(home);
                    int capacity = (int)(b.Def.Service!.Capacity * EconomySystem.OutputFactor(world, settlement, b));
                    if (d > b.Def.Service.Radius || b.ServedCount >= capacity || d >= bestDistance) continue;
                    best = b;
                    bestDistance = d;
                }
                if (best == null) continue;

                best.ServedCount++;
                served++;
                c.CoveredNeeds.Add(need.Id);
            }

            foreach (var (resource, perPerson) in consumes)
            {
                int used = Math.Min(settlement.Stock.Get(resource), (int)Math.Ceiling(served * perPerson));
                settlement.Stock.TryTake(resource, used);
                settlement.Ledger.Record(resource, -used, Economy.LedgerKeys.Used);
            }

            foreach (var b in open)
            {
                string? missing = consumes.Keys.FirstOrDefault(r => settlement.Stock.Get(r) < consumes[r]);
                b.Problem = canServe == 0 && missing != null ? $"Needs {missing}" : null;
            }
        }

        foreach (var need in world.Data.Needs)
            if (need.IsGoodsNeed) SupplyGoods(settlement, need);
    }

    /// <summary>
    /// Household goods (clothing, furniture...) reach people who have the service they come through (a market),
    /// in citizen order, as far as the stock lasts. Use builds up fractionally and whole units are taken.
    /// </summary>
    private static void SupplyGoods(Settlement settlement, Data.NeedDef need)
    {
        int canServe = int.MaxValue;
        foreach (var (resource, perPerson) in need.Goods)
        {
            double available = settlement.Stock.Get(resource) - settlement.GoodsUse.GetValueOrDefault(resource);
            canServe = Math.Min(canServe, Math.Max(0, (int)(available / perPerson)));
        }

        int served = 0;
        foreach (var c in settlement.Citizens)
        {
            if (served >= canServe) break;
            if (!c.CoveredNeeds.Contains(need.Via!)) continue;
            c.CoveredNeeds.Add(need.Id);
            served++;
        }

        foreach (var (resource, perPerson) in need.Goods)
        {
            double use = settlement.GoodsUse.GetValueOrDefault(resource) + served * perPerson;
            int whole = Math.Min(settlement.Stock.Get(resource), (int)use);
            if (whole > 0) settlement.Stock.TryTake(resource, whole);
            settlement.Ledger.Record(resource, -whole, Economy.LedgerKeys.Used);
            settlement.GoodsUse[resource] = use - (int)use;
        }
    }

    /// <summary>Share of citizens whose need is covered (0..1).</summary>
    public static double Coverage(Settlement settlement, string need) =>
        settlement.Citizens.Count == 0 ? 0 : settlement.Citizens.Count(c => c.CoveredNeeds.Contains(need)) / (double)settlement.Citizens.Count;
}

/// <summary>Daily life events: ageing, marriage, births and deaths.</summary>
public static class LifeSystem
{
    public const double StarvationDeathDays = 5;
    public const double HardyStarvationDeathDays = 8;
    public const double BirthChancePerDay = 1.0 / 600;
    public const double MarriageChancePerDay = 0.03;

    public static void Daily(GameWorld world, Settlement settlement)
    {
        var rng = world.Rng;
        var dying = new List<(Citizen, string)>();

        foreach (var c in settlement.Citizens)
        {
            bool wasAdult = c.IsAdult;
            c.AgeDays++;
            if (!wasAdult && c.IsAdult)
            {
                c.Remember(world.Time, "Came of age");
            }

            double starveLimit = c.Has(Trait.Hardy) ? HardyStarvationDeathDays : StarvationDeathDays;
            if (c.StarvingTicks >= TimeScale.DaysToTicks(starveLimit))
            {
                dying.Add((c, "starved to death"));
            }
            else if (c.AgeYears > 50 && rng.Chance(0.0004 * (c.AgeYears - 50)))
            {
                dying.Add((c, "died of old age"));
            }
        }

        foreach (var (c, cause) in dying) Kill(world, settlement, c, cause);

        Marriages(world, settlement);
        Births(world, settlement);
    }

    public static void Kill(GameWorld world, Settlement settlement, Citizen c, string cause)
    {
        Remove(settlement, c);
        if (c.SpouseId is int spouseId && settlement.FindCitizen(spouseId) is { } spouse)
        {
            spouse.SpouseId = null;
            spouse.Remember(world.Time, $"Widowed when {c.FirstName} {cause}");
        }
        world.TotalDeaths++;
        world.Log(cause.Contains("starv") ? EventKind.Starvation : EventKind.Death, settlement,
            $"{c} {cause}");
    }

    /// <summary>Removes a citizen from the settlement, freeing their job and home.</summary>
    public static void Remove(Settlement settlement, Citizen c)
    {
        if (c.JobBuildingId is int job) settlement.FindBuilding(job)?.WorkerIds.Remove(c.Id);
        if (c.HomeBuildingId is int home) settlement.FindBuilding(home)?.ResidentIds.Remove(c.Id);
        c.JobBuildingId = null;
        c.HomeBuildingId = null;
        settlement.RemoveCitizen(c);
    }

    private static bool Marriageable(Citizen c) => c.SpouseId == null && c.AgeYears is >= 16 and <= 45;

    private static void Marriages(GameWorld world, Settlement settlement)
    {
        var men = settlement.Citizens.Where(c => c.Sex == Sex.Male && Marriageable(c)).ToList();
        foreach (var woman in settlement.Citizens.Where(c => c.Sex == Sex.Female && Marriageable(c)).ToList())
        {
            if (men.Count == 0) break;
            if (!world.Rng.Chance(MarriageChancePerDay)) continue;

            var man = world.Rng.Pick(men);
            if (woman.ParentIds.Intersect(man.ParentIds).Any()) continue; // no siblings
            men.Remove(man);

            woman.SpouseId = man.Id;
            man.SpouseId = woman.Id;
            woman.FamilyName = man.FamilyName;
            woman.Remember(world.Time, $"Married {man.FullName}");
            man.Remember(world.Time, $"Married {woman.FirstName}");
            world.Log(EventKind.Marriage, settlement, $"{man.FullName} married {woman.FirstName}");

            // Move in together if there is room.
            if (man.HomeBuildingId is int home && woman.HomeBuildingId != home &&
                settlement.FindBuilding(home) is { FreeHousing: > 0 } newHome)
            {
                if (woman.HomeBuildingId is int old) settlement.FindBuilding(old)?.ResidentIds.Remove(woman.Id);
                newHome.ResidentIds.Add(woman.Id);
                woman.HomeBuildingId = home;
            }
        }
    }

    private static void Births(GameWorld world, Settlement settlement)
    {
        var mothers = settlement.Citizens
            .Where(c => c.Sex == Sex.Female && c.SpouseId != null && c.AgeYears is >= 16 and <= 42 &&
                        c.HomeBuildingId != null && !c.IsStarving)
            .ToList();

        foreach (var mother in mothers)
        {
            if (!world.Rng.Chance(BirthChancePerDay)) continue;
            var father = settlement.FindCitizen(mother.SpouseId!.Value);
            if (father == null) continue;

            var child = CitizenFactory.Create(world, mother.FamilyName, ageDays: 0, allowSkills: false);
            child.ParentIds.Add(mother.Id);
            child.ParentIds.Add(father.Id);
            mother.ChildIds.Add(child.Id);
            father.ChildIds.Add(child.Id);
            child.Remember(world.Time, $"Born to {mother.FirstName} and {father.FirstName}");
            settlement.AddCitizen(child);
            world.TotalBirths++;
            world.Log(EventKind.Birth, settlement, $"{child.FullName} was born to {mother.FirstName} and {father.FirstName}");
        }
    }
}

/// <summary>Daily: unhappy citizens leave, and a thriving settlement attracts newcomers.</summary>
public static class MigrationSystem
{
    public const double UnhappyMood = 25;
    public const int UnhappyDaysBeforeLeaving = 4;
    public const double AttractiveMood = 55;
    public const double ArrivalChancePerDay = 0.35;

    /// <summary>
    /// Newcomers stay away while more adults than this share (and at least <see cref="MinJoblessForArrivals"/>)
    /// have no work at all. Haulers count as working.
    /// </summary>
    public const double JoblessShareLimit = 0.25;
    public const int MinJoblessForArrivals = 4;

    public static bool HasWorkForNewcomers(Settlement settlement)
    {
        int adults = 0, jobless = 0;
        foreach (var c in settlement.Citizens)
        {
            if (!c.IsAdult) continue;
            adults++;
            if (c.JobBuildingId == null) jobless++;
        }
        return jobless <= Math.Max(MinJoblessForArrivals, JoblessShareLimit * adults);
    }

    public static void Daily(GameWorld world, Settlement settlement)
    {
        foreach (var c in settlement.Citizens.ToList())
        {
            c.UnhappyDays = c.Mood < UnhappyMood ? c.UnhappyDays + 1 : 0;
            if (c.IsAdult && c.UnhappyDays >= UnhappyDaysBeforeLeaving && settlement.HasCitizen(c))
            {
                Leave(world, settlement, c);
            }
        }

        if (settlement.Citizens.Count == 0) return;
        double averageMood = settlement.Citizens.Average(c => c.Mood);
        if (averageMood < AttractiveMood || settlement.FreeHousing < 2 || Needs.FoodDays(world, settlement) < 3 ||
            !HasWorkForNewcomers(settlement))
            return;
        if (!world.Rng.Chance(ArrivalChancePerDay)) return;

        var arrivals = CitizenFactory.CreateHousehold(world, maxSize: Math.Min(4, settlement.FreeHousing));
        foreach (var c in arrivals)
        {
            c.Remember(world.Time, $"Arrived in {settlement.Name}");
            settlement.AddCitizen(c);
        }
        string names = string.Join(", ", arrivals.Select(a => a.FirstName));
        world.Log(EventKind.Arrival, settlement, $"Newcomers arrived: {names} {arrivals[0].FamilyName}");
    }

    private static void Leave(GameWorld world, Settlement settlement, Citizen c)
    {
        var leaving = new List<Citizen> { c };
        leaving.AddRange(c.ChildIds.Select(settlement.FindCitizen).OfType<Citizen>().Where(k => !k.IsAdult));
        foreach (var person in leaving) LifeSystem.Remove(settlement, person);
        if (c.SpouseId is int s && settlement.FindCitizen(s) is { } spouse)
        {
            spouse.SpouseId = null;
            spouse.Remember(world.Time, $"Abandoned by {c.FirstName}");
        }
        world.Log(EventKind.Departure, settlement,
            $"{c} left in misery" + (leaving.Count > 1 ? $", taking {leaving.Count - 1} children" : ""));
    }
}

public static class CitizenFactory
{
    private static readonly string[] StartingJobKinds = { "farmer", "baker", "woodcutter", "quarrier", "builder" };
    private static readonly Trait[] AllTraits = Enum.GetValues<Trait>();

    public static Citizen Create(GameWorld world, string familyName, long ageDays, bool allowSkills = true, Sex? sex = null)
    {
        var rng = world.Rng;
        var s = sex ?? (rng.Chance(0.5) ? Sex.Female : Sex.Male);
        var c = new Citizen(world.NextId(), Names.First(rng, s), familyName, s, ageDays);

        int traitCount = rng.Next(3);
        for (int i = 0; i < traitCount; i++)
        {
            var t = rng.Pick(AllTraits);
            if (c.Traits.Contains(t) || c.Traits.Contains(Opposite(t))) continue;
            c.Traits.Add(t);
        }

        if (allowSkills)
        {
            int skilled = rng.Next(1, 3);
            for (int i = 0; i < skilled; i++) c.Skills[rng.Pick(StartingJobKinds)] = rng.Next(5, 35);
        }
        return c;
    }

    /// <summary>A single adult, or a couple with a few children.</summary>
    public static List<Citizen> CreateHousehold(GameWorld world, int maxSize)
    {
        var rng = world.Rng;
        string family = Names.FamilyName(rng);
        long AdultAge() => (long)TimeScale.YearsToDays(rng.Next(18, 46)) + rng.Next(TimeScale.DaysPerYear);

        var first = Create(world, family, AdultAge());
        var household = new List<Citizen> { first };
        if (maxSize < 2 || rng.Chance(0.3)) return household;

        var partner = Create(world, family, AdultAge(), sex: first.Sex == Sex.Male ? Sex.Female : Sex.Male);
        first.SpouseId = partner.Id;
        partner.SpouseId = first.Id;
        household.Add(partner);

        int children = Math.Min(rng.Next(0, 4), maxSize - 2);
        for (int i = 0; i < children; i++)
        {
            var child = Create(world, family, (long)TimeScale.YearsToDays(rng.Next(1, 13)), allowSkills: false);
            foreach (var parent in new[] { first, partner })
            {
                child.ParentIds.Add(parent.Id);
                parent.ChildIds.Add(child.Id);
            }
            household.Add(child);
        }
        return household;
    }

    private static Trait Opposite(Trait t) => t switch
    {
        Trait.Hardworking => Trait.Lazy,
        Trait.Lazy => Trait.Hardworking,
        Trait.Cheerful => Trait.Gloomy,
        Trait.Gloomy => Trait.Cheerful,
        _ => t,
    };
}
