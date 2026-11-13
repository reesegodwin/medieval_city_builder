using Sim.Core.Data;
using Sim.Core.Events;
using Sim.Core.Settlements;
using Sim.Core.Simulation;

namespace Sim.Core.Systems;

/// <summary>
/// Daily, after services are worked out: homes whose residents have had the next tier's needs met for
/// <see cref="UpgradeAfterDays"/> days upgrade in place (paying the cost, keeping a building reserve); homes whose
/// own tier's needs go unmet for <see cref="DowngradeAfterDays"/> days slip back a tier.
/// </summary>
public static class HomeSystem
{
    public const int UpgradeAfterDays = 5;
    public const int DowngradeAfterDays = 15;
    public const int MaxUpgradesPerDay = 4;

    /// <summary>Upgrades leave at least this much wood and stone for building.</summary>
    public const int WoodReserve = 40;
    public const int StoneReserve = 20;

    /// <summary>
    /// Upgrades also leave this many days of upkeep in the treasury (at least <see cref="MinCoinReserve"/>):
    /// spending it on townhouses once left upkeep unpaid, which halved what taverns and markets could serve.
    /// </summary>
    public const int UpkeepDaysReserve = 10;
    public const int MinCoinReserve = 100;

    /// <summary>A family pays for its own upgrade when it has this many times the coin cost saved.</summary>
    public const double FamilyPaysMargin = 2;

    /// <summary>Whether every resident has all these needs covered (and there is at least one).</summary>
    public static bool NeedsMet(Settlement s, Building home, IReadOnlyList<string> needs)
    {
        if (home.ResidentIds.Count == 0) return false;
        foreach (int id in home.ResidentIds)
        {
            if (s.FindCitizen(id) is not { } c) return false;
            foreach (string need in needs)
                if (!c.CoveredNeeds.Contains(need)) return false;
        }
        return true;
    }

    public static void Daily(GameWorld world, Settlement s)
    {
        int upgrades = 0;
        foreach (var home in s.Buildings.ToList())
        {
            if (home.Def.Home is not { } tier || !home.IsOperational) continue;

            // Slipping back: this tier's needs went unmet for too long.
            if (tier.Requires.Count > 0 && !NeedsMet(s, home, tier.Requires))
            {
                if (++home.NeedsUnmetDays >= DowngradeAfterDays && world.Data.HomeBelow(home.Def.Id) is { } below)
                {
                    Change(world, s, home, below);
                    world.Log(EventKind.Shortage, s, $"A {tier.Class} home went back to a {below.Name}: its needs weren't met");
                }
                home.UpgradeReadyDays = 0;
                continue;
            }
            home.NeedsUnmetDays = 0;

            // Moving up: the next tier's needs are met, day after day.
            if (tier.UpgradeTo is not { } nextId) continue;
            var next = world.Data.Building(nextId);
            if (!NeedsMet(s, home, next.Home!.Requires))
            {
                home.UpgradeReadyDays = 0;
                continue;
            }
            home.UpgradeReadyDays++;
            if (!s.Rules.AutoUpgradeHomes || home.UpgradeReadyDays < UpgradeAfterDays || upgrades >= MaxUpgradesPerDay) continue;

            // A family with savings pays the coin part itself (builders' pay); the town only gives materials.
            const string coin = Economy.Inventory.Coin;
            int coinCost = next.Cost.GetValueOrDefault(coin);
            double purse = Economy.HouseholdSystem.Purse(s, home);
            bool familyPays = coinCost > 0 && purse >= coinCost * FamilyPaysMargin;
            var townCost = familyPays ? next.Cost.Where(kv => kv.Key != coin).ToDictionary(kv => kv.Key, kv => kv.Value) : next.Cost;
            if (!CanAfford(world, s, townCost)) continue;

            s.Stock.TryTakeAll(townCost);
            s.Ledger.RecordAll(townCost, -1, Economy.LedgerKeys.Building);
            if (familyPays)
            {
                double keep = 1 - coinCost / purse;
                foreach (int id in home.ResidentIds)
                    if (s.FindCitizen(id) is { IsAdult: true } c) c.Coin *= keep;
            }
            Change(world, s, home, next);
            upgrades++;
            world.Log(EventKind.BuildingCompleted, s, $"A home became a {next.Name}: its people are now {world.Data.ClassOf(next.Home.Class).Name.ToLowerInvariant()}");
        }
    }

    private static bool CanAfford(GameWorld world, Settlement s, IReadOnlyDictionary<string, int> cost) =>
        s.Stock.Has(cost) &&
        s.Stock.Get("wood") - cost.GetValueOrDefault("wood") >= WoodReserve &&
        s.Stock.Get("stone") - cost.GetValueOrDefault("stone") >= StoneReserve &&
        (!cost.ContainsKey(Economy.Inventory.Coin) ||
         s.Stock.Get(Economy.Inventory.Coin) - cost[Economy.Inventory.Coin] >=
         Math.Max(MinCoinReserve, EconomySystem.DailyUpkeep(world, s) * UpkeepDaysReserve));

    /// <summary>Swaps the home's tier in place; people beyond a smaller home's room move out and find another.</summary>
    private static void Change(GameWorld world, Settlement s, Building home, BuildingDef to)
    {
        home.Def = to;
        home.UpgradeReadyDays = 0;
        home.NeedsUnmetDays = 0;
        while (home.ResidentIds.Count > to.Housing)
        {
            int id = home.ResidentIds[^1];
            home.ResidentIds.RemoveAt(home.ResidentIds.Count - 1);
            if (s.FindCitizen(id) is { } c) c.HomeBuildingId = null;
        }
    }

    /// <summary>The class of a citizen: that of their home's tier (peasants if homeless or in an ordinary home).</summary>
    public static SocialClassDef ClassOf(GameWorld world, Settlement s, Citizens.Citizen c) =>
        world.Data.ClassOf(c.HomeBuildingId is int h && s.FindBuilding(h)?.Def.Home is { } home ? home.Class : world.Data.Classes[0].Id);
}
