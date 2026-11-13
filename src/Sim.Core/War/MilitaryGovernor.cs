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

    /// <summary>
    /// Under siege, a governed town holds out behind its walls and lets its archers work. It rides out only when it
    /// clearly outnumbers the band and the engines are up, and pays the band off only as a last resort: once the wall
    /// under attack is badly broken, or the siege has dragged on, and only if the price is cheap.
    /// </summary>
    private static void Besieged(GameWorld world, Settlement s)
    {
        if (Siege.BandAt(world, s) is not { } band || !band.Engines.Any(e => e.Built)) return;
        if (Armies.Soldiers(s) >= band.Size * SallyOdds && band.LastSallyDay != world.Time.Day)
        {
            world.Execute(new SallyCommand(s.Id));
            return;
        }
        if (Siege.Tribute(world, band) > s.Stock.Get(Economy.Inventory.Coin) * TributeShare) return; // too dear: hold out

        // A long siege pinches the town: pay to end it. Otherwise pay only if the walls are about to fall and the
        // fight at the breach looks risky (everyone called up still wouldn't clearly outnumber the band).
        bool longSiege = band.SiegeDays >= HoldOutDays;
        bool wallFailing = band.HasTarget && s.Walls.TryGetValue((band.TargetX, band.TargetY), out var wall) &&
                           wall.Hp < Commands.Walls.PieceHitPoints(world, wall.Tier, wall.Piece) * WallFailingShare;
        int adults = s.Citizens.Count(c => c.IsAdult);
        int callUp = Math.Max(Armies.Soldiers(s), (int)(adults * Math.Min(0.8, s.Rules.MusterPercent * 2 / 100.0)));
        bool risky = callUp < band.Size * SafeAssaultOdds;
        if (longSiege || (wallFailing || BreachImminent(world, s, band)) && risky)
            world.Execute(new PayTributeCommand(s.Id));
    }

    /// <summary>Whether the band's finished engines would knock a wall tile down within a day.</summary>
    private static bool BreachImminent(GameWorld world, Settlement s, Warband band)
    {
        var rules = world.Data.War.Siege;
        var engines = band.Engines.Where(e => e.Built).Select(e => rules.Engine(e.Kind)).OfType<EngineDef>().ToList();
        if (engines.Count == 0) return false;
        foreach (var pos in Siege.OuterWalls(world, s))
        {
            var wall = s.Walls[pos];
            if (engines.Sum(e => Siege.Rate(e, wall)) >= wall.Hp) return true;
        }
        return false;
    }

    /// <summary>Pays tribute when it costs at most this share of the treasury.</summary>
    public const double TributeShare = 0.3;

    /// <summary>Sallies when its soldiers outnumber the band this many times.</summary>
    public const double SallyOdds = 1.25;

    /// <summary>Considers paying only once the wall under attack is below this share of its strength...</summary>
    public const double WallFailingShare = 0.35;

    /// <summary>...or the siege has lasted this many days.</summary>
    public const int HoldOutDays = 15;

    /// <summary>A fight at the breach is safe enough when everyone called up outnumbers the band this many times.</summary>
    public const double SafeAssaultOdds = 1.5;

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
