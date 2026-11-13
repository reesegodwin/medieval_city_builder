using Sim.Core.Events;
using Sim.Core.Settlements;
using Sim.Core.Simulation;

namespace Sim.Core.Economy;

/// <summary>
/// How long goods will last at the current rate, from the ledger's recent net flows, and the warnings that
/// go to the player when something is about to run out.
/// </summary>
public static class Forecast
{
    /// <summary>Days of history the rate is averaged over.</summary>
    public const int RateDays = 7;

    /// <summary>Coin comes in lumps (merchants visit every 10 days), so its rate is averaged over longer.</summary>
    public const int CoinRateDays = 14;

    /// <summary>Warn when a watched good will run out within this many days...</summary>
    public const double WarnDays = 10;

    /// <summary>...and don't warn again until it has recovered past this.</summary>
    public const double ClearDays = 20;

    /// <summary>Pseudo-resource for all food together.</summary>
    public const string Food = "food";

    /// <summary>Goods worth warning about, plus all food together.</summary>
    public static readonly string[] Watched = { Food, "wood", "stone", "tools", Inventory.Coin };

    /// <summary>Days of books needed before forecasting; the first days of a town are too uneven.</summary>
    public const int MinHistoryDays = 3;

    /// <summary>Average net change per day over the last week (negative when falling). Food counts nutrition.</summary>
    public static double NetPerDay(GameWorld world, Settlement s, string resource) =>
        resource == Food
            ? world.Data.FoodsByPreference.Sum(f => s.Ledger.Average(f.Id, _ => true, RateDays) * f.Nutrition)
            : s.Ledger.Average(resource, _ => true, resource == Inventory.Coin ? CoinRateDays : RateDays);

    /// <summary>Days until a good runs out at the current rate; infinity when it isn't falling.</summary>
    public static double DaysLeft(GameWorld world, Settlement s, string resource)
    {
        if (s.Ledger.History.Count < MinHistoryDays) return double.PositiveInfinity;
        double net = NetPerDay(world, s, resource);
        if (net >= -1e-6) return double.PositiveInfinity;
        return Stock(world, s, resource) / -net;
    }

    /// <summary>Stock in the same units as <see cref="NetPerDay"/> (nutrition for food).</summary>
    public static double Stock(GameWorld world, Settlement s, string resource) =>
        resource == Food
            ? world.Data.FoodsByPreference.Sum(f => s.Stock.Get(f.Id) * f.Nutrition)
            : s.Stock.Get(resource);

    public static string NameOf(GameWorld world, string resource) =>
        resource == Food ? "Food" : world.Data.Resources.GetValueOrDefault(resource)?.Name ?? resource;

    /// <summary>Daily: warn once when a watched good is about to run out, again only after it recovered.</summary>
    public static void Daily(GameWorld world, Settlement s)
    {
        foreach (string resource in Watched)
        {
            double days = DaysLeft(world, s, resource);
            bool warned = s.ShortageWarnings.Contains(resource);
            if (!warned && days < WarnDays && Stock(world, s, resource) >= 1)
            {
                s.ShortageWarnings.Add(resource);
                world.Log(EventKind.Shortage, s,
                    $"{NameOf(world, resource)} runs out in {Math.Max(1, Math.Floor(days)):0} days at the current rate");
            }
            else if (warned && days > ClearDays)
            {
                s.ShortageWarnings.Remove(resource);
            }
        }
    }
}
