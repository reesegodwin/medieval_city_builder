using Sim.Core.Economy;
using Sim.Core.Events;
using Sim.Core.Settlements;
using Sim.Core.Simulation;
using Sim.Core.Time;

namespace Sim.Core.Systems;

/// <summary>
/// The year's seasons: farms, gardens and fishing yield more or less (see <see cref="Data.SeasonRules"/> and
/// <see cref="EconomySystem.OutputFactor"/>), and in winter every occupied home burns firewood. Homes the town
/// can't supply are cold, which upsets the people in them. Food outside granaries slowly rots, fastest in
/// summer and not at all in winter, when it freezes.
/// </summary>
public static class SeasonSystem
{
    public const string Firewood = "firewood";

    /// <summary>Rough nutrition per stored food item, to turn days of food into a number of items.</summary>
    public const double AverageFoodNutrition = 0.8;

    /// <summary>Towns keep this much more than the winter reserve before selling food in autumn.</summary>
    public const double ReserveMargin = 1.25;

    /// <summary>Outside autumn, only food beyond this many winter reserves is sold (a big store, not the harvest).</summary>
    public const double OffSeasonReserveMargin = 1.5;

    public static int FoodStock(GameWorld world, Settlement s) => world.Data.FoodsByPreference.Sum(f => s.Stock.Get(f.Id));

    /// <summary>Food items to keep for winter: <see cref="Data.SeasonRules.WinterReserveDays"/> days of the town's need.</summary>
    public static double WinterReserve(GameWorld world, Settlement s) =>
        s.Citizens.Sum(Needs.DailyFoodNeed) * world.Data.Seasons.WinterReserveDays / AverageFoodNutrition;

    /// <summary>
    /// How much of a food the town can sell and still keep its winter reserve (with a margin, bigger outside
    /// autumn): the food's share of everything above it. Zero for anything that isn't food.
    /// </summary>
    public static int SellableFood(GameWorld world, Settlement s, string resource)
    {
        if (world.Data.Resources.GetValueOrDefault(resource)?.IsFood != true) return 0;
        int total = FoodStock(world, s);
        double excess = total - WinterReserve(world, s) * (world.Time.Season == Season.Autumn ? ReserveMargin : OffSeasonReserveMargin);
        return excess <= 0 || total == 0 ? 0 : (int)(excess * s.Stock.Get(resource) / total);
    }
    public static Data.SeasonDef Current(GameWorld world) => world.Data.Seasons.Of(world.Time.Season);

    public static void Daily(GameWorld world, Settlement s)
    {
        var season = Current(world);
        if (world.Time.Day > 0 && (world.Time.DayOfYear - 1) % TimeScale.DaysPerSeason == 0)
            world.Log(EventKind.Season, s, Announcement(world, world.Time.Season));
        Heat(world, s, season.HeatingWoodPerHomeDay);
        Spoil(world, s, season.Spoilage);
    }

    /// <summary>
    /// Food beyond what the granaries hold rots at each food's daily rate (scaled by the season). Granaries hold
    /// food first, so only the overflow is at risk.
    /// </summary>
    public static void Spoil(GameWorld world, Settlement s, double seasonFactor)
    {
        int total = FoodStock(world, s);
        double exposed = total == 0 ? 0 : Math.Max(0, total - s.FoodStoreCapacity) / (double)total;
        foreach (var food in world.Data.FoodsByPreference.OrderBy(f => f.Id, StringComparer.Ordinal))
        {
            if (food.SpoilPerDay <= 0) continue;
            double rot = s.Stock.Get(food.Id) * exposed * food.SpoilPerDay * seasonFactor + s.SpoilageOwed.GetValueOrDefault(food.Id);
            int whole = Math.Min((int)rot, s.Stock.Get(food.Id));
            if (whole > 0)
            {
                s.Stock.TryTake(food.Id, whole);
                s.Ledger.Record(food.Id, -whole, LedgerKeys.Spoiled);
            }
            double owed = rot - whole;
            if (owed > 1e-9 && exposed > 0) s.SpoilageOwed[food.Id] = Math.Min(owed, 1);
            else s.SpoilageOwed.Remove(food.Id);
        }
    }

    private static string Announcement(GameWorld world, Season season)
    {
        var def = world.Data.Seasons.Of(season);
        string crops = $"farms yield {def.OutputOf("farm"):P0}, fishing {def.OutputOf("fishing_hut"):P0}";
        return season switch
        {
            Season.Winter => $"Winter has come: {crops}, and every home burns firewood",
            Season.Spring => $"Spring: the thaw, and the fields are sown ({crops})",
            Season.Summer => $"Summer: {crops}",
            _ => $"Autumn and the harvest: {crops}. Store food for winter",
        };
    }

    /// <summary>Burns a day's firewood. Homes are heated in list order until the wood runs out.</summary>
    public static void Heat(GameWorld world, Settlement s, double woodPerHome)
    {
        var homes = s.Buildings.Where(b => b.IsOperational && b.Def.Housing > 0 && b.ResidentIds.Count > 0).ToList();
        if (woodPerHome <= 0)
        {
            foreach (var b in s.Buildings) b.Cold = false;
            s.HeatingOwed = 0;
            return;
        }

        double want = homes.Count * woodPerHome + s.HeatingOwed;
        int whole = (int)Math.Floor(want);
        int taken = Math.Min(whole, s.Stock.Get(Firewood));
        if (taken > 0)
        {
            s.Stock.TryTake(Firewood, taken);
            s.Ledger.Record(Firewood, -taken, LedgerKeys.Heating);
        }
        // Owed wood (less than a unit) carries over only when the day was paid in full.
        s.HeatingOwed = taken == whole ? want - whole : 0;
        int heated = taken == whole ? homes.Count : (int)Math.Floor(taken / woodPerHome);
        for (int i = 0; i < homes.Count; i++) homes[i].Cold = i >= heated;
    }

    /// <summary>Whether homes need heating today.</summary>
    public static bool HeatingSeason(GameWorld world) => Current(world).HeatingWoodPerHomeDay > 0;
}
