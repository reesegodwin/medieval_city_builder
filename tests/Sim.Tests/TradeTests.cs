using Sim.Core.AI;
using Sim.Core.Commands;
using Sim.Core.Events;
using Sim.Core.Persistence;
using Sim.Core.Settlements;
using Sim.Core.Simulation;
using Sim.Core.Systems;
using Sim.Core.Time;
using Sim.Core.Trade;
using Sim.Net;
using Sim.Core.World;

namespace Sim.Tests;

public class TradeTests
{
    private static Dictionary<string, int> Goods(string r, int n) => new() { [r] = n };

    /// <summary>Two hand-run towns in one world, each with a finished, staffed Trading Post.</summary>
    private static (GameWorld World, Settlement A, Settlement B, Simulator Sim) TwoTowns()
    {
        var world = new GameWorld(42);
        world.Execute(new FoundSettlementCommand("a"));
        world.Execute(new FoundSettlementCommand("b"));
        foreach (var s in world.Settlements)
        {
            s.Rules.AutoBuild = false;
            var post = Scenario.Place(world, s, "trading_post");
            Scenario.Finish(world, s, post);
            RoadPlanner.Connect(world, s, post);
            Roads.FinishAllPlans(world, s);
            JobSystem.Tick(world, s);
        }
        Scenario.ConnectByRoad(world, world.Settlements[0], world.Settlements[1]);
        return (world, world.Settlements[0], world.Settlements[1], new Simulator(world));
    }

    [Fact]
    public void PostingSetsGoodsAsideAndCancellingReturnsThem()
    {
        var (world, a, b, _) = TwoTowns();
        a.Stock.Add("cloth", 10);

        var posted = world.Execute(new PostOfferCommand(a.Id, Goods("cloth", 10), Goods("coin", 60)));
        Assert.True(posted.Ok, posted.Error);
        Assert.Equal(0, a.Stock.Get("cloth"));

        Assert.False(world.Execute(new CancelOfferCommand(b.Id, posted.CreatedId!.Value)).Ok); // not B's offer
        Assert.True(world.Execute(new CancelOfferCommand(a.Id, posted.CreatedId.Value)).Ok);
        Assert.Equal(10, a.Stock.Get("cloth"));
        Assert.Empty(world.Trade.Offers);
    }

    [Fact]
    public void TownsWithoutATradingPostCantTrade()
    {
        var (world, town, _) = Scenario.NewTown();
        town.Stock.Add("cloth", 10);
        var result = world.Execute(new PostOfferCommand(town.Id, Goods("cloth", 10), Goods("coin", 60)));
        Assert.False(result.Ok);
        Assert.Equal(10, town.Stock.Get("cloth"));
    }

    [Fact]
    public void UnsoldOffersExpireAndGoodsComeBack()
    {
        var (world, a, _, sim) = TwoTowns();
        a.Stock.Add("cloth", 10);
        world.Execute(new PostOfferCommand(a.Id, Goods("cloth", 10), Goods("coin", 999)));

        sim.RunDays((int)world.Data.Trade.OfferDays + 1);

        Assert.Empty(world.Trade.Offers);
        Assert.Equal(10, a.Stock.Get("cloth"));
    }

    [Fact]
    public void CaravansNeedARoad()
    {
        var world = new GameWorld(42);
        world.Execute(new FoundSettlementCommand("a"));
        world.Execute(new FoundSettlementCommand("b"));
        foreach (var s in world.Settlements)
        {
            s.Rules.AutoBuild = false;
            Scenario.Finish(world, s, Scenario.Place(world, s, "trading_post"));
        }
        var (a, b) = (world.Settlements[0], world.Settlements[1]);
        a.Stock.Add("cloth", 10);
        var offer = world.Execute(new PostOfferCommand(a.Id, Goods("cloth", 10), Goods("coin", 30)));
        var refused = world.Execute(new AcceptOfferCommand(b.Id, offer.CreatedId!.Value));
        Assert.False(refused.Ok);
        Assert.Contains("road", refused.Error);
        Assert.Single(world.Trade.Offers); // nothing happened
    }

    [Fact]
    public void AcceptingIsInstantButGoodsTravelByCaravan()
    {
        var (world, a, b, sim) = TwoTowns();
        a.Stock.Add("cloth", 10);
        int bCoin = b.Stock.Get("coin"), aCoin = a.Stock.Get("coin");
        var offer = world.Execute(new PostOfferCommand(a.Id, Goods("cloth", 10), Goods("coin", 30)));

        Assert.True(world.Execute(new AcceptOfferCommand(b.Id, offer.CreatedId!.Value)).Ok);
        Assert.Equal(bCoin - 30, b.Stock.Get("coin")); // paid at once...
        Assert.Equal(0, b.Stock.Get("cloth"));          // ...but the cloth is on the road
        var caravans = world.Trade.Caravans.Where(c => c.Kind == CaravanKind.Delivery).ToList();
        Assert.Equal(2, caravans.Count);
        Assert.All(Trading.Points(caravans[0]), t => Assert.True(world.Map.GetTile(t.X, t.Y).IsRoad)); // only on roads

        long travel = caravans[0].ArriveTick - world.Time.Tick;
        Assert.True(travel >= TimeScale.TicksPerDay);
        sim.RunTicks(travel - TimeScale.TicksPerHour);
        Assert.Equal(0, b.Stock.Get("cloth"));

        sim.RunTicks(TimeScale.TicksPerHour * 2);
        Assert.Equal(10, b.Stock.Get("cloth"));
        Assert.True(a.Stock.Get("coin") >= aCoin + 30 - 5); // coin arrived (a little may have been spent on nothing)
    }

    [Fact]
    public void RoutesDeliverEachRoundAndSkipWhenShort()
    {
        var (world, a, b, sim) = TwoTowns();
        a.Stock.Add("cloth", 10);
        b.Stock.Add("tools", 4);
        var route = world.Execute(new ProposeRouteCommand(a.Id, b.Id, Goods("cloth", 5), Goods("tools", 2), 5));
        Assert.True(route.Ok, route.Error);
        Assert.False(world.Execute(new AcceptRouteCommand(a.Id, route.CreatedId!.Value)).Ok); // only the partner accepts
        Assert.True(world.Execute(new AcceptRouteCommand(b.Id, route.CreatedId.Value)).Ok);

        sim.RunDays(16); // rounds at days 0, 5, 10, 15: two succeed, then A is out of cloth
        var r = world.Trade.Routes.Single();
        Assert.Equal(2, r.Rounds);
        Assert.True(r.Skipped >= 1);
        Assert.True(world.Execute(new EndRouteCommand(b.Id, r.Id)).Ok);
        Assert.Empty(world.Trade.Routes);
    }

    [Fact]
    public void MerchantsBuyFairOffersSellGoodsAndIgnoreOverpricedOnes()
    {
        var (world, a, _, sim) = TwoTowns();
        // Wait for a merchant to set out for A, then post just before it arrives.
        while (!world.Trade.Caravans.Any(c => c.Kind == CaravanKind.MerchantArriving && c.ToId == a.Id)) sim.RunTicks(TimeScale.TicksPerHour);
        var merchant = world.Trade.Caravans.First(c => c.Kind == CaravanKind.MerchantArriving && c.ToId == a.Id);

        a.Stock.Add("cloth", 20);
        a.Stock.Add("coin", 500);
        var fair = Goods("cloth", 10);
        world.Execute(new PostOfferCommand(a.Id, fair, Goods("coin", Trading.FairSellPrice(world, fair))));
        var greedy = world.Execute(new PostOfferCommand(a.Id, Goods("cloth", 10), Goods("coin", Trading.FairSellPrice(world, fair) * 3)));
        var tools = Goods("tools", 5);
        world.Execute(new PostOfferCommand(a.Id, Goods("coin", Trading.FairBuyPrice(world, tools)), tools));
        int toolsBefore = a.Stock.Get("tools");

        sim.RunTicks(merchant.ArriveTick - world.Time.Tick + TimeScale.TicksPerHour);

        Assert.Equal(new[] { greedy.CreatedId!.Value }, world.Trade.Offers.Where(o => o.SettlementId == a.Id).Select(o => o.Id));
        Assert.True(a.Stock.Get("tools") >= toolsBefore + 5 - 1, "The merchant sold us tools (a little wear allowed)");
        Assert.Contains(world.Events, e => e.Kind == EventKind.Trade && e.Message.StartsWith("A merchant bought 10 cloth"));
    }

    [Fact]
    public void GovernedTownsTradeWithEachOther()
    {
        var world = new GameWorld(42);
        world.Execute(new FoundSettlementCommand("a"));
        world.Execute(new FoundSettlementCommand("b"));
        // Well-stocked towns rarely run short, so they first need each other some time in their first winter.
        var sim = new Simulator(world);
        sim.RunDays(200);
        for (int day = 200; day < 450 && !world.Events.Any(e => e.Message.Contains("accepted your offer")); day++) sim.RunDays(1);

        Assert.True(world.Settlements.All(s => Trading.TradingPost(s) != null), "Both towns build a Trading Post");
        Assert.NotNull(Trading.RoadPath(world, world.Settlements[0], world.Settlements[1])); // and a road between them
        Assert.Contains(world.Events, e => e.Message.Contains("accepted your offer"));
        Assert.True(world.Trade.MerchantVisits > 0);
    }

    [Fact]
    public void CaravansOnTheRoadSurviveSaveAndLoad()
    {
        var (world, a, b, sim) = TwoTowns();
        a.Stock.Add("cloth", 10);
        var offer = world.Execute(new PostOfferCommand(a.Id, Goods("cloth", 10), Goods("coin", 30)));
        world.Execute(new AcceptOfferCommand(b.Id, offer.CreatedId!.Value));
        world.Execute(new ProposeRouteCommand(b.Id, a.Id, Goods("coin", 5), Goods("wood", 5), 3));

        var copy = SaveGame.FromJson(SaveGame.ToJson(world));
        Assert.Equal(world.ComputeStateHash(), copy.ComputeStateHash());
        Assert.Equal(2, copy.Trade.Caravans.Count(c => c.Kind == CaravanKind.Delivery));

        sim.RunDays(20);
        new Simulator(copy).RunDays(20);
        Assert.Equal(world.ComputeStateHash(), copy.ComputeStateHash());
        Assert.Equal(10, copy.Settlements[1].Stock.Get("cloth"));
    }

    [Fact]
    public void PlayersTradingOnlineStayInStep()
    {
        var world = new GameWorld(42);
        world.Execute(new FoundSettlementCommand("alice"));
        world.Execute(new FoundSettlementCommand("bob"));
        foreach (var s in world.Settlements)
        {
            var post = Scenario.Place(world, s, "trading_post");
            Scenario.Finish(world, s, post);
            RoadPlanner.Connect(world, s, post);
            Roads.FinishAllPlans(world, s);
            s.Stock.Add("cloth", 20);
        }
        Scenario.ConnectByRoad(world, world.Settlements[0], world.Settlements[1]);
        world.Settings.SecondsPerGameDay = 10;
        var t0 = DateTimeOffset.UtcNow;
        var host = new WorldHost(world, nowUtc: t0);
        var alice = new LoopbackPeer(host, "alice");
        var bob = new LoopbackPeer(host, "bob");
        alice.Client.Hello();
        bob.Client.Hello();
        host.Update(0, t0);
        alice.Client.Poll();
        bob.Client.Poll();

        int? offerId = null;
        alice.Client.CommandFinished += (_, r) => offerId ??= r.CreatedId;
        alice.Client.Send(new PostOfferCommand(alice.Client.Town!.Id, Goods("cloth", 10), Goods("coin", 40)));
        host.Update(0, t0);
        alice.Client.Poll();
        bob.Client.Poll();
        Assert.NotNull(offerId);
        Assert.Single(bob.Client.World!.Trade.Offers); // bob sees it instantly

        bob.Client.Send(new AcceptOfferCommand(bob.Client.Town!.Id, offerId!.Value));
        for (int i = 0; i < 400; i++) // 20 game days
        {
            host.Update(0.5, t0);
            alice.Client.Poll();
            bob.Client.Poll();
        }

        Assert.Equal(host.World.ComputeStateHash(), alice.Client.World!.ComputeStateHash());
        Assert.Equal(host.World.ComputeStateHash(), bob.Client.World.ComputeStateHash());
        Assert.True(host.World.FindSettlement(bob.Client.Town.Id)!.Stock.Get("cloth") >= 30);
    }
}
