using Sim.Core.Util;

namespace Sim.Core.Citizens;

public static class Names
{
    private static readonly string[] Female =
    {
        "Ada", "Agnes", "Alys", "Beatrice", "Cecily", "Edith", "Elena", "Emma", "Isabel", "Joan",
        "Juliana", "Margery", "Matilda", "Maud", "Rose", "Sybil", "Avice", "Eleanor", "Hawise", "Lettice",
    };

    private static readonly string[] Male =
    {
        "Adam", "Alan", "Baldwin", "Geoffrey", "Gilbert", "Hugh", "John", "Ralph", "Richard", "Robert",
        "Roger", "Simon", "Thomas", "Walter", "William", "Osbert", "Hamo", "Nicholas", "Peter", "Odo",
    };

    private static readonly string[] Family =
    {
        "Ashdown", "Barrow", "Cooper", "Dunmere", "Fletcher", "Greaves", "Hale", "Ingram", "Kettle",
        "Larkin", "Mercer", "Northfield", "Oakes", "Pike", "Reeve", "Shaw", "Thatcher", "Underhill",
        "Wainwright", "Yarrow", "Brook", "Coldwell", "Fenwick", "Holt",
    };

    private static readonly string[] Places =
    {
        "Ashford", "Brackenridge", "Coldharbour", "Dunwick", "Elmstead", "Fairhaven", "Greywater",
        "Harrowmere", "Ironvale", "Kingsbrook", "Larkmoor", "Millbrook", "Northwold", "Oakhurst",
    };

    public static string First(DeterministicRandom rng, Sex sex) => rng.Pick(sex == Sex.Female ? Female : Male);
    public static string FamilyName(DeterministicRandom rng) => rng.Pick(Family);
    public static string Place(DeterministicRandom rng) => rng.Pick(Places);
}
