using Sim.Core.Commands;
using Sim.Core.Diplomacy;
using Sim.Core.Economy;
using Sim.Core.Events;
using Sim.Core.Settlements;
using Sim.Core.Simulation;
using Sim.Core.Systems;
using Sim.Core.Time;

namespace Sim.Core.War;

/// <summary>War between realms (factions.json "war").</summary>
public sealed class FactionWarRules
{
    /// <summary>Least game days between a declaration and the fighting (more on slow worlds, see <see cref="WorldSettings.MinWarWarningMinutes"/>).</summary>
    public double WarningDays { get; set; } = 2;

    /// <summary>A realm can't be attacked for this many game days after it was founded.</summary>
    public int ProtectionDays { get; set; } = 90;

    /// <summary>Peace brings a non-aggression pact for this many days.</summary>
    public int PeaceDays { get; set; } = 60;

    public double DeclareOpinion { get; set; } = -40;
    public double WarOpinionPerDay { get; set; } = -0.2;

    /// <summary>An NPC realm considers war on a realm it thinks this little of...</summary>
    public double NpcWarOpinion { get; set; } = -50;

    /// <summary>...that it outweighs this many times.</summary>
    public double NpcWarStrength { get; set; } = 1.5;

    /// <summary>An NPC realm seeks peace after this many days of war.</summary>
    public int NpcPeaceAfterDays { get; set; } = 90;

    /// <summary>Least soldiers an NPC realm marches with, and squads it keeps at home (when it has three or more).</summary>
    public int NpcArmyMin { get; set; } = 20;
    public int NpcSquadsKept { get; set; } = 1;
}

/// <summary>A war between two realms.</summary>
public sealed class WarState
{
    public int Id { get; set; }
    public int Attacker { get; set; }
    public int Defender { get; set; }
    public long DeclaredDay { get; set; }

    /// <summary>When the fighting may begin (the warning period).</summary>
    public long StartsTick { get; set; }
}

/// <summary>
/// War between realms (Phase 5, part 3). A realm declares war on another (not one it has a non-aggression pact,
/// alliance or vassalage with, nor one still under its founding protection); after a warning of at least
/// <see cref="FactionWarRules.WarningDays"/> game days and <see cref="WorldSettings.MinWarWarningMinutes"/> real
/// minutes, its towns may march their squads on the enemy's. An army is a <see cref="Warband"/> owned by a town: it
/// marches like a warband, sets off the enemy's watch and muster, lays siege with engines, and fights with its real
/// soldiers: the fallen are the home town's people. Win the assault and it carries loot home; lose, give up or be
/// paid off and the survivors march home. Paying an army off, or agreeing a peace, ends the war with a time-limited
/// non-aggression pact. Allies send relief armies to break sieges. NPC realms declare, march and make peace by
/// their temperament and strength.
/// </summary>
public static class FactionWar
{
    // ---------- wars ----------

    public static WarState? WarBetween(GameWorld world, int a, int b)
    {
        foreach (var w in world.Diplomacy.Wars)
            if (w.Attacker == a && w.Defender == b || w.Attacker == b && w.Defender == a) return w;
        return null;
    }

    /// <summary>At war, and past the warning period.</summary>
    public static bool Fighting(GameWorld world, int a, int b) =>
        WarBetween(world, a, b) is { } w && world.Time.Tick >= w.StartsTick;

    public static long WarningTicks(GameWorld world)
    {
        double days = world.Data.Factions.War.WarningDays;
        if (world.Settings.SecondsPerGameDay > 0)
            days = Math.Max(days, world.Settings.MinWarWarningMinutes * 60 / world.Settings.SecondsPerGameDay);
        return TimeScale.DaysToTicks(days);
    }

    /// <summary>Why a realm can't declare war on another now, or null.</summary>
    public static string? CheckWar(GameWorld world, int from, int to)
    {
        var target = world.Diplomacy.Find(to);
        if (from == to || target == null || Statecraft.Capital(world, to) == null) return "No such realm";
        if (WarBetween(world, from, to) != null) return "You are already at war";
        if (Statecraft.Has(world, TreatyKind.NonAggression, from, to)) return "You have a non-aggression pact: break it first";
        if (Statecraft.Allied(world, from, to)) return "You are allies: break the alliance first";
        if (Statecraft.Has(world, TreatyKind.Vassalage, from, to)) return "You are bound by vassalage: end it first";
        int protection = world.Data.Factions.War.ProtectionDays;
        if (world.Time.Day < target.FoundedDay + protection)
            return $"{target.Name} is newly founded and can't be attacked for {target.FoundedDay + protection - world.Time.Day} more days";
        return null;
    }

    public static WarState Declare(GameWorld world, int from, int to)
    {
        var war = new WarState
        {
            Id = world.NextId(), Attacker = from, Defender = to, DeclaredDay = world.Time.Day,
            StartsTick = world.Time.Tick + WarningTicks(world),
        };
        world.Diplomacy.Wars.Add(war);
        world.Diplomacy.Proposals.RemoveAll(p => p.FromFaction == from && p.ToFaction == to || p.FromFaction == to && p.ToFaction == from);
        Statecraft.ChangeOpinion(world, from, to, world.Data.Factions.War.DeclareOpinion);
        double days = (war.StartsTick - world.Time.Tick) / (double)TimeScale.TicksPerDay;
        string message = $"WAR: {world.Diplomacy.Find(from)?.Name} declared war on {world.Diplomacy.Find(to)?.Name}. " +
                         $"The fighting may begin in {days:0.#} days";
        foreach (var town in world.Settlements)
            if (town.FactionId == from || town.FactionId == to) world.Log(EventKind.Battle, town, message);
        return war;
    }

    /// <summary>Ends a war in peace: a time-limited non-aggression pact, and both sides' armies go home.</summary>
    public static void MakePeace(GameWorld world, WarState war, string how)
    {
        world.Diplomacy.Wars.Remove(war);
        world.Diplomacy.Proposals.RemoveAll(p => p.FromFaction == war.Attacker && p.ToFaction == war.Defender ||
                                                 p.FromFaction == war.Defender && p.ToFaction == war.Attacker);
        var rules = world.Data.Factions.War;
        world.Diplomacy.Treaties.Add(new Treaty
        {
            Id = world.NextId(), Kind = TreatyKind.NonAggression, A = war.Attacker, B = war.Defender,
            StartDay = world.Time.Day, EndsDay = world.Time.Day + rules.PeaceDays,
        });
        foreach (var band in world.War.Warbands.ToList())
            if (band.OwnerSettlementId != 0 && !band.Returning && world.FindSettlement(band.TargetId) is { } target &&
                world.FindSettlement(band.OwnerSettlementId) is { } home &&
                (home.FactionId == war.Attacker && target.FactionId == war.Defender || home.FactionId == war.Defender && target.FactionId == war.Attacker))
                Disperse(world, band);
        string message = $"PEACE: {world.Diplomacy.Find(war.Attacker)?.Name} and {world.Diplomacy.Find(war.Defender)?.Name} made peace ({how}); " +
                         $"no war between them for {rules.PeaceDays} days";
        foreach (var town in world.Settlements)
            if (town.FactionId == war.Attacker || town.FactionId == war.Defender) world.Log(EventKind.Diplomacy, town, message);
    }

    // ---------- armies ----------

    public static bool IsArmy(Warband band) => band.OwnerSettlementId != 0;

    /// <summary>Squads at home (not marching) that may march: regular squads, not levies.</summary>
    public static List<Squad> HomeSquads(Settlement s) => s.Squads.Where(q => !q.Levy && q.ArmyId == null && q.SoldierIds.Count > 0).ToList();

    /// <summary>Counts an army's soldiers into its raider (melee) and archer (ranged) numbers.</summary>
    public static void Sync(GameWorld world, Warband army)
    {
        var home = world.FindSettlement(army.OwnerSettlementId);
        int melee = 0, ranged = 0;
        if (home != null)
            foreach (var q in home.Squads)
                if (army.SquadIds.Contains(q.Id))
                {
                    if (world.Data.War.Unit(q.UnitId)?.Ranged == true) ranged += q.SoldierIds.Count;
                    else melee += q.SoldierIds.Count;
                }
        army.Raiders = melee;
        army.Archers = ranged;
    }

    public static Warband Raise(GameWorld world, Settlement home, Settlement target, List<Squad> squads, bool relief = false)
    {
        var faction = Factions.Of(world, home);
        var army = new Warband
        {
            Id = world.NextId(), TargetId = target.Id, X = home.CenterX, Y = home.CenterY,
            OwnerSettlementId = home.Id, Relief = relief, Banner = faction?.Name ?? home.Name,
        };
        foreach (var q in squads)
        {
            q.ArmyId = army.Id;
            army.SquadIds.Add(q.Id);
        }
        Sync(world, army);
        world.War.Warbands.Add(army);
        string message = relief
            ? $"{army.Banner} sent {army.Size} soldiers from {home.Name} to relieve {target.Name}"
            : $"{army.Banner} marched on {target.Name} with {army.Size} soldiers from {home.Name}";
        world.Log(EventKind.Battle, home, message);
        return army;
    }

    /// <summary>The army's own squads as battle blocks: real units, experience and people.</summary>
    public static List<Block> Blocks(GameWorld world, Warband army)
    {
        var home = world.FindSettlement(army.OwnerSettlementId);
        if (home == null) return new();
        return home.Squads.Where(q => army.SquadIds.Contains(q.Id) && q.SoldierIds.Count > 0).Select(q => new Block
        {
            Unit = world.Data.War.Unit(q.UnitId) ?? world.Data.War.Units[0], Count = q.SoldierIds.Count, Xp = q.Xp, Citizens = q.SoldierIds.ToList(),
        }).ToList();
    }

    /// <summary>An army's fallen are its home town's people; survivors learn. Bandits just lose numbers.</summary>
    public static void TakeAttackerLosses(GameWorld world, Warband band, BattleResult result, string where)
    {
        if (!IsArmy(band))
        {
            Raids.TakeLosses(band, result.AttackerLosses);
            return;
        }
        if (world.FindSettlement(band.OwnerSettlementId) is { } home)
        {
            foreach (int id in result.AttackersFallen)
                if (home.FindCitizen(id) is { } c) LifeSystem.Kill(world, home, c, $"fell in battle at {where}");
            foreach (var q in home.Squads)
                if (band.SquadIds.Contains(q.Id)) q.Xp += world.Data.War.Battle.SquadXpPerBattle;
        }
        Sync(world, band);
    }

    /// <summary>A band's fighting is over: bandits vanish, an army turns for home with its survivors.</summary>
    public static void Disperse(GameWorld world, Warband band)
    {
        if (!world.War.Warbands.Contains(band) || band.Returning && band.TargetId == band.OwnerSettlementId && band.Size > 0) return;
        if (!IsArmy(band) || band.Size == 0 || world.FindSettlement(band.OwnerSettlementId) == null)
        {
            if (IsArmy(band)) Home(world, band);
            else world.War.Warbands.Remove(band);
            return;
        }
        band.Returning = true;
        band.Relief = false;
        band.Siege = false;
        band.SiegeDays = 0;
        band.Engines.Clear();
        band.HasTarget = false;
        band.TargetId = band.OwnerSettlementId;
    }

    /// <summary>The army is home: its squads rejoin the garrison (empty ones are struck off).</summary>
    public static void Home(GameWorld world, Warband army)
    {
        world.War.Warbands.Remove(army);
        if (world.FindSettlement(army.OwnerSettlementId) is not { } home) return;
        foreach (var q in home.Squads.ToList())
        {
            if (!army.SquadIds.Contains(q.Id)) continue;
            q.ArmyId = null;
            if (q.SoldierIds.Count == 0) home.Squads.Remove(q);
        }
        int loot = army.Loot.Values.Sum();
        foreach (var (r, n) in army.Loot)
        {
            home.Stock.Add(r, n);
            home.Ledger.Record(r, n, LedgerKeys.Loot);
        }
        world.Log(EventKind.Battle, home, $"The army came home to {home.Name} with {army.Size} soldiers" +
                                          (loot > 0 ? $" and loot: {Trade.Trading.Describe(army.Loot)}" : ""));
    }

    /// <summary>Armies on the way home, and relief armies arriving at an ally under siege.</summary>
    public static void Arrivals(GameWorld world)
    {
        foreach (var band in world.War.Warbands.ToList())
        {
            if (!IsArmy(band) || !world.War.Warbands.Contains(band)) continue;
            var target = world.FindSettlement(band.TargetId);
            if (target == null)
            {
                Disperse(world, band);
                continue;
            }
            if (Raids.Distance(target, band) > Raids.ArriveRadius(target)) continue;
            if (band.Returning) Home(world, band);
            else if (band.Relief) Relieve(world, target, band);
        }
    }

    /// <summary>A relief army meets the besiegers in the open; either way it marches home afterwards.</summary>
    private static void Relieve(GameWorld world, Settlement town, Warband relief)
    {
        var besiegers = Siege.BandAt(world, town);
        if (besiegers == null)
        {
            Disperse(world, relief);
            return;
        }
        var result = BattleResolver.Resolve(world, Raids.AttackerBlocks(world, besiegers), Blocks(world, relief), default);
        TakeAttackerLosses(world, besiegers, result, town.Name);
        if (world.FindSettlement(relief.OwnerSettlementId) is { } home)
            foreach (int id in result.Fallen)
                if (home.FindCitizen(id) is { } c) LifeSystem.Kill(world, home, c, $"fell relieving {town.Name}");
        Sync(world, relief);
        string summary = result.DefendersWon
            ? $"{relief.Banner}'s relief army broke the siege of {town.Name}: {result.AttackerLosses} besiegers fell, {result.DefenderLosses} of theirs"
            : $"{relief.Banner}'s relief army was beaten before {town.Name} ({result.DefenderLosses} fell); the siege goes on";
        world.Log(EventKind.Battle, town, summary);
        if (world.FindSettlement(relief.OwnerSettlementId) is { } h && h != town) world.Log(EventKind.Battle, h, summary);
        Raids.Report(world, town, result, summary);
        if (result.DefendersWon) Disperse(world, besiegers);
        Disperse(world, relief);
    }

    /// <summary>An army that won its assault carries off the loot it took, and its realm offers no more war: peace.</summary>
    public static void Victory(GameWorld world, Settlement town, Warband army, Dictionary<string, int> loot)
    {
        foreach (var (r, n) in loot) army.Loot[r] = army.Loot.GetValueOrDefault(r) + n;
        if (world.FindSettlement(army.OwnerSettlementId) is { } home && WarBetween(world, home.FactionId, town.FactionId) is { } war)
            MakePeace(world, war, $"{army.Banner} sacked {town.Name}");
    }

    // ---------- daily: wars wind down, armies arrive, NPC realms decide ----------

    public static void Daily(GameWorld world)
    {
        var rules = world.Data.Factions.War;
        foreach (var war in world.Diplomacy.Wars.ToList())
        {
            if (Statecraft.Capital(world, war.Attacker) == null || Statecraft.Capital(world, war.Defender) == null)
            {
                world.Diplomacy.Wars.Remove(war);
                continue;
            }
            Statecraft.ChangeOpinion(world, war.Attacker, war.Defender, rules.WarOpinionPerDay);
        }
        foreach (var f in world.Diplomacy.Factions.ToList())
            if (f.IsNpc && Statecraft.Capital(world, f.Id) != null) NpcWar.Daily(world, f);
    }
}

/// <summary>How NPC realms wage war: declare on the weak they dislike, march, relieve allies, and seek peace.</summary>
public static class NpcWar
{
    public static void Daily(GameWorld world, Faction me)
    {
        var rules = world.Data.Factions.War;
        var temper = world.Data.Factions.Personality(me.Personality);
        var towns = world.Settlements.Where(s => s.FactionId == me.Id).ToList();
        bool armyOut = world.War.Warbands.Any(b => b.OwnerSettlementId != 0 && towns.Any(t => t.Id == b.OwnerSettlementId));

        foreach (var war in world.Diplomacy.Wars.Where(w => w.Attacker == me.Id || w.Defender == me.Id).ToList())
        {
            int enemy = war.Attacker == me.Id ? war.Defender : war.Attacker;
            long age = world.Time.Day - war.DeclaredDay;
            // Long wars, or losing ones, end in peace offers.
            double ratio = Statecraft.Strength(world, me.Id) / Math.Max(1, Statecraft.Strength(world, enemy));
            if ((age >= rules.NpcPeaceAfterDays || ratio < 0.7 && age >= 20) &&
                Statecraft.CheckProposal(world, me.Id, enemy, ProposalKind.Peace, null) == null)
                Statecraft.Propose(world, me.Id, enemy, ProposalKind.Peace);
            // March on the enemy's nearest town with what can be spared.
            if (armyOut || !FactionWar.Fighting(world, me.Id, enemy)) continue;
            foreach (var home in towns)
            {
                // Keep a squad at home if there are a few; the muster defends the rest of the time.
                var all = FactionWar.HomeSquads(home);
                var squads = all.Skip(all.Count >= 3 ? rules.NpcSquadsKept : 0).ToList();
                if (squads.Sum(q => q.SoldierIds.Count) < rules.NpcArmyMin) continue;
                var target = world.Settlements.Where(s => s.FactionId == enemy)
                    .OrderBy(s => Math.Max(Math.Abs(s.CenterX - home.CenterX), Math.Abs(s.CenterY - home.CenterY))).ThenBy(s => s.Id).FirstOrDefault();
                if (target == null) continue;
                FactionWar.Raise(world, home, target, squads);
                armyOut = true;
                break;
            }
        }

        // Allies under siege get a relief army.
        if (!armyOut)
            foreach (var town in world.Settlements)
            {
                if (town.FactionId == me.Id || !Statecraft.Allied(world, me.Id, town.FactionId) || Siege.BandAt(world, town) is not { } besiegers) continue;
                if (besiegers.OwnerSettlementId != 0 && world.FindSettlement(besiegers.OwnerSettlementId)?.FactionId == me.Id) continue;
                var home = towns.OrderBy(t => Math.Max(Math.Abs(t.CenterX - town.CenterX), Math.Abs(t.CenterY - town.CenterY))).FirstOrDefault();
                if (home == null) break;
                var all = FactionWar.HomeSquads(home);
                var squads = all.Skip(all.Count >= 3 ? rules.NpcSquadsKept : 0).ToList();
                if (squads.Sum(q => q.SoldierIds.Count) < rules.NpcArmyMin) continue;
                FactionWar.Raise(world, home, town, squads, relief: true);
                armyOut = true;
                break;
            }

        // Now and then, the warlike pick a fight with a weak realm they despise.
        if (world.Diplomacy.Wars.Any(w => w.Attacker == me.Id || w.Defender == me.Id)) return;
        if (!world.Rng.Chance(temper.Aggression / Math.Max(1, world.Data.Factions.Diplomacy.NpcActEveryDays))) return;
        double mine = Statecraft.Strength(world, me.Id);
        var victim = world.Diplomacy.Factions
            .Where(f => f.Id != me.Id && Statecraft.Capital(world, f.Id) != null &&
                        Statecraft.Opinion(world, me.Id, f.Id) <= rules.NpcWarOpinion &&
                        mine >= Statecraft.Strength(world, f.Id) * rules.NpcWarStrength &&
                        FactionWar.CheckWar(world, me.Id, f.Id) == null)
            .OrderBy(f => Statecraft.Opinion(world, me.Id, f.Id)).ThenBy(f => f.Id).FirstOrDefault();
        if (victim != null) FactionWar.Declare(world, me.Id, victim.Id);
    }

    /// <summary>Whether an NPC realm accepts peace: after a long war, when losing, or if it doesn't hate them.</summary>
    public static bool WouldMakePeace(GameWorld world, Faction me, int enemy)
    {
        if (FactionWar.WarBetween(world, me.Id, enemy) is not { } war) return false;
        long age = world.Time.Day - war.DeclaredDay;
        double ratio = Statecraft.Strength(world, me.Id) / Math.Max(1, Statecraft.Strength(world, enemy));
        // Long wars wear everyone out; a realm clearly losing takes peace sooner.
        return age >= world.Data.Factions.War.NpcPeaceAfterDays || ratio < 0.7 && age >= 10;
    }
}

// ---------- commands ----------

/// <summary>Declares war on another realm; the fighting begins after the warning period.</summary>
public sealed record DeclareWarCommand(int SettlementId, int TargetFactionId) : ISettlementCommand
{
    public CommandResult Execute(GameWorld world)
    {
        var s = world.FindSettlement(SettlementId);
        if (s == null) return CommandResult.Fail("Unknown settlement");
        string? error = FactionWar.CheckWar(world, s.FactionId, TargetFactionId);
        if (error != null) return CommandResult.Fail(error);
        return CommandResult.Success(FactionWar.Declare(world, s.FactionId, TargetFactionId).Id);
    }
}

/// <summary>Marches the town's regular squads at home (or the ones listed) on an enemy town.</summary>
public sealed record MarchCommand(int SettlementId, int TargetSettlementId, IReadOnlyList<int>? SquadIds = null) : ISettlementCommand
{
    public CommandResult Execute(GameWorld world)
    {
        var s = world.FindSettlement(SettlementId);
        var target = world.FindSettlement(TargetSettlementId);
        if (s == null || target == null) return CommandResult.Fail("Unknown settlement");
        if (target.FactionId == s.FactionId) return CommandResult.Fail("That's one of your own towns");
        if (FactionWar.WarBetween(world, s.FactionId, target.FactionId) is not { } war) return CommandResult.Fail("You are not at war with them");
        if (world.Time.Tick < war.StartsTick)
            return CommandResult.Fail($"The fighting can't begin for {(war.StartsTick - world.Time.Tick) / (double)TimeScale.TicksPerDay:0.#} more days");
        var squads = FactionWar.HomeSquads(s).Where(q => SquadIds == null || SquadIds.Count == 0 || SquadIds.Contains(q.Id)).ToList();
        if (squads.Count == 0) return CommandResult.Fail("No squads at home to march (levies can't leave)");
        return CommandResult.Success(FactionWar.Raise(world, s, target, squads).Id);
    }
}

/// <summary>Calls an army home.</summary>
public sealed record RecallArmyCommand(int SettlementId, int ArmyId) : ISettlementCommand
{
    public CommandResult Execute(GameWorld world)
    {
        var army = world.War.Warbands.FirstOrDefault(b => b.Id == ArmyId && b.OwnerSettlementId == SettlementId);
        if (army == null) return CommandResult.Fail("No such army");
        if (army.Returning) return CommandResult.Fail("Already on its way home");
        FactionWar.Disperse(world, army);
        return CommandResult.Success();
    }
}
