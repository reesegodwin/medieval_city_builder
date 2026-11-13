using Sim.Core.Economy;
using Sim.Core.Events;
using Sim.Core.Settlements;
using Sim.Core.Simulation;
using Sim.Core.Time;

namespace Sim.Core.Systems;

/// <summary>
/// The year's seasons: farms, gardens and fishing yield more or less (see <see cref="Data.SeasonRules"/> and
/// <see cref="EconomySystem.OutputFactor"/>), and in winter every occupied home burns firewood. Homes the town
/// can't supply are cold, which upsets the people in them.
/// </summary>
public static class SeasonSystem
{
    public static Data.SeasonDef Current(GameWorld world) => world.Data.Seasons.Of(world.Time.Season);

    public static void Daily(GameWorld world, Settlement s)
    {
        var season = Current(world);
        if (world.Time.Day > 0 && (world.Time.DayOfYear - 1) % TimeScale.DaysPerSeason == 0)
            world.Log(EventKind.Season, s, Announcement(world, world.Time.Season));
        Heat(world, s, season.HeatingWoodPerHomeDay);
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
        int taken = Math.Min(whole, s.Stock.Get("wood"));
        if (taken > 0)
        {
            s.Stock.TryTake("wood", taken);
            s.Ledger.Record("wood", -taken, LedgerKeys.Heating);
        }
        // Owed wood (less than a unit) carries over only when the day was paid in full.
        s.HeatingOwed = taken == whole ? want - whole : 0;
        int heated = taken == whole ? homes.Count : (int)Math.Floor(taken / woodPerHome);
        for (int i = 0; i < homes.Count; i++) homes[i].Cold = i >= heated;
    }

    /// <summary>Whether homes need heating today.</summary>
    public static bool HeatingSeason(GameWorld world) => Current(world).HeatingWoodPerHomeDay > 0;
}
