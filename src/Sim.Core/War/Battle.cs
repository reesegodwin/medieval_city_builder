using Sim.Core.Settlements;
using Sim.Core.Simulation;
using Sim.Core.Systems;

namespace Sim.Core.War;

/// <summary>Battle tuning (units.json "battle").</summary>
public sealed class BattleRules
{
    public int MaxRounds { get; set; } = 10;

    /// <summary>Share of the enemy's strength that becomes casualties each round (before defence).</summary>
    public double Lethality { get; set; } = 0.07;

    /// <summary>A side routs when its morale falls below this...</summary>
    public double RoutMorale { get; set; } = 0.3;

    /// <summary>...or fewer than this share of it is left standing.</summary>
    public double RoutRemaining { get; set; } = 0.35;

    /// <summary>Archers' first two rounds (volleys before the lines meet).</summary>
    public double RangedOpening { get; set; } = 1.25;

    /// <summary>Defence behind a closed ring of walls, plus per wall tier above the palisade.</summary>
    public double WallDefence { get; set; } = 0.3;
    public double WallDefencePerTier { get; set; } = 0.25;

    /// <summary>Extra for archers shooting from walls and towers.</summary>
    public double WallRanged { get; set; } = 0.3;

    /// <summary>Defence per tower, up to a cap.</summary>
    public double TowerBonus { get; set; } = 0.02;
    public double MaxTowerBonus { get; set; } = 0.3;

    /// <summary>Squad experience for the full veteran bonus, and that bonus.</summary>
    public double VeteranXp { get; set; } = 100;
    public double MaxVeteranBonus { get; set; } = 0.3;

    public double SquadXpPerBattle { get; set; } = 15;
    public double GeneralXpPerBattle { get; set; } = 20;
}

/// <summary>A block of fighters of one kind: a squad of townsfolk, or a band of raiders.</summary>
public sealed class Block
{
    public UnitDef Unit { get; init; } = new();
    public int Count { get; set; }
    public double Xp { get; init; }

    /// <summary>The townsfolk in it (defenders), so the fallen are real people.</summary>
    public List<int>? Citizens { get; init; }
}

/// <summary>What a town's walls give its defenders.</summary>
public readonly record struct Fortification(int RingTier, int Towers)
{
    public static Fortification Of(GameWorld world, Settlement s) =>
        new(WallSystem.Enclose(world, s).RingTier, WallSystem.Towers(s).Count());
}

public sealed class BattleResult
{
    public bool DefendersWon { get; set; }
    public int Rounds { get; set; }
    public int AttackersStart { get; set; }
    public int DefendersStart { get; set; }
    public int AttackerLosses { get; set; }
    public int DefenderLosses { get; set; }

    /// <summary>Townsfolk who fell (citizen ids), in the order they fell.</summary>
    public List<int> Fallen { get; } = new();

    /// <summary>Attacking soldiers who fell (citizen ids), when the attackers are a town's army.</summary>
    public List<int> AttackersFallen { get; } = new();
}

/// <summary>
/// The aggregate battle resolver (Phase 3): rounds of combat between two sides of blocks (a melee line, archers
/// behind). Each round a side inflicts casualties from its strength (troop counts, kind of soldier, experience,
/// general) against the other's defence (kind of soldier, walls and towers for a town's defenders). Losses fall
/// mostly on the melee line, cost morale, and a side whose morale breaks, or that has lost too many, routs.
/// Deterministic: all chance comes from the world's random generator.
/// </summary>
public static class BattleResolver
{
    public static int Count(IEnumerable<Block> side) => side.Sum(b => b.Count);

    public static BattleResult Resolve(GameWorld world, List<Block> attackers, List<Block> defenders, Fortification fort,
        General? defenderGeneral = null)
    {
        var rules = world.Data.War.Battle;
        var result = new BattleResult { AttackersStart = Count(attackers), DefendersStart = Count(defenders) };
        if (result.DefendersStart == 0)
        {
            result.DefendersWon = false;
            return result;
        }
        if (result.AttackersStart == 0)
        {
            result.DefendersWon = true;
            return result;
        }

        var traits = defenderGeneral?.Traits.Select(t => world.Data.War.Trait(t)).OfType<GeneralTraitDef>().ToList() ?? new();
        double genSkill = defenderGeneral == null ? 0 : defenderGeneral.Skill / 200; // up to +50%
        double genAttack = 1 + genSkill + traits.Sum(t => t.Attack);
        double genDefence = 1 + genSkill / 2 + traits.Sum(t => t.Defence);
        double genRanged = traits.Sum(t => t.Ranged);
        double genWalls = traits.Sum(t => t.Walls);
        double genMorale = traits.Sum(t => t.Morale);

        bool walled = fort.RingTier >= 0;
        double wallDefence = walled ? 1 + (rules.WallDefence + rules.WallDefencePerTier * fort.RingTier) * (1 + genWalls) : 1;
        double towerDefence = walled ? Math.Min(rules.MaxTowerBonus, fort.Towers * rules.TowerBonus) : 0;

        double Veteran(Block b) => 1 + Math.Min(rules.MaxVeteranBonus, b.Xp / Math.Max(1, rules.VeteranXp) * rules.MaxVeteranBonus);
        double Power(List<Block> side, bool isDefender, int round) => side.Sum(b =>
        {
            double p = b.Count * b.Unit.Attack * Veteran(b);
            if (b.Unit.Ranged)
            {
                if (round <= 2) p *= rules.RangedOpening;
                if (isDefender && walled) p *= 1 + rules.WallRanged;
                if (isDefender) p *= 1 + genRanged;
            }
            return isDefender ? p * genAttack : p;
        });
        double Defence(List<Block> side, bool isDefender)
        {
            int n = Count(side);
            if (n == 0) return 1;
            double d = side.Sum(b => b.Count * b.Unit.Defence) / n;
            return isDefender ? d * genDefence * (wallDefence + towerDefence) : d;
        }

        double attackerMorale = 1, defenderMorale = 1 + genMorale + (walled ? 0.2 : 0);
        for (int round = 1; round <= rules.MaxRounds; round++)
        {
            result.Rounds = round;
            double aPower = Power(attackers, false, round), dPower = Power(defenders, true, round);
            double toDefenders = dPower == 0 && aPower == 0 ? 0 : aPower * rules.Lethality / Defence(defenders, true) * (0.85 + 0.3 * world.Rng.NextDouble());
            double toAttackers = dPower * rules.Lethality / Defence(attackers, false) * (0.85 + 0.3 * world.Rng.NextDouble());

            int aBefore = Count(attackers), dBefore = Count(defenders);
            result.AttackerLosses += TakeLosses(world, attackers, toAttackers, result.AttackersFallen);
            result.DefenderLosses += TakeLosses(world, defenders, toDefenders, result.Fallen);
            int aAfter = Count(attackers), dAfter = Count(defenders);

            attackerMorale -= aBefore == 0 ? 1 : 2.0 * (aBefore - aAfter) / result.AttackersStart;
            defenderMorale -= (walled ? 1.4 : 2.0) * (dBefore - dAfter) / result.DefendersStart;

            bool attackersBreak = aAfter == 0 || attackerMorale < rules.RoutMorale || aAfter < result.AttackersStart * rules.RoutRemaining;
            bool defendersBreak = dAfter == 0 || defenderMorale < rules.RoutMorale || dAfter < result.DefendersStart * rules.RoutRemaining;
            if (attackersBreak || defendersBreak)
            {
                // If both break the same round, whoever has kept more of their strength holds the field.
                result.DefendersWon = !defendersBreak || (attackersBreak && dAfter / (double)result.DefendersStart >= aAfter / (double)result.AttackersStart);
                return result;
            }
        }
        result.DefendersWon = true; // the attackers couldn't break through before giving up
        return result;
    }

    /// <summary>Spreads casualties over the blocks, mostly on the melee line (archers stand behind).</summary>
    private static int TakeLosses(GameWorld world, List<Block> side, double losses, List<int>? fallen)
    {
        int whole = (int)Math.Floor(losses);
        if (world.Rng.NextDouble() < losses - whole) whole++;
        int taken = 0;
        while (taken < whole)
        {
            double total = side.Sum(b => b.Count * (b.Unit.Ranged ? 0.4 : 1.0));
            if (total <= 0) break;
            double pick = world.Rng.NextDouble() * total;
            Block? hit = null;
            foreach (var b in side)
            {
                double w = b.Count * (b.Unit.Ranged ? 0.4 : 1.0);
                if (w <= 0) continue;
                hit = b;
                if (pick < w) break;
                pick -= w;
            }
            if (hit == null) break;
            hit.Count--;
            if (hit.Citizens is { Count: > 0 } people)
            {
                int i = world.Rng.Next(people.Count);
                fallen?.Add(people[i]);
                people.RemoveAt(i);
            }
            taken++;
        }
        return taken;
    }
}
