using Sim.Core.Economy;
using Sim.Core.Events;
using Sim.Core.Settlements;
using Sim.Core.Simulation;
using Sim.Core.Systems;

namespace Sim.Core.War;

/// <summary>Raid tuning (units.json "raids").</summary>
public sealed class RaidRules
{
    public int StartDay { get; set; } = 75;
    public double ChancePerDay { get; set; } = 0.011;
    public int MinPopulation { get; set; } = 40;
    public double SpeedTilesPerDay { get; set; } = 12;
    public int SpawnDistance { get; set; } = 70;
    public int BaseSize { get; set; } = 6;
    public double PerPopulation { get; set; } = 0.035;
    public double PerThousandCoin { get; set; } = 4;
    public int MaxSize { get; set; } = 60;
    public double ArcherShare { get; set; } = 0.3;
    public double LootCoinShare { get; set; } = 0.25;
    public double LootGoodsShare { get; set; } = 0.1;
    public int StandDownDays { get; set; } = 2;
}

/// <summary>A band of raiders on the map, marching on a town.</summary>
public sealed class Warband
{
    public int Id { get; set; }
    public int TargetId { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public int Raiders { get; set; }
    public int Archers { get; set; }
    public int Size => Raiders + Archers;

    /// <summary>Dug in before the town's walls (see <see cref="War.Siege"/>).</summary>
    public bool Siege { get; set; }
    public int SiegeDays { get; set; }
    public List<SiegeEngine> Engines { get; set; } = new();

    /// <summary>The wall tile being battered.</summary>
    public bool HasTarget { get; set; }
    public int TargetX { get; set; }
    public int TargetY { get; set; }

    /// <summary>Day the town last sallied against this band (one sally a day).</summary>
    public long LastSallyDay { get; set; } = -1;

    /// <summary>The town whose army this is (0: bandits). An army's soldiers are that town's squads.</summary>
    public int OwnerSettlementId { get; set; }
    public List<int> SquadIds { get; set; } = new();

    /// <summary>The realm's name, for describing the army.</summary>
    public string Banner { get; set; } = "";

    /// <summary>Marching home (its target is its own town).</summary>
    public bool Returning { get; set; }

    /// <summary>Marching to break the siege of an ally's town.</summary>
    public bool Relief { get; set; }

    /// <summary>What an army carries home from a sacked town.</summary>
    public SortedDictionary<string, int> Loot { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>A battle that was fought, for reports.</summary>
public sealed class BattleReport
{
    public long Day { get; set; }
    public int SettlementId { get; set; }
    public bool DefendersWon { get; set; }
    public int Attackers { get; set; }
    public int Defenders { get; set; }
    public int AttackerLosses { get; set; }
    public int DefenderLosses { get; set; }
    public string Summary { get; set; } = "";
}

/// <summary>Warbands and recent battles across the world (saved with it).</summary>
public sealed class WarBook
{
    public List<Warband> Warbands { get; set; } = new();
    public List<BattleReport> Reports { get; set; } = new();
}

/// <summary>How alarmed a town is (the offline call to arms).</summary>
public enum Alert : byte
{
    None = 0,

    /// <summary>A warband has been seen within the watch radius: a warning, no cost.</summary>
    Watch = 1,

    /// <summary>It's close: the garrison and a share of the townsfolk arm themselves (they stop working).</summary>
    Muster = 2,

    /// <summary>It's at the walls: everyone eligible is called up.</summary>
    CallToArms = 3,
}

/// <summary>
/// Raids and the offline call to arms. Every so often (rule <c>Raids</c>, from day 75) a warband sized to a town's
/// population and wealth appears beyond its land and marches on it. The town raises its alert as the band comes
/// near: Watch (a warning), Muster (garrison plus a share of townsfolk as militia), then Call to arms. When the band
/// reaches the town a battle is fought with the town's walls and towers; losing means plunder. Militia stand down
/// once the threat has gone. Deterministic: chance comes from the world's generator, settlements in list order.
/// </summary>
public static class Raids
{
    public const int MusterSquadSize = 10;

    /// <summary>How far out the town keeps watch: its land plus 30 tiles, plus 2 per tower (towers see far).</summary>
    public static double WatchRadius(Settlement s) => s.TerritoryExtent + 30 + 2 * WallSystem.Towers(s).Count();

    public static double MusterRadius(Settlement s) => s.TerritoryExtent + 15;

    /// <summary>Where the band reaches the town: the edge of its land.</summary>
    public static double ArriveRadius(Settlement s) => s.TerritoryExtent + 2;

    /// <summary>Whether a town knows of a band: its own watch sees it, or an ally's does (alliances share the watch).</summary>
    public static bool Sees(GameWorld world, Settlement s, Warband w)
    {
        if (Distance(s, w) <= WatchRadius(s)) return true;
        foreach (var t in world.Settlements)
            if (t != s && t.FactionId != 0 && Diplomacy.Statecraft.Allied(world, s.FactionId, t.FactionId) && Distance(t, w) <= WatchRadius(t))
                return true;
        return false;
    }

    public static double Distance(Settlement s, Warband w) => Math.Max(Math.Abs(w.X - s.CenterX), Math.Abs(w.Y - s.CenterY));

    public static void Daily(GameWorld world)
    {
        var book = world.War;
        var rules = world.Data.War.Raids;
        foreach (var s in world.Settlements)
        {
            if (!s.Rules.Raids || s.Citizens.Count < rules.MinPopulation || world.Time.Day < rules.StartDay) continue;
            if (book.Warbands.Any(w => w.TargetId == s.Id)) continue;
            // A vassal's overlord keeps bandits away, mostly.
            double chance = rules.ChancePerDay *
                (Diplomacy.Statecraft.OverlordOf(world, s.FactionId) != null ? world.Data.Factions.Diplomacy.VassalRaidFactor : 1);
            if (!world.Rng.Chance(chance)) continue;
            Spawn(world, s, rules);
        }

        foreach (var w in book.Warbands.ToList())
        {
            var s = world.FindSettlement(w.TargetId);
            if (s == null) { book.Warbands.Remove(w); continue; }
            // March straight on the town centre.
            double dx = s.CenterX - w.X, dy = s.CenterY - w.Y;
            double length = Math.Sqrt(dx * dx + dy * dy);
            double step = Math.Min(rules.SpeedTilesPerDay, Math.Max(0, length - ArriveRadius(s) + 1));
            if (length > 0) { w.X += dx / length * step; w.Y += dy / length * step; }
        }

        FactionWar.Arrivals(world);
        foreach (var s in world.Settlements)
        {
            UpdateAlert(world, s);
            Siege.Repair(world, s);
        }
    }

    /// <summary>Bands that threaten a town: marching on it to fight (not armies going home or coming to help).</summary>
    public static bool Threatens(Warband w, Settlement s) => w.TargetId == s.Id && !w.Returning && !w.Relief;

    private static void Spawn(GameWorld world, Settlement s, RaidRules rules)
    {
        int size = (int)Math.Round(rules.BaseSize + s.Citizens.Count * rules.PerPopulation + s.Stock.Get(Inventory.Coin) / 1000.0 * rules.PerThousandCoin);
        size = Math.Clamp(size, 1, rules.MaxSize);
        int archers = (int)Math.Round(size * rules.ArcherShare);
        // Somewhere on a square around the town, well beyond its land.
        double distance = s.TerritoryExtent + rules.SpawnDistance;
        int side = world.Rng.Next(4);
        double along = world.Rng.NextDouble() * 2 - 1;
        var (x, y) = side switch
        {
            0 => (along * distance, -distance),
            1 => (distance, along * distance),
            2 => (along * distance, distance),
            _ => (-distance, along * distance),
        };
        world.War.Warbands.Add(new Warband
        {
            Id = world.NextId(), TargetId = s.Id, X = s.CenterX + x, Y = s.CenterY + y, Raiders = size - archers, Archers = archers,
        });
    }

    /// <summary>What the town knows of a warband: the further away, the rougher the count (fog of war).</summary>
    public static string Describe(Settlement s, Warband w)
    {
        if (w.OwnerSettlementId != 0)
        {
            double d0 = Distance(s, w);
            int shown = d0 > MusterRadius(s) ? Math.Max(10, (int)Math.Round(w.Size / 10.0) * 10) : w.Size;
            return $"{w.Banner}'s army of {(d0 > MusterRadius(s) ? "about " : "")}{shown}";
        }
        double d = Distance(s, w);
        if (d > MusterRadius(s))
        {
            int rough = Math.Max(10, (int)Math.Round(w.Size / 10.0) * 10);
            return $"a warband of about {rough}";
        }
        if (d > ArriveRadius(s) + 3)
        {
            int Round5(int n) => Math.Max(5, (int)Math.Round(n / 5.0) * 5);
            return $"a warband of about {Round5(w.Raiders)} raiders and {Round5(w.Archers)} archers";
        }
        return $"a warband of {w.Raiders} raiders and {w.Archers} archers";
    }

    private static void UpdateAlert(GameWorld world, Settlement s)
    {
        var band = world.War.Warbands.Where(w => Threatens(w, s)).OrderBy(w => w.Id).FirstOrDefault();
        var level = Alert.None;
        if (band != null)
        {
            double d = Distance(s, band);
            level = d <= ArriveRadius(s) ? Alert.CallToArms : d <= MusterRadius(s) ? Alert.Muster : d <= WatchRadius(s) ? Alert.Watch : Alert.None;
        }

        if (level > s.Alert && band != null)
        {
            string what = Describe(s, band);
            world.Log(EventKind.Military, s, level switch
            {
                Alert.Watch => $"Watch: {what} has been seen, heading this way",
                Alert.Muster => $"Muster: {what} is close; the militia are arming",
                _ => $"Call to arms: {what} is at the walls!",
            });
        }
        // Behind walls under a siege, the normal muster keeps watch; everyone is called up only for the assault.
        bool besieging = band != null && (band.Siege || level == Alert.CallToArms && Siege.WillBesiege(world, s, band));
        if (level >= Alert.Muster) Muster(world, s, besieging ? Alert.Muster : level);
        s.Alert = level;
        s.QuietDays = level == Alert.None ? s.QuietDays + 1 : 0;

        if (level == Alert.CallToArms && band != null)
        {
            // A big band at a walled town digs in; otherwise it attacks at once.
            if (!band.Siege && Siege.WillBesiege(world, s, band)) Siege.Begin(world, s, band);
            if (band.Siege) Siege.Daily(world, s, band);
            else Fight(world, s, band);
        }
        if (s.QuietDays >= world.Data.War.Raids.StandDownDays) StandDown(world, s);
    }

    /// <summary>Raises militia up to the muster share of adults (twice that at a call to arms).</summary>
    internal static void Muster(GameWorld world, Settlement s, Alert level)
    {
        int adults = s.Citizens.Count(c => c.IsAdult);
        double share = s.Rules.MusterPercent / 100.0 * (level == Alert.CallToArms ? 2 : 1);
        int wanted = (int)Math.Round(adults * Math.Min(0.8, share)) - Armies.Soldiers(s);
        // Those who can afford it take up the best gear in store; the rest come as militia.
        foreach (string unit in new[] { "men_at_arms", "spearmen", "archers", "militia" })
        {
            while (wanted > 0)
            {
                var (squad, _) = Armies.Raise(world, s, unit, Math.Min(MusterSquadSize, wanted), levy: true);
                if (squad == null) break;
                wanted -= squad.SoldierIds.Count;
            }
        }
    }

    private static void StandDown(GameWorld world, Settlement s)
    {
        var levies = s.Squads.Where(q => q.Levy).ToList();
        if (levies.Count == 0) return;
        foreach (var q in levies) Armies.Disband(world, s, q);
        world.Log(EventKind.Military, s, "Stand down: the militia have gone back to work");
    }

    /// <summary>The warband attacks: a battle with every squad behind the town's walls; the band is gone afterwards.</summary>
    public static List<Block> AttackerBlocks(GameWorld world, Warband band) => FactionWar.IsArmy(band) ? FactionWar.Blocks(world, band) : new()
    {
        new() { Unit = world.Data.War.Unit("raider")!, Count = band.Raiders },
        new() { Unit = world.Data.War.Unit("raider_archer")!, Count = band.Archers },
    };

    public static List<Block> DefenderBlocks(GameWorld world, Settlement s) => s.Squads.Where(q => q.ArmyId == null).Select(q => new Block
    {
        Unit = world.Data.War.Unit(q.UnitId) ?? world.Data.War.Units[0], Count = q.SoldierIds.Count, Xp = q.Xp, Citizens = q.SoldierIds.ToList(),
    }).ToList();

    /// <summary>The townsfolk who fell are dead; survivors and the general learn from the battle.</summary>
    public static void Bury(GameWorld world, Settlement s, BattleResult result, string how)
    {
        foreach (int id in result.Fallen)
            if (s.FindCitizen(id) is { } c) LifeSystem.Kill(world, s, c, how);
        var units = world.Data.War;
        foreach (var q in s.Squads) q.Xp += units.Battle.SquadXpPerBattle;
        if (s.General is { } g)
        {
            g.Xp += units.Battle.GeneralXpPerBattle;
            g.Battles++;
            g.Skill = Math.Min(100, 10 + Math.Sqrt(g.Xp) * 3);
        }
    }

    /// <summary>Takes losses from a band, archers in proportion.</summary>
    public static void TakeLosses(Warband band, int losses)
    {
        losses = Math.Min(losses, band.Size);
        int archers = band.Size == 0 ? 0 : (int)Math.Round(losses * (double)band.Archers / band.Size);
        band.Archers -= archers;
        band.Raiders = Math.Max(0, band.Raiders - (losses - archers));
    }

    public static void Report(GameWorld world, Settlement s, BattleResult result, string summary)
    {
        world.War.Reports.Add(new BattleReport
        {
            Day = world.Time.Day, SettlementId = s.Id, DefendersWon = result.DefendersWon, Attackers = result.AttackersStart,
            Defenders = result.DefendersStart, AttackerLosses = result.AttackerLosses, DefenderLosses = result.DefenderLosses, Summary = summary,
        });
        if (world.War.Reports.Count > 20) world.War.Reports.RemoveAt(0);
    }

    public static BattleResult Fight(GameWorld world, Settlement s, Warband band)
    {
        var units = world.Data.War;
        var result = BattleResolver.Resolve(world, AttackerBlocks(world, band), DefenderBlocks(world, s), Fortification.Of(world, s), s.General);
        Bury(world, s, result, "fell defending the town");
        FactionWar.TakeAttackerLosses(world, band, result, s.Name);

        var rules = units.Raids;
        string loot = "";
        var carried = new Dictionary<string, int>();
        if (!result.DefendersWon)
        {
            var taken = new List<string>();
            foreach (var (r, n) in s.Stock.Items.ToList())
            {
                int amount = (int)Math.Floor(n * (r == Inventory.Coin ? rules.LootCoinShare : rules.LootGoodsShare));
                if (amount <= 0 || !s.Stock.TryTake(r, amount)) continue;
                s.Ledger.Record(r, -amount, LedgerKeys.Disaster_("raid"));
                carried[r] = amount;
                if (taken.Count < 4) taken.Add($"{amount} {r}");
            }
            loot = taken.Count > 0 ? $"; they carried off {string.Join(", ", taken)}" + (s.Stock.Items.Count() > 4 ? " and more" : "") : "";
        }

        string who = FactionWar.IsArmy(band) ? $"{band.Banner}'s army" : "The raiders";
        string summary = result.DefendersWon
            ? $"{who} {(FactionWar.IsArmy(band) ? "was" : "were")} beaten off: {result.AttackerLosses} of {result.AttackersStart} fell; {result.DefenderLosses} of our {result.DefendersStart} defenders died"
            : result.DefendersStart == 0
                ? $"{who} sacked the town unopposed{loot}"
                : $"{who} broke our {result.DefendersStart} defenders ({result.DefenderLosses} died){loot}";
        world.Log(EventKind.Battle, s, summary);
        if (FactionWar.IsArmy(band) && world.FindSettlement(band.OwnerSettlementId) is { } home)
            world.Log(EventKind.Battle, home, $"At {s.Name}: {summary.Replace("our ", "their ")}");
        Report(world, s, result, summary);
        if (FactionWar.IsArmy(band) && !result.DefendersWon) FactionWar.Victory(world, s, band, carried);
        FactionWar.Disperse(world, band);
        s.Alert = Alert.None;
        return result;
    }
}

/// <summary>Switches raids on or off for a settlement.</summary>
public sealed record SetRaidsCommand(int SettlementId, bool Enabled) : Commands.ISettlementCommand
{
    public Commands.CommandResult Execute(GameWorld world)
    {
        var s = world.FindSettlement(SettlementId);
        if (s == null) return Commands.CommandResult.Fail("Unknown settlement");
        s.Rules.Raids = Enabled;
        if (!Enabled) world.War.Warbands.RemoveAll(w => w.TargetId == s.Id && w.OwnerSettlementId == 0); // bandits only
        return Commands.CommandResult.Success();
    }
}

/// <summary>Sets the share of adults (0-80%) who arm as militia when the town musters.</summary>
public sealed record SetMusterCommand(int SettlementId, int Percent) : Commands.ISettlementCommand
{
    public Commands.CommandResult Execute(GameWorld world)
    {
        var s = world.FindSettlement(SettlementId);
        if (s == null) return Commands.CommandResult.Fail("Unknown settlement");
        s.Rules.MusterPercent = Math.Clamp(Percent, 0, 80);
        return Commands.CommandResult.Success();
    }
}
