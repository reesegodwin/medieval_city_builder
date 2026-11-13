using Sim.Core.Settlements;
using Sim.Core.Simulation;

namespace Sim.Core.War;

/// <summary>
/// The governor's army: from <see cref="WarRules.GarrisonPopulation"/> people a governed town appoints a general and
/// keeps a small standing garrison (<see cref="WarRules.GarrisonShare"/> of its people) in its barracks, with the best
/// gear it has: men-at-arms, then archers and spearmen in turn, and only while it has spare hands. Militia for emergencies come from the muster.
/// </summary>
public static class MilitaryGovernor
{
    /// <summary>Squads wanted in the barracks.</summary>
    public static int WantedSquads(GameWorld world, Settlement s)
    {
        var war = world.Data.War;
        if (s.Citizens.Count < war.GarrisonPopulation) return 0;
        return Math.Max(1, (int)Math.Ceiling(s.Citizens.Count * war.GarrisonShare / war.SquadSize));
    }

    /// <summary>The most experienced adult (oldest under 50) who isn't serving or already the general.</summary>
    public static Citizens.Citizen? GeneralCandidate(Settlement s) =>
        s.Citizens.Where(c => c.IsAdult && c.SquadId == null && c.AgeYears < 50 && c.Id != s.General?.CitizenId)
            .OrderByDescending(c => c.AgeDays).ThenBy(c => c.Id).FirstOrDefault();

    /// <summary>Under siege, a governed town sallies if it clearly outnumbers the band, or pays it off if that's cheap.</summary>
    private static void Besieged(GameWorld world, Settlement s)
    {
        // Once the band starts on its engines: ride out if clearly stronger, else buy it off if that's cheap.
        if (Siege.BandAt(world, s) is not { } band || band.Engines.Count == 0) return;
        if (Armies.Soldiers(s) >= band.Size * SallyOdds && band.LastSallyDay != world.Time.Day)
            world.Execute(new SallyCommand(s.Id));
        else if (Siege.Tribute(world, band) <= s.Stock.Get(Economy.Inventory.Coin) * TributeShare)
            world.Execute(new PayTributeCommand(s.Id));
    }

    /// <summary>Pays tribute when it costs at most this share of the treasury.</summary>
    public const double TributeShare = 0.3;

    /// <summary>Sallies when its soldiers outnumber the band this many times.</summary>
    public const double SallyOdds = 1.25;

    public static void Daily(GameWorld world, Settlement s)
    {
        Besieged(world, s);
        var war = world.Data.War;
        if (s.Citizens.Count < war.GarrisonPopulation) return;

        if (s.General == null)
        {
            if (GeneralCandidate(s) is { } pick) world.Execute(new PromoteGeneralCommand(s.Id, pick.Id));
        }

        int regular = s.Squads.Count(q => !q.Levy);
        if (regular >= WantedSquads(world, s) || regular >= Armies.SquadCapacity(world, s) || s.Alert >= Alert.Muster) return;
        // Only from spare hands: a garrison that empties the taverns and breweries costs more than it saves.
        if (Armies.SpareHands(s) < war.SquadSize) return;
        int archers = s.Squads.Count(q => q.UnitId == "archers"), melee = regular - archers;
        bool Has(string unitId, int size) => war.Unit(unitId) is { } u && u.Gear.All(kv => s.Stock.Get(kv.Key) >= kv.Value * size);
        string? unit = Has("men_at_arms", war.SquadSize) ? "men_at_arms"
            : archers < melee && Has("archers", war.SquadSize) ? "archers"
            : Has("spearmen", war.SquadSize) ? "spearmen"
            : Has("archers", war.SquadSize) ? "archers" : null;
        if (unit != null) world.Execute(new RecruitSquadCommand(s.Id, unit));
    }
}
