using Sim.Core.Economy;
using Sim.Core.Events;
using Sim.Core.Settlements;
using Sim.Core.Simulation;
using Sim.Core.Time;

namespace Sim.Core.Trade;

/// <summary>Prices, travel times and the bookkeeping behind trade.</summary>
public static class Trading
{
    public static string Describe(IReadOnlyDictionary<string, int> goods) =>
        goods.Count == 0 ? "nothing" : string.Join(", ", goods.Select(kv => $"{kv.Value} {kv.Key}"));

    /// <summary>Fair value in coin of some goods (coin counts at face value).</summary>
    public static double Value(GameWorld world, IReadOnlyDictionary<string, int> goods) =>
        goods.Sum(kv => kv.Value * (world.Data.Resources.GetValueOrDefault(kv.Key)?.Value ?? 0));

    /// <summary>What a seller asks at a fair price (what merchants pay).</summary>
    public static int FairSellPrice(GameWorld world, IReadOnlyDictionary<string, int> goods) =>
        Math.Max(1, (int)Math.Round(Value(world, goods)));

    /// <summary>What a buyer pays at a fair price (what merchants charge).</summary>
    public static int FairBuyPrice(GameWorld world, IReadOnlyDictionary<string, int> goods) =>
        Math.Max(1, (int)Math.Ceiling(Value(world, goods) * world.Data.Trade.MerchantMarkup));

    public static bool IsOnlyCoin(IReadOnlyDictionary<string, int> goods) => goods.Count == 1 && goods.ContainsKey(Inventory.Coin);

    public static Building? TradingPost(Settlement s) => s.Buildings.FirstOrDefault(b => b.Def.Trade && b.IsOperational);

    /// <summary>Where caravans leave from and arrive at: the Trading Post's door, or the town centre.</summary>
    public static (int X, int Y) Depot(Settlement s) => TradingPost(s) is { } post ? post.DoorTile : (s.CenterX, s.CenterY);

    public static long TravelTicks(GameWorld world, (int X, int Y) from, (int X, int Y) to)
    {
        int distance = Math.Abs(from.X - to.X) + Math.Abs(from.Y - to.Y);
        double days = Math.Max(1, distance / world.Data.Trade.CaravanTilesPerDay);
        return TimeScale.DaysToTicks(days);
    }

    /// <summary>Starts a caravan from one town to another carrying the cargo (already taken from the sender).</summary>
    public static Caravan Send(GameWorld world, Settlement from, Settlement to, IReadOnlyDictionary<string, int> cargo)
    {
        var start = Depot(from);
        var end = Depot(to);
        var caravan = new Caravan
        {
            Id = world.NextId(),
            Kind = CaravanKind.Delivery,
            FromId = from.Id,
            ToId = to.Id,
            Cargo = new SortedDictionary<string, int>(cargo.ToDictionary(kv => kv.Key, kv => kv.Value), StringComparer.Ordinal),
            StartX = start.X, StartY = start.Y, EndX = end.X, EndY = end.Y,
            DepartTick = world.Time.Tick,
            ArriveTick = world.Time.Tick + TravelTicks(world, start, end),
        };
        world.Trade.Caravans.Add(caravan);
        return caravan;
    }

    public static void AddAll(Settlement s, IReadOnlyDictionary<string, int> goods)
    {
        foreach (var (resource, amount) in goods) s.Stock.Add(resource, amount);
    }

    /// <summary>Ends an offer that wasn't taken: its set-aside goods go back to the town.</summary>
    public static void Withdraw(GameWorld world, TradeOffer offer)
    {
        world.Trade.Offers.Remove(offer);
        if (world.FindSettlement(offer.SettlementId) is { } owner) AddAll(owner, offer.Give);
    }

    /// <summary>Where merchants come from for a town: outside its land, in a direction fixed by its id.</summary>
    public static (int X, int Y) MerchantOrigin(GameWorld world, Settlement s)
    {
        int d = world.Data.Trade.MerchantDistance + s.TerritoryExtent;
        return (s.Id % 4) switch
        {
            0 => (s.CenterX + d, s.CenterY),
            1 => (s.CenterX, s.CenterY + d),
            2 => (s.CenterX - d, s.CenterY),
            _ => (s.CenterX, s.CenterY - d),
        };
    }
}

/// <summary>
/// Hourly, once per world: expires offers, delivers arriving caravans, runs trade routes that are due, and sends
/// merchants to towns with a staffed Trading Post.
/// </summary>
public static class TradeSystem
{
    public static void Hourly(GameWorld world)
    {
        var book = world.Trade;
        long now = world.Time.Tick;

        foreach (var offer in book.Offers.Where(o => o.ExpiresTick <= now).ToList())
        {
            Trading.Withdraw(world, offer);
            if (world.FindSettlement(offer.SettlementId) is { } owner)
                world.Log(EventKind.Trade, owner, $"Offer of {Trading.Describe(offer.Give)} expired unsold; goods returned");
        }

        foreach (var caravan in book.Caravans.Where(c => c.ArriveTick <= now).ToList())
        {
            book.Caravans.Remove(caravan);
            Arrive(world, caravan);
        }

        foreach (var route in book.Routes.Where(r => r.Accepted && r.NextTick <= now).ToList()) RunRoute(world, route);

        ScheduleMerchants(world);
    }

    private static void Arrive(GameWorld world, Caravan caravan)
    {
        switch (caravan.Kind)
        {
            case CaravanKind.Delivery:
                if (world.FindSettlement(caravan.ToId) is { } to)
                {
                    Trading.AddAll(to, caravan.Cargo);
                    world.Log(EventKind.Trade, to, $"Caravan arrived with {Trading.Describe(caravan.Cargo)}");
                }
                break;
            case CaravanKind.MerchantArriving:
                if (world.FindSettlement(caravan.ToId) is { } town) MerchantTrades(world, town, caravan);
                break;
        }
    }

    private static void RunRoute(GameWorld world, TradeRoute route)
    {
        route.NextTick += TimeScale.DaysToTicks(route.IntervalDays);
        var from = world.FindSettlement(route.FromId);
        var to = world.FindSettlement(route.ToId);
        if (from == null || to == null)
        {
            world.Trade.Routes.Remove(route);
            return;
        }
        if (!from.Stock.Has(route.Give) || !to.Stock.Has(route.Want))
        {
            route.Skipped++;
            var shortTown = !from.Stock.Has(route.Give) ? from : to;
            world.Log(EventKind.Trade, from, $"Trade route with {(shortTown == from ? to.Name : from.Name)} skipped: {shortTown.Name} is short");
            return;
        }
        from.Stock.TryTakeAll(route.Give);
        to.Stock.TryTakeAll(route.Want);
        Trading.Send(world, from, to, route.Give);
        Trading.Send(world, to, from, route.Want);
        route.Rounds++;
        world.Trade.CompletedTrades++;
    }

    private static void ScheduleMerchants(GameWorld world)
    {
        var book = world.Trade;
        long now = world.Time.Tick;
        foreach (var s in world.Settlements)
        {
            var post = Trading.TradingPost(s);
            if (post == null || post.WorkerIds.Count == 0) continue;
            if (!book.NextMerchant.TryGetValue(s.Id, out long next))
            {
                book.NextMerchant[s.Id] = now + TimeScale.DaysToTicks(world.Data.Trade.MerchantVisitDays / 2);
                continue;
            }
            if (next > now) continue;
            book.NextMerchant[s.Id] = now + TimeScale.DaysToTicks(world.Data.Trade.MerchantVisitDays);

            var origin = Trading.MerchantOrigin(world, s);
            var door = Trading.Depot(s);
            book.Caravans.Add(new Caravan
            {
                Id = world.NextId(),
                Kind = CaravanKind.MerchantArriving,
                ToId = s.Id,
                StartX = origin.X, StartY = origin.Y, EndX = door.X, EndY = door.Y,
                DepartTick = now,
                ArriveTick = now + Trading.TravelTicks(world, origin, door),
            });
        }
    }

    /// <summary>
    /// A merchant at a town's Trading Post buys the town's sale offers priced at or below the fair price and
    /// fills its purchase offers paying at least the fair price plus markup, within its purse and cargo.
    /// </summary>
    private static void MerchantTrades(GameWorld world, Settlement town, Caravan merchant)
    {
        var rules = world.Data.Trade;
        int purse = rules.MerchantPurse;
        int cargo = rules.MerchantCargo;
        var bought = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var deals = new List<string>();

        foreach (var offer in world.Trade.Offers.Where(o => o.SettlementId == town.Id).ToList())
        {
            if (Trading.IsOnlyCoin(offer.Want) && !offer.Give.ContainsKey(Inventory.Coin))
            {
                int price = offer.Want[Inventory.Coin];
                if (price > Trading.FairSellPrice(world, offer.Give) || price > purse) continue;
                purse -= price;
                town.Stock.Add(Inventory.Coin, price);
                foreach (var (r, n) in offer.Give) bought[r] = bought.GetValueOrDefault(r) + n;
                world.Trade.Offers.Remove(offer);
                deals.Add($"bought {Trading.Describe(offer.Give)} for {price} coin");
            }
            else if (Trading.IsOnlyCoin(offer.Give) && offer.Want.Keys.All(rules.MerchantGoods.Contains))
            {
                int paid = offer.Give[Inventory.Coin];
                int units = offer.Want.Values.Sum();
                if (paid < Trading.FairBuyPrice(world, offer.Want) || units > cargo) continue;
                cargo -= units;
                Trading.AddAll(town, offer.Want);
                bought[Inventory.Coin] = bought.GetValueOrDefault(Inventory.Coin) + paid;
                world.Trade.Offers.Remove(offer);
                deals.Add($"sold {Trading.Describe(offer.Want)} for {paid} coin");
            }
        }

        world.Trade.MerchantVisits++;
        world.Trade.CompletedTrades += deals.Count;
        world.Log(EventKind.Trade, town, deals.Count == 0
            ? "A merchant visited but found nothing to trade"
            : $"A merchant {string.Join("; ", deals)}");

        // The merchant heads home with what it bought.
        var home = (merchant.StartX, merchant.StartY);
        world.Trade.Caravans.Add(new Caravan
        {
            Id = world.NextId(),
            Kind = CaravanKind.MerchantLeaving,
            FromId = town.Id,
            Cargo = bought,
            StartX = merchant.EndX, StartY = merchant.EndY, EndX = home.Item1, EndY = home.Item2,
            DepartTick = world.Time.Tick,
            ArriveTick = world.Time.Tick + Trading.TravelTicks(world, (merchant.EndX, merchant.EndY), home),
        });
    }
}
