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

    public static void Tick(GameWorld world, Settlement settlement)
    {
        foreach (var c in settlement.Citizens)
        {
            c.Satiation = Math.Max(0, c.Satiation - DailyFoodNeed(c) / TimeScale.TicksPerDay);

            if (c.Satiation < EatBelow)
            {
                foreach (var food in world.Data.FoodsByPreference)
                {
                    while (c.Satiation < EatUntil && settlement.Stock.TryTake(food.Id, 1))
                    {
                        c.Satiation += food.Nutrition;
                        c.LastMealMood = food.MealMood;
                    }
                    if (c.Satiation >= EatUntil) break;
                }
            }

            if (c.IsStarving) c.StarvingTicks++;
            else c.StarvingTicks = 0;
        }
    }

    /// <summary>Hourly: mood drifts toward a target set by how well the citizen's needs are met.</summary>
    public static void UpdateMood(Settlement settlement)
    {
        foreach (var c in settlement.Citizens)
        {
            double target = 50 + c.LastMealMood;
            target += c.HomeBuildingId != null ? 5 : -20;
            if (c.IsAdult) target += c.JobBuildingId != null ? 3 : -3;
            if (c.Has(Trait.Cheerful)) target += 8;
            if (c.Has(Trait.Gloomy)) target -= 8;
            if (c.IsStarving) target -= 35;
            else if (c.Satiation < 0.25) target -= 10;

            c.Mood += (Math.Clamp(target, 0, 100) - c.Mood) * 0.1;
        }
    }
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
        if (averageMood < AttractiveMood || settlement.FreeHousing < 2 || Needs.FoodDays(world, settlement) < 3)
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
