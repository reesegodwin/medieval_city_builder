using Sim.Core.Economy;
using Sim.Core.Events;
using Sim.Core.Settlements;
using Sim.Core.Simulation;
using Sim.Core.Systems;

namespace Sim.Core.War;

/// <summary>A kind of siege engine (units.json "siege.engines").</summary>
public sealed class EngineDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>Game days for the band to build one.</summary>
    public double BuildDays { get; set; } = 2;

    /// <summary>Wall hit points knocked off a day, before the multipliers below.</summary>
    public double Damage { get; set; } = 50;

    /// <summary>Multiplier against each wall tier (palisade, stone wall, large stone wall).</summary>
    public List<double> VsTier { get; set; } = new() { 1, 1, 1 };

    /// <summary>Extra multiplier against gates and gatehouses.</summary>
    public double VsGate { get; set; } = 1;

    /// <summary>How far from the wall (tiles) it works once built: a ram must touch it, a trebuchet out-ranges archers.</summary>
    public double Range { get; set; } = 1;

    /// <summary>How much shooting it takes before it's wrecked.</summary>
    public int Hp { get; set; } = 100;

    /// <summary>Share of arrow damage it takes (a ram under its hide roof takes less).</summary>
    public double ArrowFactor { get; set; } = 1;
}

/// <summary>Siege tuning (units.json "siege").</summary>
public sealed class SiegeRules
{
    /// <summary>Bands at least this big lay siege to a walled town instead of attacking at once.</summary>
    public int MinSize { get; set; } = 25;

    /// <summary>Days a band keeps up a siege before giving up.</summary>
    public int MaxDays { get; set; } = 30;

    /// <summary>Engines a band can work: 1 + size / this, at most <see cref="MaxEngines"/>.</summary>
    public int RaidersPerEngine { get; set; } = 20;
    public int MaxEngines { get; set; } = 4;

    /// <summary>Coin per raider to buy the band off.</summary>
    public double TributePerRaider { get; set; } = 6;

    /// <summary>A band that falls below this share of <see cref="MinSize"/> gives up.</summary>
    public double GiveUpShare { get; set; } = 0.5;

    /// <summary>Output of workplaces outside the walls while besieged.</summary>
    public double OutsideOutput { get; set; } = 0.5;

    public double SiegeMood { get; set; } = -3;

    /// <summary>Wall hit points restored a day per damaged tile once the siege is over (paid in materials).</summary>
    public int RepairHpPerDay { get; set; } = 40;

    /// <summary>How far archers and towers on the walls can shoot (tiles). Engines are built beyond it.</summary>
    public double ArcherRange { get; set; } = 8;

    /// <summary>Tiles beyond archer range at which the band builds its engines.</summary>
    public double BuildMargin { get; set; } = 3;

    /// <summary>Damage a day to engines in range: per archer on the walls, and per tower.</summary>
    public double ArrowDamagePerArcher { get; set; } = 1.5;
    public double ArrowDamagePerTower { get; set; } = 6;

    public List<EngineDef> Engines { get; set; } = new();

    public EngineDef? Engine(string id) => Engines.FirstOrDefault(e => e.Id == id);
}

/// <summary>An engine a besieging band is building or working.</summary>
public sealed class SiegeEngine
{
    public string Kind { get; set; } = "";

    /// <summary>0..1 while being built.</summary>
    public double Progress { get; set; }

    public bool Built => Progress >= 1;

    /// <summary>Strength left against arrows (set when it's finished and rolled up into range).</summary>
    public int Hp { get; set; }
}

/// <summary>
/// Sieges (Phase 4). A band big enough that reaches a town with a closed wall ring digs in instead of attacking.
/// Day by day it builds siege engines (each takes game days, so an offline lord has real time to react) and
/// batters the weakest stretch of wall it can find. When a wall tile falls, the breach opens the ring and the band
/// storms in: the usual battle, with the walls now helping less. A town can sally out to burn the engines or pay the
/// band to leave; if the band can't break in within <see cref="SiegeRules.MaxDays"/>, it gives up. While besieged,
/// work outside the walls runs at half output, trade stops and people worry. Walls are repaired afterwards.
/// </summary>
public static class Siege
{
    public static bool Besieged(GameWorld world, Settlement s) => BandAt(world, s) != null;

    /// <summary>The band besieging a town, if any.</summary>
    public static Warband? BandAt(GameWorld world, Settlement s)
    {
        foreach (var w in world.War.Warbands)
            if (w.Siege && w.TargetId == s.Id) return w;
        return null;
    }

    public static int MaxEngines(SiegeRules rules, Warband band) =>
        Math.Min(rules.MaxEngines, 1 + band.Size / Math.Max(1, rules.RaidersPerEngine));

    public static int Tribute(GameWorld world, Warband band) => (int)Math.Ceiling(band.Size * world.Data.War.Siege.TributePerRaider);

    /// <summary>Whether an arriving band lays siege (big enough, and the town is walled in) rather than attacking.</summary>
    public static bool WillBesiege(GameWorld world, Settlement s, Warband band) =>
        world.Data.War.Siege.Engines.Count > 0 && band.Size >= world.Data.War.Siege.MinSize && WallSystem.Enclose(world, s).RingTier >= 0;

    public static void Begin(GameWorld world, Settlement s, Warband band)
    {
        band.Siege = true;
        band.SiegeDays = 0;
        world.Log(EventKind.Battle, s, $"Siege: {Raids.Describe(s, band)} dug in before the walls and began building siege engines. " +
                                      "Sally out or pay them off, or hold out (K)");
    }

    /// <summary>How fast an engine wears down a wall tile (hit points a day).</summary>
    public static double Rate(EngineDef engine, WallSegment wall) =>
        engine.Damage * engine.VsTier[Math.Min(wall.Tier, engine.VsTier.Count - 1)] * (wall.Gate ? engine.VsGate : 1);

    /// <summary>
    /// Wall tiles worth battering: open ground outside on one side and walled-in ground on the other, so knocking
    /// one down opens the ring (a corner, say, wouldn't).
    /// </summary>
    public static List<(int X, int Y)> OuterWalls(GameWorld world, Settlement s)
    {
        var inside = WallSystem.Enclose(world, s).Inside;
        var outer = new List<(int X, int Y)>();
        foreach (var (pos, _) in s.Walls)
        {
            bool outsideNext = false, insideNext = false;
            foreach (var (dx, dy) in new[] { (0, -1), (1, 0), (0, 1), (-1, 0) })
            {
                var n = (pos.X + dx, pos.Y + dy);
                if (inside.Contains(n)) insideNext = true;
                else if (world.Map.GetTile(n.Item1, n.Item2) is { WallTier: 0, BuildingId: 0 }) outsideNext = true;
            }
            if (outsideNext && insideNext) outer.Add(pos);
        }
        return outer;
    }

    /// <summary>The engine kind to build next: the one that would bring down some outer wall tile fastest.</summary>
    public static EngineDef? BestEngine(GameWorld world, Settlement s, List<(int X, int Y)> outer)
    {
        EngineDef? best = null;
        double bestScore = 0;
        foreach (var engine in world.Data.War.Siege.Engines)
        {
            double score = 0;
            foreach (var pos in outer)
            {
                var wall = s.Walls[pos];
                score = Math.Max(score, Rate(engine, wall) / Math.Max(1, wall.Hp));
            }
            // Quicker to build counts too: damage per day of building and battering.
            score /= engine.BuildDays;
            if (score > bestScore) { bestScore = score; best = engine; }
        }
        return best;
    }

    /// <summary>A siege day for the band: build, batter, and storm a breach (or give up).</summary>
    public static void Daily(GameWorld world, Settlement s, Warband band)
    {
        var rules = world.Data.War.Siege;
        band.SiegeDays++;
        if (band.SiegeDays > rules.MaxDays || band.Size < rules.MinSize * rules.GiveUpShare)
        {
            End(world, s, band, "The raiders gave up the siege and marched away");
            return;
        }

        var outer = OuterWalls(world, s);
        if (outer.Count == 0 || WallSystem.Enclose(world, s).RingTier < 0)
        {
            Assault(world, s, band, "The raiders found a way in and stormed the town");
            return;
        }

        // 1. Build: the engine in progress, else start the best kind for these walls.
        var building = band.Engines.FirstOrDefault(e => !e.Built);
        if (building == null && band.Engines.Count < MaxEngines(rules, band) && BestEngine(world, s, outer) is { } next)
            band.Engines.Add(building = new SiegeEngine { Kind = next.Id });
        if (building != null && rules.Engine(building.Kind) is { } def)
        {
            // Built out of bowshot, then rolled up to where it works from.
            building.Progress = Math.Min(1, building.Progress + 1 / Math.Max(0.1, def.BuildDays));
            if (building.Built)
            {
                building.Hp = def.Hp;
                world.Log(EventKind.Battle, s, $"Siege, day {band.SiegeDays}: the raiders finished a {def.Name.ToLowerInvariant()} and " +
                    (InArcherRange(rules, def) ? "rolled it up to the walls" : "set it up beyond bowshot"));
            }
        }

        // 2. Archers and towers on the walls shoot at engines that have come within their range.
        ShootAtEngines(world, s, band);

        // 3. Batter the outer wall tile the built engines bring down soonest.
        var engines = band.Engines.Where(e => e.Built).Select(e => rules.Engine(e.Kind)).OfType<EngineDef>().ToList();
        if (engines.Count == 0) return;
        (int X, int Y)? target = null;
        double soonest = double.MaxValue;
        foreach (var pos in outer)
        {
            var wall = s.Walls[pos];
            double rate = engines.Sum(e => Rate(e, wall));
            double days = wall.Hp / Math.Max(0.001, rate);
            if (days < soonest) { soonest = days; target = pos; }
        }
        if (target is not { } t) return;
        band.TargetX = t.X;
        band.TargetY = t.Y;
        band.HasTarget = true;
        var hit = s.Walls[t];
        hit.Hp -= (int)Math.Ceiling(engines.Sum(e => Rate(e, hit)));
        if (hit.Hp > 0) return;

        // 3. A breach: the tile is gone, the ring is open, and the band storms in.
        string piece = hit.Piece switch
        {
            WallPiece.Gate => "gate",
            WallPiece.Gatehouse => "gatehouse",
            WallPiece.Tower => "tower",
            _ => world.Data.WallTiers[hit.Tier].Name.ToLowerInvariant(),
        };
        Commands.Walls.Breach(world, s, t.X, t.Y);
        Assault(world, s, band, $"Breach! The raiders broke through the {piece} and stormed in");
    }

    /// <summary>Whether an engine, once working, stands within bowshot of the walls.</summary>
    public static bool InArcherRange(SiegeRules rules, EngineDef engine) => engine.Range <= rules.ArcherRange;

    /// <summary>Archers in squads (on the walls during a siege, up to the wall-walk's room) and the towers.</summary>
    public static double ArrowDamage(GameWorld world, Settlement s)
    {
        var rules = world.Data.War.Siege;
        int archers = s.Squads.Where(q => world.Data.War.Unit(q.UnitId)?.Ranged == true).Sum(q => q.SoldierIds.Count);
        int room = WallSystem.DefenderCapacity(world, s);
        return Math.Min(archers, room) * rules.ArrowDamagePerArcher + WallSystem.Towers(s).Count() * rules.ArrowDamagePerTower;
    }

    /// <summary>
    /// The defenders shoot at the nearest engine in range (only engines that have been finished and brought up within
    /// bowshot; those still being built, and trebuchets working from beyond range, are safe). Wrecked engines are gone.
    /// </summary>
    public static void ShootAtEngines(GameWorld world, Settlement s, Warband band)
    {
        var rules = world.Data.War.Siege;
        double damage = ArrowDamage(world, s);
        if (damage <= 0) return;
        var target = band.Engines
            .Where(e => e.Built && rules.Engine(e.Kind) is { } d && InArcherRange(rules, d))
            .OrderBy(e => rules.Engine(e.Kind)!.Range).FirstOrDefault();
        if (target == null) return;
        var def = rules.Engine(target.Kind)!;
        if (target.Hp <= 0) target.Hp = def.Hp; // finished before engines had strength (older saves)
        target.Hp -= (int)Math.Ceiling(damage * def.ArrowFactor);
        if (target.Hp > 0) return;
        band.Engines.Remove(target);
        world.Log(EventKind.Battle, s, $"Siege, day {band.SiegeDays}: our archers wrecked the raiders' {def.Name.ToLowerInvariant()}");
    }

    private static void Assault(GameWorld world, Settlement s, Warband band, string message)
    {
        world.Log(EventKind.Battle, s, message);
        Raids.Muster(world, s, Alert.CallToArms); // everyone eligible runs to the breach
        Raids.Fight(world, s, band);
    }

    private static void End(GameWorld world, Settlement s, Warband band, string message)
    {
        world.War.Warbands.Remove(band);
        world.Log(EventKind.Battle, s, message);
    }

    /// <summary>The town's soldiers fight the band outside the walls; a win burns its engines.</summary>
    public static BattleResult Sally(GameWorld world, Settlement s, Warband band)
    {
        band.LastSallyDay = world.Time.Day;
        var result = BattleResolver.Resolve(world, Raids.AttackerBlocks(world, band), Raids.DefenderBlocks(world, s), default, s.General);
        Raids.Bury(world, s, result, "fell in a sally");
        Raids.TakeLosses(band, result.AttackerLosses);
        string summary;
        if (result.DefendersWon)
        {
            int burned = band.Engines.Count;
            band.Engines.Clear();
            summary = $"Sally: our {result.DefendersStart} soldiers drove the raiders back ({result.AttackerLosses} fell) " +
                      $"and burned {burned} siege engine{(burned == 1 ? "" : "s")}; {result.DefenderLosses} of ours died";
        }
        else
            summary = $"Sally: our {result.DefendersStart} soldiers were thrown back ({result.DefenderLosses} died); " +
                      $"{result.AttackerLosses} raiders fell";
        world.Log(EventKind.Battle, s, summary);
        Raids.Report(world, s, result, summary);
        if (band.Size < world.Data.War.Siege.MinSize * world.Data.War.Siege.GiveUpShare)
            End(world, s, band, "Too few raiders were left to keep up the siege: they marched away");
        return result;
    }

    /// <summary>
    /// Once the siege is over, builders patch damaged walls a little each day, paying for it in the wall's own
    /// material in proportion to the strength restored.
    /// </summary>
    public static void Repair(GameWorld world, Settlement s)
    {
        if (s.Walls.Count == 0 || Besieged(world, s)) return;
        int perDay = world.Data.War.Siege.RepairHpPerDay;
        foreach (var (pos, wall) in s.Walls)
        {
            int max = Commands.Walls.PieceHitPoints(world, wall.Tier, wall.Piece);
            if (wall.Hp >= max) continue;
            int restore = Math.Min(perDay, max - wall.Hp);
            var cost = new Dictionary<string, int>();
            foreach (var (r, n) in Commands.Walls.PieceCost(world, wall.Tier, wall.Piece))
                cost[r] = Math.Max(1, (int)Math.Ceiling(n * (double)restore / max));
            if (!s.Stock.TryTakeAll(cost)) return; // out of materials: wait
            s.Ledger.RecordAll(cost, -1, LedgerKeys.Building);
            wall.Hp += restore;
        }
    }
}

/// <summary>The town's soldiers ride out against the besiegers (once a day).</summary>
public sealed record SallyCommand(int SettlementId) : Commands.ISettlementCommand
{
    public Commands.CommandResult Execute(GameWorld world)
    {
        var s = world.FindSettlement(SettlementId);
        if (s == null) return Commands.CommandResult.Fail("Unknown settlement");
        var band = Siege.BandAt(world, s);
        if (band == null) return Commands.CommandResult.Fail("The town isn't under siege");
        if (Armies.Soldiers(s) == 0) return Commands.CommandResult.Fail("No soldiers to sally with");
        if (band.LastSallyDay == world.Time.Day) return Commands.CommandResult.Fail("The soldiers already sallied today");
        Siege.Sally(world, s, band);
        return Commands.CommandResult.Success();
    }
}

/// <summary>Pays the besiegers to go away.</summary>
public sealed record PayTributeCommand(int SettlementId) : Commands.ISettlementCommand
{
    public Commands.CommandResult Execute(GameWorld world)
    {
        var s = world.FindSettlement(SettlementId);
        if (s == null) return Commands.CommandResult.Fail("Unknown settlement");
        var band = Siege.BandAt(world, s);
        if (band == null) return Commands.CommandResult.Fail("The town isn't under siege");
        int price = Siege.Tribute(world, band);
        if (!s.Stock.TryTake(Inventory.Coin, price)) return Commands.CommandResult.Fail($"The raiders want {price} coin");
        s.Ledger.Record(Inventory.Coin, -price, LedgerKeys.Tribute);
        world.War.Warbands.Remove(band);
        world.Log(EventKind.Battle, s, $"The town paid the raiders {price} coin, and they lifted the siege");
        return Commands.CommandResult.Success();
    }
}
