using Sim.Core.Commands;
using Sim.Core.Economy;
using Sim.Core.World;
using Sim.Core.Settlements;
using Sim.Core.Simulation;
using Sim.Core.Trade;

namespace Sim.Core.AI;

/// <summary>
/// How governed (Auto) towns trade: they sell surpluses at the fair price, buy what they're short of (from other
/// towns' offers, or from merchants with buy offers), and accept trade routes that help them. They use the same
/// commands a player would.
/// </summary>
public static class TradeGovernor
{
    /// <summary>Goods bought per purchase offer, at most.</summary>
    public const int BuyAmount = 20;

    /// <summary>Smallest purchase worth posting.</summary>
    public const int MinBuyAmount = 5;

    /// <summary>A surplus is stock at or above this share of the stock limit.</summary>
    public const double SurplusShare = 0.9;

    /// <summary>Days between attempts to plan a road to the same trading partner.</summary>
    public const int RoadRetryDays = 10;

    /// <summary>Most tiles searched when planning a road to another town.</summary>
    public const int MaxRoadPlanSearch = 300_000;

    /// <summary>
    /// Caravans need roads, so a governed town plans a road towards the nearest town with a Trading Post that its
    /// roads don't reach yet: to that town's roads, or to the edge of its territory (which that town builds on from).
    /// </summary>
    private static void ConnectToPartner(GameWorld world, Settlement s)
    {
        if (s.RoadPlans.Count > 0) return; // still building the last one
        var ends = Trading.RoadEnds(world, s);
        if (ends.Count == 0) return;

        var partner = world.Settlements
            .Where(o => o != s && Trading.TradingPost(o) != null)
            .OrderBy(o => Math.Abs(o.CenterX - s.CenterX) + Math.Abs(o.CenterY - s.CenterY)).ThenBy(o => o.Id)
            .FirstOrDefault(o => Trading.RoadPath(world, s, o) == null);
        if (partner == null) return;

        string key = $"road@{partner.Id}";
        long today = world.Time.Day;
        if (s.FailedSiteSearches.TryGetValue(key, out long tried) && today - tried < RoadRetryDays) return;
        s.FailedSiteSearches[key] = today;

        var theirRoads = RoadNetwork(world, Trading.RoadEnds(world, partner));
        bool AtPartner(int x, int y)
        {
            if (theirRoads.Contains((x, y))) return true;
            var map = world.Map;
            return map.GetTile(x + 1, y).TerritoryId == partner.Id || map.GetTile(x - 1, y).TerritoryId == partner.Id ||
                   map.GetTile(x, y + 1).TerritoryId == partner.Id || map.GetTile(x, y - 1).TerritoryId == partner.Id;
        }
        bool Blocked(int x, int y) =>
            world.Map.GetTile(x, y) is { IsRoad: false, RoadPlanned: false } && Roads.Check(world, s, x, y) != null;

        var path = Pathfinder.FindPathTowards(world.Map, ends[0], AtPartner, Trading.Depot(partner),
            Pathfinder.Mode.BuildRoad, MaxRoadPlanSearch, Blocked);
        if (path == null) return;
        var tiles = path.Where(t => world.Map.GetTile(t.X, t.Y) is { IsRoad: false, RoadPlanned: false }).ToList();
        if (tiles.Count > 0) world.Execute(new PlaceRoadCommand(s.Id, tiles));
    }

    /// <summary>Road tiles joined to the given starting tiles (breadth-first).</summary>
    private static HashSet<(int X, int Y)> RoadNetwork(GameWorld world, IEnumerable<(int X, int Y)> starts)
    {
        var seen = new HashSet<(int X, int Y)>(starts);
        var queue = new Queue<(int X, int Y)>(seen);
        while (queue.Count > 0 && seen.Count < Trading.MaxRoadSearch)
        {
            var (x, y) = queue.Dequeue();
            foreach (var n in new[] { (x, y - 1), (x + 1, y), (x, y + 1), (x - 1, y) })
                if (world.Map.GetTile(n.Item1, n.Item2).IsRoad && seen.Add(n)) queue.Enqueue(n);
        }
        return seen;
    }

    public static void Daily(GameWorld world, Settlement s)
    {
        if (Trading.TradingPost(s) == null) return;
        ConnectToPartner(world, s);
        var rules = world.Data.Trade;
        var offers = world.Trade.Offers;

        bool Used(string r)
        {
            var def = world.Data.Resources.GetValueOrDefault(r);
            if (def == null || r == Inventory.Coin) return false;
            return r is "wood" or "stone" || def.IsFood || def.IsTool ||
                   world.Data.Needs.Any(n => n.Goods.ContainsKey(r)) ||
                   s.Buildings.Any(b => b.Def.Recipe?.Inputs.ContainsKey(r) == true);
        }
        bool Short(string r) => Used(r) && s.Stock.Get(r) < s.Rules.LowStockThreshold;
        bool Surplus(string r)
        {
            int limit = s.LimitOf(r);
            return r != Inventory.Coin && limit != int.MaxValue && s.Stock.Get(r) >= limit * SurplusShare;
        }

        // 1. Take one other town's offer that brings something we're short of, at a fair price.
        foreach (var offer in offers.Where(o => o.SettlementId != s.Id).ToList())
        {
            if (!offer.Give.Keys.All(Short) || !s.Stock.Has(offer.Want)) continue;
            bool fair = Trading.IsOnlyCoin(offer.Want)
                ? offer.Want[Inventory.Coin] <= Trading.FairBuyPrice(world, offer.Give)
                : offer.Want.Keys.All(Surplus) && Trading.Value(world, offer.Want) <= Trading.FairBuyPrice(world, offer.Give);
            if (fair && world.Execute(new AcceptOfferCommand(s.Id, offer.Id)).Ok) break;
        }

        // 2. Accept routes proposed to us that bring shortages for surpluses.
        foreach (var route in world.Trade.Routes.Where(r => r.ToId == s.Id && !r.Accepted).ToList())
        {
            if (route.Give.Keys.Any(Short) && route.Want.Keys.All(Surplus)) world.Execute(new AcceptRouteCommand(s.Id, route.Id));
        }

        int open = offers.Count(o => o.SettlementId == s.Id);
        // One open offer per good, whether buying or selling it, so a town never buys what it's selling.
        bool HasOffer(string r) => offers.Any(o => o.SettlementId == s.Id && (o.Give.ContainsKey(r) || o.Want.ContainsKey(r)));

        // 3. Sell surpluses at the fair price.
        foreach (var def in world.Data.Resources.Values.OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            if (open >= rules.MaxOpenOffers - 2) break;
            string r = def.Id;
            if (def.Value <= 0 || !Surplus(r) || HasOffer(r)) continue;
            var give = new Dictionary<string, int> { [r] = Math.Max(10, s.Stock.Get(r) / 5) };
            var want = new Dictionary<string, int> { [Inventory.Coin] = Trading.FairSellPrice(world, give) };
            if (world.Execute(new PostOfferCommand(s.Id, give, want)).Ok) open++;
        }

        // 4. Buy shortages from merchants (or anyone) at the fair price plus markup, as far as our coin goes.
        foreach (string r in rules.MerchantGoods)
        {
            if (open >= rules.MaxOpenOffers) break;
            if (!Short(r) || HasOffer(r)) continue;
            double unitPrice = world.Data.Resources[r].Value * rules.MerchantMarkup;
            int amount = Math.Min(BuyAmount, (int)(s.Stock.Get(Inventory.Coin) / Math.Max(unitPrice, 0.01)));
            if (amount < MinBuyAmount) continue;
            var want = new Dictionary<string, int> { [r] = amount };
            var give = new Dictionary<string, int> { [Inventory.Coin] = Trading.FairBuyPrice(world, want) };
            if (world.Execute(new PostOfferCommand(s.Id, give, want)).Ok) open++;
        }
    }
}
