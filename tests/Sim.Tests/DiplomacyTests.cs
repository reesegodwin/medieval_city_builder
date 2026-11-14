using Sim.Core.Commands;
using Sim.Core.Diplomacy;
using Sim.Core.Economy;
using Sim.Core.Persistence;
using Sim.Core.Settlements;
using Sim.Core.Simulation;
using Sim.Core.Time;
using Sim.Core.War;

namespace Sim.Tests;

public class DiplomacyTests
{
    /// <summary>The player's town and two NPC realms, a few days in.</summary>
    private static (GameWorld World, Simulator Sim, Settlement Me, Faction A, Faction B) Land()
    {
        var world = new GameWorld(42);
        world.Settings.NpcTowns = 2;
        SettlementFounder.Found(world);
        var sim = new Simulator(world);
        sim.RunDays(3);
        var me = world.Settlements[0];
        var npcs = world.Diplomacy.Factions.Where(f => f.IsNpc).ToList();
        // Keep the NPCs from making their own moves during the tests.
        world.Diplomacy.Proposals.Clear();
        return (world, sim, me, npcs[0], npcs[1]);
    }

    [Fact]
    public void AcceptedProposalsBecomeTreaties()
    {
        var (world, _, me, a, _) = Land();
        var sent = world.Execute(new ProposeCommand(me.Id, a.Id, ProposalKind.TradePact));
        Assert.True(sent.Ok, sent.Error);
        Assert.False(world.Execute(new ProposeCommand(me.Id, a.Id, ProposalKind.TradePact)).Ok); // already proposed
        var p = Assert.Single(world.Diplomacy.Proposals);
        Statecraft.Answer(world, p, accept: true);
        Assert.True(Statecraft.Has(world, TreatyKind.TradePact, me.FactionId, a.Id));
        Assert.Empty(world.Diplomacy.Proposals);
        Assert.False(world.Execute(new ProposeCommand(me.Id, a.Id, ProposalKind.TradePact)).Ok); // already have one

        // Pact partners' caravans travel faster.
        var seat = Statecraft.Capital(world, a.Id)!;
        Assert.True(Sim.Core.Trade.Trading.PactTravel(world, me, seat, 1000) < 1000);
    }

    [Fact]
    public void ProposalsLapseAndUnansweredDemandsCountAsRefused()
    {
        var (world, sim, me, a, _) = Land();
        double before = Statecraft.Opinion(world, me.FactionId, a.Id);
        Statecraft.Propose(world, a.Id, me.FactionId, ProposalKind.Demand, new Dictionary<string, int> { ["coin"] = 50 });
        Statecraft.Propose(world, a.Id, me.FactionId, ProposalKind.NonAggression);
        var expires = world.Diplomacy.Proposals.Max(p => p.ExpiresTick);
        sim.RunTicks(expires - world.Time.Tick + TimeScale.TicksPerDay);
        Assert.DoesNotContain(world.Diplomacy.Proposals, p => p.ToFaction == me.FactionId);
        Assert.True(Statecraft.Opinion(world, me.FactionId, a.Id) < before + 1); // the refused demand hurt (drift is small)
        Assert.Contains(world.Events, e => e.Message.Contains("refused"));
    }

    [Fact]
    public void DemandsMoveGoodsOrSourRelations()
    {
        var (world, _, me, a, b) = Land();
        me.Stock.Add("coin", 500);
        var seatA = Statecraft.Capital(world, a.Id)!;
        int coinA = seatA.Stock.Get("coin"), mine = me.Stock.Get("coin");
        var demand = Statecraft.Propose(world, a.Id, me.FactionId, ProposalKind.Demand, new Dictionary<string, int> { ["coin"] = 100 });
        Assert.True(world.Execute(new AnswerProposalCommand(me.Id, demand.Id, true)).Ok);
        Assert.Equal(mine - 100, me.Stock.Get("coin"));
        Assert.Equal(coinA + 100, seatA.Stock.Get("coin"));

        double opinion = Statecraft.Opinion(world, me.FactionId, b.Id);
        var other = Statecraft.Propose(world, b.Id, me.FactionId, ProposalKind.Demand, new Dictionary<string, int> { ["coin"] = 100 });
        Assert.True(world.Execute(new AnswerProposalCommand(me.Id, other.Id, false)).Ok);
        Assert.Equal(opinion + world.Data.Factions.Diplomacy.DemandRefusedOpinion, Statecraft.Opinion(world, me.FactionId, b.Id), 6);
    }

    [Fact]
    public void GiftsWarmRelations()
    {
        var (world, _, me, a, _) = Land();
        me.Stock.Add("coin", 300);
        double before = Statecraft.Opinion(world, me.FactionId, a.Id);
        Assert.True(world.Execute(new SendGiftCommand(me.Id, a.Id, new Dictionary<string, int> { ["coin"] = 200 })).Ok);
        Assert.True(Statecraft.Opinion(world, me.FactionId, a.Id) > before + 10);
        Assert.False(world.Execute(new SendGiftCommand(me.Id, a.Id, new Dictionary<string, int> { ["coin"] = 100000 })).Ok);
    }

    [Fact]
    public void VassalsPayTheirOverlordAndAreRaidedLess()
    {
        var (world, sim, me, a, _) = Land();
        var offer = Statecraft.Propose(world, me.FactionId, a.Id, ProposalKind.OfferVassalage);
        Statecraft.Answer(world, offer, accept: true);
        Assert.Equal(a.Id, Statecraft.OverlordOf(world, me.FactionId)?.Id);
        Assert.False(world.Execute(new ProposeCommand(me.Id, a.Id, ProposalKind.OfferVassalage)).Ok);

        var seat = Statecraft.Capital(world, a.Id)!;
        sim.RunDays(20);
        Assert.True(seat.Ledger.History.Sum(d => d.Get("coin", LedgerKeys.Tribute)) > 0, "tribute arrived");
        Assert.True(me.Ledger.History.Sum(d => d.Get("coin", LedgerKeys.Tribute)) < 0, "tribute paid");
    }

    [Fact]
    public void BreakingATreatyIsRemembered()
    {
        var (world, _, me, a, b) = Land();
        Statecraft.Answer(world, Statecraft.Propose(world, me.FactionId, a.Id, ProposalKind.NonAggression), accept: true);
        var treaty = Assert.Single(world.Diplomacy.Treaties);
        double withA = Statecraft.Opinion(world, me.FactionId, a.Id), withB = Statecraft.Opinion(world, me.FactionId, b.Id);
        Assert.True(world.Execute(new EndTreatyCommand(me.Id, treaty.Id)).Ok);
        Assert.Empty(world.Diplomacy.Treaties);
        Assert.True(Statecraft.Opinion(world, me.FactionId, a.Id) < withA - 20);
        Assert.True(Statecraft.Opinion(world, me.FactionId, b.Id) < withB);
    }

    [Fact]
    public void NpcRealmsAnswerByOpinionStrengthAndTemper()
    {
        var (world, _, me, a, _) = Land();
        var rules = world.Data.Factions;
        var trader = rules.Personality("trader");
        var warlike = rules.Personality("warlike");
        Statecraft.ChangeOpinion(world, me.FactionId, a.Id, -Statecraft.Opinion(world, me.FactionId, a.Id)); // neutral
        var pact = new Proposal { Kind = ProposalKind.TradePact, FromFaction = me.FactionId, ToFaction = a.Id };
        Assert.True(NpcDiplomacy.WouldAccept(world, a, trader, pact));
        var alliance = new Proposal { Kind = ProposalKind.Alliance, FromFaction = me.FactionId, ToFaction = a.Id };
        Assert.False(NpcDiplomacy.WouldAccept(world, a, warlike, alliance)); // only friends ally
        Statecraft.ChangeOpinion(world, me.FactionId, a.Id, 80);
        Assert.True(NpcDiplomacy.WouldAccept(world, a, warlike, alliance));
        var demand = new Proposal { Kind = ProposalKind.Demand, FromFaction = me.FactionId, ToFaction = a.Id, Goods = { ["coin"] = 10 } };
        Assert.False(NpcDiplomacy.WouldAccept(world, a, warlike, demand)); // nobody pays an equal
    }

    [Fact]
    public void AlliesShareTheirWatch()
    {
        var (world, _, me, a, _) = Land();
        var seat = Statecraft.Capital(world, a.Id)!;
        var band = new Warband { Id = world.NextId(), TargetId = seat.Id, X = seat.CenterX + Raids.WatchRadius(seat) - 2, Y = seat.CenterY, Raiders = 10 };
        world.War.Warbands.Add(band);
        Assert.False(Raids.Sees(world, me, band));
        Statecraft.Answer(world, Statecraft.Propose(world, me.FactionId, a.Id, ProposalKind.Alliance), accept: true);
        Assert.True(Raids.Sees(world, me, band));
    }

    [Fact]
    public void DiplomacySurvivesSaveLoadAndTheWire()
    {
        var (world, _, me, a, b) = Land();
        Statecraft.Answer(world, Statecraft.Propose(world, me.FactionId, a.Id, ProposalKind.TradePact), accept: true);
        Statecraft.Propose(world, b.Id, me.FactionId, ProposalKind.Demand, new Dictionary<string, int> { ["coin"] = 40 });
        Statecraft.ChangeOpinion(world, me.FactionId, b.Id, -12.5);
        var loaded = SaveGame.FromJson(SaveGame.ToJson(world));
        Assert.Equal(world.ComputeStateHash(), loaded.ComputeStateHash());
        Assert.Single(loaded.Diplomacy.Treaties);
        Assert.Equal(40, loaded.Diplomacy.Proposals.Single().Goods["coin"]);

        var command = new ProposeCommand(me.Id, b.Id, ProposalKind.Demand, new Dictionary<string, int> { ["coin"] = 7 });
        var back = (ProposeCommand)CommandCodec.Decode(System.Text.Json.Nodes.JsonNode.Parse(CommandCodec.Encode(command).ToJsonString())!);
        Assert.Equal(ProposalKind.Demand, back.Kind);
        Assert.Equal(7, back.Goods!["coin"]);
    }
}
