using Sim.Core.Commands;
using Sim.Core.Economy;
using Sim.Core.Events;
using Sim.Core.Settlements;
using Sim.Core.Simulation;
using Sim.Core.Time;

namespace Sim.Core.Diplomacy;

/// <summary>Diplomacy tuning (factions.json "diplomacy").</summary>
public sealed class DiplomacyRules
{
    /// <summary>Game days a proposal stays open.</summary>
    public double ProposalDays { get; set; } = 3;

    /// <summary>Caravans between trade-pact partners travel this much faster.</summary>
    public double PactCaravanSpeed { get; set; } = 1.25;

    /// <summary>Governed towns accept a pact partner's offers at up to this much over the fair price.</summary>
    public double PactPriceBonus { get; set; } = 0.2;

    /// <summary>Share of a vassal's daily coin income (taxes and family spending) paid to its overlord.</summary>
    public double VassalShare { get; set; } = 0.15;

    /// <summary>Raids on a vassal are this much less likely (the overlord's protection).</summary>
    public double VassalRaidFactor { get; set; } = 0.5;

    /// <summary>Opinion per day: drift back towards neutral (share), and from pacts and alliances.</summary>
    public double OpinionDecay { get; set; } = 0.01;
    public double PactOpinionPerDay { get; set; } = 0.25;
    public double AllianceOpinionPerDay { get; set; } = 0.15;

    /// <summary>Opinion lost a day towards a neighbour (within <see cref="NeighbourDistance"/>) by the warlike, and towards one far stronger by the cautious.</summary>
    public double RivalryPerDay { get; set; } = 0.1;
    public int NeighbourDistance { get; set; } = 400;

    /// <summary>Opinion per 100 coin's worth of gifts (at most <see cref="MaxGiftOpinion"/> a gift).</summary>
    public double GiftOpinionPer100 { get; set; } = 8;
    public double MaxGiftOpinion { get; set; } = 30;

    public double DemandRefusedOpinion { get; set; } = -15;
    public double DemandPaidOpinion { get; set; } = -5;
    public double TreatyBrokenOpinion { get; set; } = -30;

    /// <summary>What every other faction thinks less of a treaty breaker.</summary>
    public double TreatyBrokenWitnessOpinion { get; set; } = -8;

    /// <summary>Average days between an NPC realm's own diplomatic moves.</summary>
    public double NpcActEveryDays { get; set; } = 20;
}

public enum TreatyKind : byte
{
    TradePact,
    NonAggression,
    Alliance,

    /// <summary>A = overlord, B = vassal.</summary>
    Vassalage,
}

public enum ProposalKind : byte
{
    TradePact,
    NonAggression,
    Alliance,

    /// <summary>The proposer offers to become the receiver's vassal.</summary>
    OfferVassalage,

    /// <summary>The proposer demands the receiver become its vassal.</summary>
    DemandVassalage,

    /// <summary>The proposer demands goods or coin "or else".</summary>
    Demand,

    /// <summary>An end to the war between them.</summary>
    Peace,
}

/// <summary>What two factions think of each other (one number for both; A &lt; B).</summary>
public sealed class Relation
{
    public int A { get; set; }
    public int B { get; set; }

    /// <summary>-100 (hatred) .. 100 (friendship).</summary>
    public double Opinion { get; set; }
}

public sealed class Treaty
{
    public int Id { get; set; }
    public TreatyKind Kind { get; set; }
    public int A { get; set; }
    public int B { get; set; }
    public long StartDay { get; set; }

    /// <summary>Vassalage: tribute owed but not yet paid (less than a coin).</summary>
    public double Owed { get; set; }

    /// <summary>Day it lapses (0: lasts until broken), e.g. the non-aggression that follows a peace.</summary>
    public long EndsDay { get; set; }
}

public sealed class Proposal
{
    public int Id { get; set; }
    public ProposalKind Kind { get; set; }
    public int FromFaction { get; set; }
    public int ToFaction { get; set; }

    /// <summary>For demands: what is asked for.</summary>
    public SortedDictionary<string, int> Goods { get; set; } = new(StringComparer.Ordinal);

    public long ExpiresTick { get; set; }
}

/// <summary>
/// Diplomacy (Phase 5, part 2). Factions hold an opinion of each other that drifts with trade pacts, alliances,
/// gifts, demands, rivalry between neighbours and fear of the strong. They make proposals that expire after a few
/// days: trade pacts (faster caravans, friendlier prices), non-aggression, alliances (shared watch, and help in war
/// later), vassalage (the vassal pays a share of its income for protection from raids) and demands for goods.
/// Gifts are sent at once. NPC realms answer by opinion, strength and temperament, and make their own moves.
/// </summary>
public static class Statecraft
{
    // ---------- queries ----------

    public static Relation RelationOf(GameWorld world, int a, int b)
    {
        if (a > b) (a, b) = (b, a);
        foreach (var r in world.Diplomacy.Relations)
            if (r.A == a && r.B == b) return r;
        var created = new Relation { A = a, B = b };
        world.Diplomacy.Relations.Add(created);
        world.Diplomacy.Relations.Sort((x, y) => x.A != y.A ? x.A.CompareTo(y.A) : x.B.CompareTo(y.B));
        return created;
    }

    public static double Opinion(GameWorld world, int a, int b) => a == b ? 100 : RelationOf(world, a, b).Opinion;

    public static void ChangeOpinion(GameWorld world, int a, int b, double delta)
    {
        if (a == b) return;
        var r = RelationOf(world, a, b);
        r.Opinion = Math.Clamp(r.Opinion + delta, -100, 100);
    }

    public static Treaty? FindTreaty(GameWorld world, TreatyKind kind, int a, int b)
    {
        foreach (var t in world.Diplomacy.Treaties)
            if (t.Kind == kind && (t.A == a && t.B == b || t.A == b && t.B == a)) return t;
        return null;
    }

    public static bool Has(GameWorld world, TreatyKind kind, int a, int b) => FindTreaty(world, kind, a, b) != null;

    public static bool Allied(GameWorld world, int a, int b) => a == b || Has(world, TreatyKind.Alliance, a, b);

    /// <summary>The overlord a faction pays tribute to, if any.</summary>
    public static Faction? OverlordOf(GameWorld world, int faction)
    {
        foreach (var t in world.Diplomacy.Treaties)
            if (t.Kind == TreatyKind.Vassalage && t.B == faction) return world.Diplomacy.Find(t.A);
        return null;
    }

    /// <summary>A faction's seat: its first town, where gifts and tribute arrive.</summary>
    public static Settlement? Capital(GameWorld world, int faction) => world.Settlements.FirstOrDefault(s => s.FactionId == faction);

    /// <summary>Rough military and economic weight: soldiers, walls and people.</summary>
    public static double Strength(GameWorld world, int faction)
    {
        double total = 0;
        foreach (var s in world.Settlements)
            if (s.FactionId == faction)
                total += War.Armies.Soldiers(s) * 2 + s.Walls.Count / 10.0 + s.Citizens.Count / 20.0 + 1;
        return total;
    }

    public static string OpinionWord(double opinion) => opinion switch
    {
        >= 60 => "friendly",
        >= 20 => "warm",
        > -20 => "neutral",
        > -60 => "wary",
        _ => "hostile",
    };

    public static string Describe(ProposalKind kind) => kind switch
    {
        ProposalKind.TradePact => "a trade pact",
        ProposalKind.NonAggression => "a non-aggression pact",
        ProposalKind.Alliance => "an alliance",
        ProposalKind.OfferVassalage => "to become their vassal",
        ProposalKind.DemandVassalage => "that you become their vassal",
        ProposalKind.Peace => "peace",
        _ => "tribute",
    };

    public static string Describe(TreatyKind kind) => kind switch
    {
        TreatyKind.TradePact => "trade pact",
        TreatyKind.NonAggression => "non-aggression pact",
        TreatyKind.Alliance => "alliance",
        _ => "vassalage",
    };

    // ---------- actions (used by the commands below and by NPC realms) ----------

    public static Proposal Propose(GameWorld world, int from, int to, ProposalKind kind, IReadOnlyDictionary<string, int>? goods = null)
    {
        var p = new Proposal
        {
            Id = world.NextId(), Kind = kind, FromFaction = from, ToFaction = to,
            ExpiresTick = world.Time.Tick + TimeScale.DaysToTicks(world.Data.Factions.Diplomacy.ProposalDays),
        };
        if (goods != null)
            foreach (var (r, n) in goods) p.Goods[r] = n;
        world.Diplomacy.Proposals.Add(p);
        if (Capital(world, to) is { } there)
            world.Log(EventKind.Diplomacy, there, $"{world.Diplomacy.Find(from)?.Name} propose {Describe(kind)}" +
                                                  (p.Goods.Count > 0 ? $": {Trade.Trading.Describe(p.Goods)}" : "") + " (N)");
        return p;
    }

    /// <summary>Why a proposal can't be made, or null.</summary>
    public static string? CheckProposal(GameWorld world, int from, int to, ProposalKind kind, IReadOnlyDictionary<string, int>? goods)
    {
        if (from == to) return "That's your own realm";
        if (world.Diplomacy.Find(to) == null || Capital(world, to) == null) return "No such realm";
        if (world.Diplomacy.Proposals.Any(p => p.FromFaction == from && p.ToFaction == to && p.Kind == kind)) return "Already proposed";
        return kind switch
        {
            ProposalKind.TradePact when Has(world, TreatyKind.TradePact, from, to) => "You already have a trade pact",
            ProposalKind.NonAggression when Has(world, TreatyKind.NonAggression, from, to) || Allied(world, from, to) => "You already have one",
            ProposalKind.Alliance when Allied(world, from, to) => "You're already allies",
            ProposalKind.OfferVassalage or ProposalKind.DemandVassalage when OverlordOf(world, kind == ProposalKind.OfferVassalage ? from : to) != null
                => "Already someone's vassal",
            ProposalKind.OfferVassalage or ProposalKind.DemandVassalage when Has(world, TreatyKind.Vassalage, from, to) => "Already bound by vassalage",
            ProposalKind.Peace when War.FactionWar.WarBetween(world, from, to) == null => "You are not at war",
            ProposalKind.TradePact or ProposalKind.NonAggression or ProposalKind.Alliance or ProposalKind.OfferVassalage or ProposalKind.DemandVassalage
                when War.FactionWar.WarBetween(world, from, to) != null => "You are at war: make peace first",
            ProposalKind.Demand when goods == null || goods.Count == 0 || goods.Any(kv => kv.Value <= 0 || !world.Data.Resources.ContainsKey(kv.Key))
                => "Say what you demand",
            _ => null,
        };
    }

    public static void Answer(GameWorld world, Proposal p, bool accept)
    {
        world.Diplomacy.Proposals.Remove(p);
        var rules = world.Data.Factions.Diplomacy;
        var from = world.Diplomacy.Find(p.FromFaction);
        var to = world.Diplomacy.Find(p.ToFaction);
        var fromSeat = Capital(world, p.FromFaction);
        var toSeat = Capital(world, p.ToFaction);
        if (from == null || to == null || fromSeat == null || toSeat == null) return;

        if (p.Kind == ProposalKind.Demand)
        {
            if (accept && toSeat.Stock.Has(p.Goods))
            {
                Transfer(world, toSeat, fromSeat, p.Goods, LedgerKeys.Tribute);
                ChangeOpinion(world, from.Id, to.Id, rules.DemandPaidOpinion);
                LogBoth(world, fromSeat, toSeat, $"{to.Name} gave in to {from.Name}'s demand for {Trade.Trading.Describe(p.Goods)}");
            }
            else
            {
                ChangeOpinion(world, from.Id, to.Id, rules.DemandRefusedOpinion);
                LogBoth(world, fromSeat, toSeat, $"{to.Name} refused {from.Name}'s demand");
            }
            return;
        }

        if (p.Kind == ProposalKind.Peace)
        {
            if (accept && War.FactionWar.WarBetween(world, from.Id, to.Id) is { } war) War.FactionWar.MakePeace(world, war, "agreed");
            else if (!accept) LogBoth(world, fromSeat, toSeat, $"{to.Name} refused {from.Name}'s offer of peace");
            return;
        }

        if (!accept)
        {
            ChangeOpinion(world, from.Id, to.Id, -2);
            LogBoth(world, fromSeat, toSeat, $"{to.Name} turned down {from.Name}'s offer of {Describe(p.Kind)}".Replace("of to become", "to become"));
            return;
        }
        var (kind, a, b) = p.Kind switch
        {
            ProposalKind.TradePact => (TreatyKind.TradePact, from.Id, to.Id),
            ProposalKind.NonAggression => (TreatyKind.NonAggression, from.Id, to.Id),
            ProposalKind.Alliance => (TreatyKind.Alliance, from.Id, to.Id),
            ProposalKind.OfferVassalage => (TreatyKind.Vassalage, to.Id, from.Id),
            _ => (TreatyKind.Vassalage, from.Id, to.Id),
        };
        if (Has(world, kind, a, b) || kind == TreatyKind.Vassalage && OverlordOf(world, b) != null) return;
        world.Diplomacy.Treaties.Add(new Treaty { Id = world.NextId(), Kind = kind, A = a, B = b, StartDay = world.Time.Day });
        ChangeOpinion(world, a, b, 5);
        string what = kind == TreatyKind.Vassalage
            ? $"{world.Diplomacy.Find(b)!.Name} became the vassal of {world.Diplomacy.Find(a)!.Name}"
            : $"{from.Name} and {to.Name} signed {(kind == TreatyKind.Alliance ? "an" : "a")} {Describe(kind)}";
        LogBoth(world, fromSeat, toSeat, what);
    }

    /// <summary>Ends a treaty early: the other side, and everyone watching, think less of the breaker.</summary>
    public static void Break(GameWorld world, Treaty t, int breaker)
    {
        var rules = world.Data.Factions.Diplomacy;
        world.Diplomacy.Treaties.Remove(t);
        int other = t.A == breaker ? t.B : t.A;
        ChangeOpinion(world, breaker, other, rules.TreatyBrokenOpinion);
        foreach (var f in world.Diplomacy.Factions)
            if (f.Id != breaker && f.Id != other) ChangeOpinion(world, breaker, f.Id, rules.TreatyBrokenWitnessOpinion);
        if (Capital(world, breaker) is { } a && Capital(world, other) is { } b)
            LogBoth(world, a, b, $"{world.Diplomacy.Find(breaker)?.Name} broke the {Describe(t.Kind)} with {world.Diplomacy.Find(other)?.Name}");
    }

    public static void Gift(GameWorld world, Settlement from, int toFaction, IReadOnlyDictionary<string, int> goods)
    {
        var rules = world.Data.Factions.Diplomacy;
        var to = Capital(world, toFaction)!;
        Transfer(world, from, to, goods, LedgerKeys.Gifts);
        double value = Trade.Trading.Value(world, goods);
        ChangeOpinion(world, from.FactionId, toFaction, Math.Min(rules.MaxGiftOpinion, value / 100 * rules.GiftOpinionPer100));
        LogBoth(world, from, to, $"{world.Diplomacy.Find(from.FactionId)?.Name} sent {world.Diplomacy.Find(toFaction)?.Name} a gift of {Trade.Trading.Describe(goods)}");
    }

    private static void Transfer(GameWorld world, Settlement from, Settlement to, IReadOnlyDictionary<string, int> goods, string key)
    {
        from.Stock.TryTakeAll(goods);
        from.Ledger.RecordAll(goods, -1, key);
        foreach (var (r, n) in goods) to.Stock.Add(r, n);
        to.Ledger.RecordAll(goods, 1, key);
    }

    private static void LogBoth(GameWorld world, Settlement a, Settlement b, string message)
    {
        world.Log(EventKind.Diplomacy, a, message);
        if (b != a) world.Log(EventKind.Diplomacy, b, message);
    }

    // ---------- daily ----------

    public static void Daily(GameWorld world)
    {
        var book = world.Diplomacy;
        var rules = world.Data.Factions.Diplomacy;

        // Proposals run out: an unanswered demand counts as refused.
        foreach (var p in book.Proposals.Where(p => p.ExpiresTick <= world.Time.Tick).ToList())
        {
            if (p.Kind == ProposalKind.Demand) Answer(world, p, accept: false);
            else book.Proposals.Remove(p);
        }

        // Opinions drift.
        var factions = book.Factions.Where(f => Capital(world, f.Id) != null).ToList();
        for (int i = 0; i < factions.Count; i++)
        for (int j = i + 1; j < factions.Count; j++)
        {
            var (a, b) = (factions[i], factions[j]);
            var r = RelationOf(world, a.Id, b.Id);
            double delta = -r.Opinion * rules.OpinionDecay;
            if (Has(world, TreatyKind.TradePact, a.Id, b.Id)) delta += rules.PactOpinionPerDay;
            if (Has(world, TreatyKind.Alliance, a.Id, b.Id)) delta += rules.AllianceOpinionPerDay;
            var (sa, sb) = (Capital(world, a.Id)!, Capital(world, b.Id)!);
            bool neighbours = Math.Max(Math.Abs(sa.CenterX - sb.CenterX), Math.Abs(sa.CenterY - sb.CenterY)) <= rules.NeighbourDistance;
            if (neighbours)
            {
                var (pa, pb) = (Temper(world, a), Temper(world, b));
                delta -= rules.RivalryPerDay * ((pa?.Aggression ?? 0) + (pb?.Aggression ?? 0)) / 2;
                double ratio = Strength(world, a.Id) / Math.Max(1, Strength(world, b.Id));
                if (ratio > 2) delta -= rules.RivalryPerDay * (pb?.Caution ?? 0);
                if (ratio < 0.5) delta -= rules.RivalryPerDay * (pa?.Caution ?? 0);
            }
            r.Opinion = Math.Clamp(r.Opinion + delta, -100, 100);
        }

        // Vassals pay their share of yesterday's income; treaties with vanished realms, or past their end, lapse.
        foreach (var t in book.Treaties.ToList())
        {
            var (a, b) = (Capital(world, t.A), Capital(world, t.B));
            if (a == null || b == null || t.EndsDay > 0 && world.Time.Day >= t.EndsDay)
            {
                book.Treaties.Remove(t);
                continue;
            }
            if (t.Kind != TreatyKind.Vassalage) continue;
            double income = b.Ledger.Average(Inventory.Coin, k => LedgerKeys.Category(k) is LedgerKeys.Taxes or HouseholdSystem.SalesKey, 1);
            t.Owed += Math.Max(0, income) * rules.VassalShare;
            int tribute = Math.Min(b.Stock.Get(Inventory.Coin), (int)Math.Floor(t.Owed));
            if (tribute <= 0) continue;
            t.Owed -= tribute;
            Transfer(world, b, a, new Dictionary<string, int> { [Inventory.Coin] = tribute }, LedgerKeys.Tribute);
        }

        foreach (var f in factions)
            if (f.IsNpc) NpcDiplomacy.Daily(world, f);
    }

    public static PersonalityDef? Temper(GameWorld world, Faction f) => f.IsNpc ? world.Data.Factions.Personality(f.Personality) : null;
}

/// <summary>How NPC realms answer proposals and make their own moves.</summary>
public static class NpcDiplomacy
{
    public static void Daily(GameWorld world, Faction me)
    {
        var temper = world.Data.Factions.Personality(me.Personality);
        foreach (var p in world.Diplomacy.Proposals.Where(p => p.ToFaction == me.Id).ToList())
            Statecraft.Answer(world, p, WouldAccept(world, me, temper, p));

        if (!world.Rng.Chance(1 / Math.Max(1, world.Data.Factions.Diplomacy.NpcActEveryDays))) return;
        MakeAMove(world, me, temper);
    }

    public static bool WouldAccept(GameWorld world, Faction me, PersonalityDef temper, Proposal p)
    {
        double opinion = Statecraft.Opinion(world, me.Id, p.FromFaction);
        double ratio = Statecraft.Strength(world, p.FromFaction) / Math.Max(1, Statecraft.Strength(world, me.Id));
        switch (p.Kind)
        {
            case ProposalKind.TradePact: return opinion > -20 + (1 - temper.TradeBias) * 30;
            case ProposalKind.NonAggression: return opinion > 0 - temper.Caution * 20 || ratio > 1.5 && temper.Caution >= 0.5;
            case ProposalKind.Alliance: return opinion > 40 - temper.Caution * 10;
            case ProposalKind.OfferVassalage: return opinion > -30;
            case ProposalKind.DemandVassalage: return ratio >= 2.5 && (temper.Caution >= 0.7 || opinion > 20);
            case ProposalKind.Peace: return War.NpcWar.WouldMakePeace(world, me, p.FromFaction);
            case ProposalKind.Demand:
            {
                var seat = Statecraft.Capital(world, me.Id);
                if (seat == null || !seat.Stock.Has(p.Goods)) return false;
                double share = Trade.Trading.Value(world, p.Goods) / Math.Max(1, seat.Stock.Get(Inventory.Coin) + 1);
                return ratio >= 1.5 + temper.Aggression && share <= 0.25;
            }
            default: return false;
        }
    }

    private static void MakeAMove(GameWorld world, Faction me, PersonalityDef temper)
    {
        var seat = Statecraft.Capital(world, me.Id);
        if (seat == null) return;
        double myStrength = Statecraft.Strength(world, me.Id);
        var others = world.Diplomacy.Factions.Where(f => f.Id != me.Id && Statecraft.Capital(world, f.Id) != null)
            .OrderByDescending(f => Statecraft.Opinion(world, me.Id, f.Id)).ThenBy(f => f.Id).ToList();
        if (others.Count == 0) return;

        bool Try(Faction to, ProposalKind kind, IReadOnlyDictionary<string, int>? goods = null)
        {
            if (Statecraft.CheckProposal(world, me.Id, to.Id, kind, goods) != null) return false;
            Statecraft.Propose(world, me.Id, to.Id, kind, goods);
            return true;
        }

        foreach (var other in others)
        {
            double opinion = Statecraft.Opinion(world, me.Id, other.Id);
            double ratio = myStrength / Math.Max(1, Statecraft.Strength(world, other.Id));
            var theirSeat = Statecraft.Capital(world, other.Id)!;

            if (ratio >= 2 + (1 - temper.Aggression) && temper.Aggression >= 0.6 && opinion < 20)
            {
                // The strong lean on the weak: tribute, or vassalage from the very weak.
                if (ratio >= 3 && Try(other, ProposalKind.DemandVassalage)) return;
                int coin = Math.Max(50, theirSeat.Stock.Get(Inventory.Coin) / 10);
                if (Try(other, ProposalKind.Demand, new Dictionary<string, int> { [Inventory.Coin] = coin })) return;
            }
            if (ratio <= 0.4 && temper.Caution >= 0.7 && opinion > -20 && Try(other, ProposalKind.OfferVassalage)) return;
            if (opinion > 50 - temper.Caution * 10 && Try(other, ProposalKind.Alliance)) return;
            if (ratio < 0.8 && temper.Caution >= 0.5 && opinion > -30 && Try(other, ProposalKind.NonAggression)) return;
            if (opinion > -10 && world.Rng.Chance(temper.TradeBias) && Try(other, ProposalKind.TradePact)) return;
        }

        // Nothing to propose: a gift to the realm it likes best, if it can spare it.
        var friend = others[0];
        int spare = seat.Stock.Get(Inventory.Coin) / 20;
        if (spare >= 30 && Statecraft.Opinion(world, me.Id, friend.Id) > -40)
            Statecraft.Gift(world, seat, friend.Id, new Dictionary<string, int> { [Inventory.Coin] = Math.Min(spare, 150) });
    }
}

// ---------- commands (from the acting town; its realm is the one acting) ----------

public sealed record ProposeCommand(int SettlementId, int TargetFactionId, ProposalKind Kind,
    IReadOnlyDictionary<string, int>? Goods = null) : ISettlementCommand
{
    public CommandResult Execute(GameWorld world)
    {
        var s = world.FindSettlement(SettlementId);
        if (s == null) return CommandResult.Fail("Unknown settlement");
        string? error = Statecraft.CheckProposal(world, s.FactionId, TargetFactionId, Kind, Goods);
        if (error != null) return CommandResult.Fail(error);
        return CommandResult.Success(Statecraft.Propose(world, s.FactionId, TargetFactionId, Kind, Goods).Id);
    }
}

public sealed record AnswerProposalCommand(int SettlementId, int ProposalId, bool Accept) : ISettlementCommand
{
    public CommandResult Execute(GameWorld world)
    {
        var s = world.FindSettlement(SettlementId);
        if (s == null) return CommandResult.Fail("Unknown settlement");
        var p = world.Diplomacy.Proposals.FirstOrDefault(x => x.Id == ProposalId);
        if (p == null) return CommandResult.Fail("That proposal is gone");
        if (p.ToFaction != s.FactionId) return CommandResult.Fail("That proposal wasn't made to you");
        if (Accept && p.Kind == ProposalKind.Demand && !s.Stock.Has(p.Goods)) return CommandResult.Fail($"You don't have {Trade.Trading.Describe(p.Goods)}");
        Statecraft.Answer(world, p, Accept);
        return CommandResult.Success();
    }
}

public sealed record EndTreatyCommand(int SettlementId, int TreatyId) : ISettlementCommand
{
    public CommandResult Execute(GameWorld world)
    {
        var s = world.FindSettlement(SettlementId);
        if (s == null) return CommandResult.Fail("Unknown settlement");
        var t = world.Diplomacy.Treaties.FirstOrDefault(x => x.Id == TreatyId);
        if (t == null) return CommandResult.Fail("No such treaty");
        if (t.A != s.FactionId && t.B != s.FactionId) return CommandResult.Fail("Not your treaty");
        Statecraft.Break(world, t, s.FactionId);
        return CommandResult.Success();
    }
}

public sealed record SendGiftCommand(int SettlementId, int TargetFactionId, IReadOnlyDictionary<string, int> Goods) : ISettlementCommand
{
    public CommandResult Execute(GameWorld world)
    {
        var s = world.FindSettlement(SettlementId);
        if (s == null) return CommandResult.Fail("Unknown settlement");
        if (TargetFactionId == s.FactionId || Statecraft.Capital(world, TargetFactionId) == null) return CommandResult.Fail("No such realm");
        if (Goods.Count == 0 || Goods.Any(kv => kv.Value <= 0 || !world.Data.Resources.ContainsKey(kv.Key))) return CommandResult.Fail("Choose a gift");
        if (!s.Stock.Has(Goods)) return CommandResult.Fail($"You don't have {Trade.Trading.Describe(Goods)}");
        Statecraft.Gift(world, s, TargetFactionId, Goods);
        return CommandResult.Success();
    }
}
