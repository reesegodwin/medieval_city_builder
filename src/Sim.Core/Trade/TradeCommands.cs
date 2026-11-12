using Sim.Core.Commands;
using Sim.Core.Economy;
using Sim.Core.Events;
using Sim.Core.Settlements;
using Sim.Core.Simulation;
using Sim.Core.Time;

namespace Sim.Core.Trade;

internal static class TradeChecks
{
    /// <summary>The acting town, or why it can't trade.</summary>
    public static (Settlement? Town, string? Error) Trader(GameWorld world, int settlementId)
    {
        var s = world.FindSettlement(settlementId);
        if (s == null) return (null, "Unknown settlement");
        if (Trading.TradingPost(s) == null) return (null, "Build a Trading Post first");
        return (s, null);
    }

    public static string? CheckGoods(GameWorld world, IReadOnlyDictionary<string, int> goods, string side)
    {
        if (goods.Count == 0) return $"Choose something to {side}";
        foreach (var (r, n) in goods)
        {
            if (!world.Data.Resources.ContainsKey(r)) return $"Unknown resource '{r}'";
            if (n <= 0) return "Amounts must be positive";
        }
        return null;
    }

    public static SortedDictionary<string, int> Sorted(IReadOnlyDictionary<string, int> goods) =>
        new(goods.ToDictionary(kv => kv.Key, kv => kv.Value), StringComparer.Ordinal);
}

/// <summary>Posts an offer at the town's Trading Post. The goods to give are set aside until it ends.</summary>
public sealed record PostOfferCommand(int SettlementId, IReadOnlyDictionary<string, int> Give, IReadOnlyDictionary<string, int> Want)
    : ISettlementCommand
{
    public CommandResult Execute(GameWorld world)
    {
        var (s, error) = TradeChecks.Trader(world, SettlementId);
        if (s == null) return CommandResult.Fail(error!);
        error = TradeChecks.CheckGoods(world, Give, "give") ?? TradeChecks.CheckGoods(world, Want, "ask for");
        if (error != null) return CommandResult.Fail(error);
        if (Give.Keys.Intersect(Want.Keys).Any()) return CommandResult.Fail("Can't give and ask for the same thing");
        if (world.Trade.Offers.Count(o => o.SettlementId == s.Id) >= world.Data.Trade.MaxOpenOffers)
            return CommandResult.Fail($"At most {world.Data.Trade.MaxOpenOffers} open offers");
        if (!s.Stock.TryTakeAll(Give)) return CommandResult.Fail($"Not enough in stock to give {Trading.Describe(Give)}");

        var offer = new TradeOffer
        {
            Id = world.NextId(),
            SettlementId = s.Id,
            Give = TradeChecks.Sorted(Give),
            Want = TradeChecks.Sorted(Want),
            PostedTick = world.Time.Tick,
            ExpiresTick = world.Time.Tick + TimeScale.DaysToTicks(world.Data.Trade.OfferDays),
        };
        world.Trade.Offers.Add(offer);
        world.Log(EventKind.Trade, s, $"Offered {Trading.Describe(Give)} for {Trading.Describe(Want)}");
        return CommandResult.Success(offer.Id);
    }
}

public sealed record CancelOfferCommand(int SettlementId, int OfferId) : ISettlementCommand
{
    public CommandResult Execute(GameWorld world)
    {
        var offer = world.Trade.Offers.FirstOrDefault(o => o.Id == OfferId);
        if (offer == null) return CommandResult.Fail("That offer is gone");
        if (offer.SettlementId != SettlementId) return CommandResult.Fail("That isn't your offer");
        Trading.Withdraw(world, offer);
        return CommandResult.Success();
    }
}

/// <summary>Takes another town's offer: both sides' goods leave at once and travel by caravan.</summary>
public sealed record AcceptOfferCommand(int SettlementId, int OfferId) : ISettlementCommand
{
    public CommandResult Execute(GameWorld world)
    {
        var (buyer, error) = TradeChecks.Trader(world, SettlementId);
        if (buyer == null) return CommandResult.Fail(error!);
        var offer = world.Trade.Offers.FirstOrDefault(o => o.Id == OfferId);
        if (offer == null) return CommandResult.Fail("That offer is gone");
        if (offer.SettlementId == buyer.Id) return CommandResult.Fail("That's your own offer");
        var seller = world.FindSettlement(offer.SettlementId);
        if (seller == null) return CommandResult.Fail("That town is gone");
        var path = Trading.RoadPath(world, seller, buyer);
        if (path == null) return CommandResult.Fail(Trading.NoRoadMessage(seller, buyer));
        if (!buyer.Stock.TryTakeAll(offer.Want)) return CommandResult.Fail($"You need {Trading.Describe(offer.Want)}");

        world.Trade.Offers.Remove(offer);
        Trading.Send(world, seller, buyer, offer.Give, path);
        Trading.Send(world, buyer, seller, offer.Want, path.AsEnumerable().Reverse().ToList());
        world.Trade.CompletedTrades++;
        world.Log(EventKind.Trade, seller, $"{buyer.Name} accepted your offer of {Trading.Describe(offer.Give)}; caravans are on the way");
        world.Log(EventKind.Trade, buyer, $"Bought {Trading.Describe(offer.Give)} from {seller.Name}; caravans are on the way");
        return CommandResult.Success();
    }
}

/// <summary>Proposes a standing route to another town: every interval each side sends its part.</summary>
public sealed record ProposeRouteCommand(int SettlementId, int PartnerId, IReadOnlyDictionary<string, int> Give,
    IReadOnlyDictionary<string, int> Want, double IntervalDays) : ISettlementCommand
{
    public CommandResult Execute(GameWorld world)
    {
        var (s, error) = TradeChecks.Trader(world, SettlementId);
        if (s == null) return CommandResult.Fail(error!);
        if (PartnerId == s.Id || world.FindSettlement(PartnerId) == null) return CommandResult.Fail("Choose another town");
        error = TradeChecks.CheckGoods(world, Give, "send") ?? TradeChecks.CheckGoods(world, Want, "receive");
        if (error != null) return CommandResult.Fail(error);
        if (IntervalDays < 1) return CommandResult.Fail("Routes run at most once a day");
        if (world.Trade.Routes.Count(r => r.FromId == s.Id || r.ToId == s.Id) >= world.Data.Trade.MaxRoutes)
            return CommandResult.Fail($"At most {world.Data.Trade.MaxRoutes} trade routes");

        var route = new TradeRoute
        {
            Id = world.NextId(),
            FromId = s.Id,
            ToId = PartnerId,
            Give = TradeChecks.Sorted(Give),
            Want = TradeChecks.Sorted(Want),
            IntervalDays = IntervalDays,
        };
        world.Trade.Routes.Add(route);
        world.Log(EventKind.Trade, s, $"Proposed a trade route: {Trading.Describe(Give)} for {Trading.Describe(Want)} every {IntervalDays:0.#} days");
        return CommandResult.Success(route.Id);
    }
}

public sealed record AcceptRouteCommand(int SettlementId, int RouteId) : ISettlementCommand
{
    public CommandResult Execute(GameWorld world)
    {
        var (s, error) = TradeChecks.Trader(world, SettlementId);
        if (s == null) return CommandResult.Fail(error!);
        var route = world.Trade.Routes.FirstOrDefault(r => r.Id == RouteId);
        if (route == null) return CommandResult.Fail("That route is gone");
        if (route.ToId != s.Id) return CommandResult.Fail("That route wasn't proposed to you");
        if (route.Accepted) return CommandResult.Fail("Already running");
        route.Accepted = true;
        route.NextTick = world.Time.Tick;
        if (world.FindSettlement(route.FromId) is { } from)
            world.Log(EventKind.Trade, from, $"{s.Name} accepted your trade route");
        return CommandResult.Success();
    }
}

/// <summary>Ends (or declines) a route; either side may.</summary>
public sealed record EndRouteCommand(int SettlementId, int RouteId) : ISettlementCommand
{
    public CommandResult Execute(GameWorld world)
    {
        var route = world.Trade.Routes.FirstOrDefault(r => r.Id == RouteId);
        if (route == null) return CommandResult.Fail("That route is gone");
        if (route.FromId != SettlementId && route.ToId != SettlementId) return CommandResult.Fail("That isn't your route");
        world.Trade.Routes.Remove(route);
        return CommandResult.Success();
    }
}
