using Sim.Core.Time;

namespace Sim.Core.Citizens;

public enum Sex
{
    Female,
    Male,
}

public enum Trait
{
    Hardworking,
    Lazy,
    Cheerful,
    Gloomy,
    Glutton,
    Hardy,
}

/// <summary>
/// A real, persistent townsperson. Everything that matters to the simulation lives here;
/// walking and animation are left to the client.
/// </summary>
public sealed class Citizen
{
    public const double AdultAgeYears = 14;
    public const double MaxSkill = 100;

    public int Id { get; }
    public string FirstName { get; }
    public string FamilyName { get; set; }
    public Sex Sex { get; }

    public long AgeDays { get; set; }
    public List<Trait> Traits { get; } = new();

    /// <summary>Skill per job kind, 0..100. Improves by doing the job.</summary>
    public SortedDictionary<string, double> Skills { get; } = new(StringComparer.Ordinal);

    public int? JobBuildingId { get; set; }
    public int? HomeBuildingId { get; set; }
    public int? SpouseId { get; set; }
    public List<int> ChildIds { get; } = new();
    public List<int> ParentIds { get; } = new();

    /// <summary>How fed the citizen is: 1 = full, 0 = starving.</summary>
    public double Satiation { get; set; } = 1.0;

    /// <summary>0..100. 50 is content; drives work speed, and later crime, emigration and rebellion.</summary>
    public double Mood { get; set; } = 55;

    /// <summary>Needs met by a service building near the citizen's home (updated daily).</summary>
    public SortedSet<string> CoveredNeeds { get; } = new(StringComparer.Ordinal);

    /// <summary>Last day each food was eaten, for the food variety bonus.</summary>
    public SortedDictionary<string, long> RecentFoods { get; } = new(StringComparer.Ordinal);

    /// <summary>Mood effect of the most recent meal.</summary>
    public double LastMealMood { get; set; }

    public long StarvingTicks { get; set; }
    public int UnhappyDays { get; set; }

    /// <summary>Days of sickness left; sick people don't work.</summary>
    public int SickDays { get; set; }

    public bool IsSick => SickDays > 0;

    /// <summary>The squad this citizen serves in, if called up. Soldiers don't work.</summary>
    public int? SquadId { get; set; }

    /// <summary>Savings: wages in, what the family buys out (see <see cref="Economy.HouseholdSystem"/>).</summary>
    public double Coin { get; set; }

    /// <summary>Notable life events, for the inspection panel and faction chronicle.</summary>
    public List<string> History { get; } = new();

    public Citizen(int id, string firstName, string familyName, Sex sex, long ageDays)
    {
        Id = id;
        FirstName = firstName;
        FamilyName = familyName;
        Sex = sex;
        AgeDays = ageDays;
    }

    public string FullName => $"{FirstName} {FamilyName}";
    public double AgeYears => AgeDays / (double)TimeScale.DaysPerYear;
    public bool IsAdult => AgeYears >= AdultAgeYears;

    /// <summary>Adults who aren't sick or serving can take a job (a general is excluded by the job system).</summary>
    public bool CanWork => IsAdult && !IsSick && SquadId == null;
    public bool IsStarving => Satiation <= 0;

    public bool Has(Trait trait) => Traits.Contains(trait);

    public double Skill(string jobKind) => Skills.TryGetValue(jobKind, out double s) ? s : 0;

    public void Remember(GameTime when, string text) => History.Add($"{when}: {text}");

    public override string ToString() => $"{FullName} ({(int)AgeYears})";
}
