using Sim.Core.Commands;
using Sim.Core.Data;
using Sim.Core.Settlements;
using Sim.Core.Simulation;
using Sim.Core.Systems;
using Sim.Core.World;

namespace Sim.Core.AI;

/// <summary>Finds good spots for new buildings around a settlement.</summary>
public static class SiteFinder
{
    public static (int X, int Y)? Find(GameWorld world, Settlement settlement, BuildingDef def)
    {
        bool preferFertile = def.FertileBonus > 0;
        (int, int)? best = null;
        double bestScore = double.MinValue;
        int validFound = 0;

        for (int r = 0; r <= Placement.SettlementRadius; r++)
        {
            foreach (var (x, y) in Ring(settlement.CenterX, settlement.CenterY, r))
            {
                int left = x - def.Width / 2, top = y - def.Height / 2;
                if (Placement.Check(world, settlement, def, left, top) != null) continue;
                if (!HasClearBorder(world, left, top, def)) continue;

                if (!preferFertile) return (left, top);

                double score = FertileFraction(world, left, top, def) * 10 - r * 0.15;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = (left, top);
                }
                if (++validFound >= 60) return best;
            }
        }
        return best;
    }

    /// <summary>Keep a one-tile gap around buildings so the town has lanes between them.</summary>
    private static bool HasClearBorder(GameWorld world, int left, int top, BuildingDef def)
    {
        for (int y = top - 1; y <= top + def.Height; y++)
        for (int x = left - 1; x <= left + def.Width; x++)
        {
            if (world.Map.GetTile(x, y).BuildingId != 0) return false;
        }
        return true;
    }

    private static double FertileFraction(GameWorld world, int left, int top, BuildingDef def)
    {
        int fertile = 0;
        for (int y = top; y < top + def.Height; y++)
        for (int x = left; x < left + def.Width; x++)
        {
            if (world.Map.GetTile(x, y).Terrain == Terrain.Fertile) fertile++;
        }
        return fertile / (double)(def.Width * def.Height);
    }

    /// <summary>Points on the square ring at Chebyshev distance r, in a fixed order.</summary>
    private static IEnumerable<(int, int)> Ring(int cx, int cy, int r)
    {
        if (r == 0)
        {
            yield return (cx, cy);
            yield break;
        }
        for (int x = cx - r; x <= cx + r; x++) yield return (x, cy - r);
        for (int y = cy - r + 1; y <= cy + r; y++) yield return (cx + r, y);
        for (int x = cx + r - 1; x >= cx - r; x--) yield return (x, cy + r);
        for (int y = cy + r - 1; y > cy - r; y--) yield return (cx - r, y);
    }
}

/// <summary>
/// Simple rule-based planner that grows a settlement when its rules allow auto-building.
/// This is the seed of the NPC faction AI; it issues the same commands a player would.
/// </summary>
public static class SettlementGovernor
{
    public const int MaxActiveConstructionSites = 2;

    public static void Daily(GameWorld world, Settlement settlement)
    {
        if (!settlement.Rules.AutoBuild) return;
        if (settlement.Buildings.Count(b => b.IsConstructionSite) >= MaxActiveConstructionSites) return;

        string? wanted = ChooseNextBuilding(world, settlement);
        if (wanted == null) return;

        var def = world.Data.Building(wanted);
        var site = SiteFinder.Find(world, settlement, def);
        if (site is not var (x, y)) return;

        world.Execute(new PlaceBuildingCommand(settlement.Id, wanted, x, y));
    }

    public static string? ChooseNextBuilding(GameWorld world, Settlement s)
    {
        int population = s.Citizens.Count;
        int plannedHousing = s.Buildings.Sum(b => b.Def.Housing) - population;
        double foodDays = Needs.FoodDays(world, s);
        int farms = s.CountIncludingPlanned("farm");
        int bakeries = s.CountIncludingPlanned("bakery");

        // Don't build more workplaces of a kind while existing ones are short of workers.
        bool Understaffed(string defId) => s.Buildings.Any(b =>
            b.Def.Id == defId && (b.IsConstructionSite || b.WorkerIds.Count < s.WantedWorkers(b)));

        var wishes = new List<string>();
        if (plannedHousing < 3) wishes.Add("hovel");
        // Each farm (two farmers) reliably covers about 6 nutrition a day.
        double dailyFoodNeed = s.Citizens.Sum(Needs.DailyFoodNeed);
        bool grainSurplus = s.Stock.Get("grain") >= s.LimitOf("grain") * 0.8;
        if ((foodDays < 3 && !grainSurplus) || farms * 6.0 < dailyFoodNeed)
        {
            if (!Understaffed("farm")) wishes.Add("farm");
        }
        if ((bakeries == 0 && population >= 18) || (grainSurplus && bakeries < farms * 0.6 && !Understaffed("bakery")))
            wishes.Add("bakery");
        if (s.Stock.Get("wood") < 40 && s.CountIncludingPlanned("woodcutter") < 1 + population / 15)
            wishes.Add("woodcutter");
        int quarries = s.CountIncludingPlanned("quarry");
        if (quarries == 0 || (s.Stock.Get("stone") < 20 && quarries < 1 + population / 30))
            wishes.Add("quarry");
        if (s.FreeStorage < 80) wishes.Add("stockpile");

        // Build the first wish we can afford; cheap essentials (woodcutter) come before anything else
        // when wood is the blocker.
        foreach (string id in wishes)
        {
            if (s.Stock.Has(world.Data.Building(id).Cost)) return id;
        }
        if (s.CountIncludingPlanned("woodcutter") < 2 && s.Stock.Has(world.Data.Building("woodcutter").Cost) &&
            wishes.Count > 0)
            return "woodcutter";
        return null;
    }
}
