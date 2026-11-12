using Sim.Core.Economy;
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

    public static void Daily(GameWorld world, Settlement s)
    {
        if (Trading.TradingPost(s) == null) return;
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
