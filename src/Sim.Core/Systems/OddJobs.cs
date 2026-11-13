using Sim.Core.Economy;
using Sim.Core.Settlements;
using Sim.Core.Simulation;
using Sim.Core.Time;

namespace Sim.Core.Systems;

/// <summary>
/// Adults who can work but have no job don't sit idle: they lend a hand at building sites, help lay planned
/// roads, and otherwise gather berries, firewood and stones (see oddjobs.json). Who does what is worked out from
/// the citizens and buildings as they are (in list order), so nothing extra is saved and every client agrees.
/// </summary>
public static class OddJobs
{
    /// <summary>How the town's odd-jobbers are shared out right now.</summary>
    public sealed class Crew
    {
        public int People { get; init; }
        public List<(Building Site, int Helpers)> Sites { get; } = new();
        public int Roads { get; set; }
        public int Gatherers { get; set; }
        public int AtSites => Sites.Sum(s => s.Helpers);
        public int HelpersAt(Building site) => Sites.FirstOrDefault(s => s.Site == site).Helpers;
    }

    /// <summary>
    /// Storage gatherers leave free (a fifth of it, between 60 and 500), so workplaces always have room for
    /// what they make. Filling the last space with stones once starved a whole town.
    /// </summary>
    public static int KeepFree(Settlement s) => Math.Clamp(s.StorageCapacity / 5, 60, 500);

    public static int RoomForGathering(Settlement s) => Math.Max(0, s.FreeStorage - KeepFree(s));

    public static int People(Settlement s) => s.Citizens.Count(c => c.CanWork && c.JobBuildingId == null);

    /// <summary>Building sites first (up to their free builder slots), then roads, then everyone else gathers.</summary>
    public static Crew Allocate(GameWorld world, Settlement s)
    {
        var rules = world.Data.OddJobs;
        int left = People(s);
        var crew = new Crew { People = left };
        if (left == 0) return crew;
        foreach (var b in s.Buildings)
        {
            if (left == 0) break;
            if (b.State != BuildingState.UnderConstruction) continue;
            int helpers = Math.Min(left, Math.Max(0, b.Def.BuilderSlots - b.WorkerIds.Count));
            if (helpers == 0) continue;
            crew.Sites.Add((b, helpers));
            left -= helpers;
        }
        if (s.RoadPlans.Count > 0 && s.TownCentre is { IsOperational: true })
        {
            int wanted = (s.RoadPlans.Count + rules.PlannedRoadsPerHelper - 1) / Math.Max(1, rules.PlannedRoadsPerHelper);
            crew.Roads = Math.Min(left, Math.Min(wanted, rules.MaxRoadHelpers));
            left -= crew.Roads;
        }
        crew.Gatherers = left;
        return crew;
    }

    /// <summary>Labour per tick that helpers add (half an average worker each, by default).</summary>
    public static double HelperLabourThisTick(GameWorld world, int helpers) =>
        helpers * world.Data.OddJobs.HelperRate / TimeScale.TicksPerDay;

    /// <summary>Goods gatherers can bring in today: in season and below the town's stock limit.</summary>
    public static List<Data.GatherDef> GatherTasks(GameWorld world, Settlement s) =>
        world.Data.OddJobs.Gather
            .Where(g => !(g.NotInWinter && world.Time.Season == Season.Winter))
            .Where(g => world.Data.Resources.ContainsKey(g.Resource) && s.Stock.Get(g.Resource) < s.LimitOf(g.Resource))
            .ToList();

    /// <summary>Whether someone without a job would find anything to do (for mood).</summary>
    public static bool Available(GameWorld world, Settlement s) =>
        s.Buildings.Any(b => b.State == BuildingState.UnderConstruction && b.WorkerIds.Count < b.Def.BuilderSlots) ||
        (s.RoadPlans.Count > 0 && s.TownCentre is { IsOperational: true }) ||
        (RoomForGathering(s) > 0 && GatherTasks(world, s).Count > 0);

    /// <summary>Daily: the gatherers' haul, dealt out in turn across the goods still wanted.</summary>
    public static void Daily(GameWorld world, Settlement s)
    {
        int gatherers = Allocate(world, s).Gatherers;
        var tasks = GatherTasks(world, s);
        if (gatherers == 0 || tasks.Count == 0) return;
        for (int i = 0; i < tasks.Count; i++)
        {
            var task = tasks[i];
            int people = gatherers / tasks.Count + (i < gatherers % tasks.Count ? 1 : 0);
            int amount = (int)Math.Floor(people * task.PerDay);
            amount = Math.Min(amount, Math.Min(s.LimitOf(task.Resource) - s.Stock.Get(task.Resource), RoomForGathering(s)));
            if (amount <= 0) continue;
            s.Stock.Add(task.Resource, amount);
            s.Ledger.Record(task.Resource, amount, LedgerKeys.Gathered);
        }
    }
}
