using Sim.Core.Citizens;
using Sim.Core.Commands;
using Sim.Core.Economy;
using Sim.Core.Settlements;
using Sim.Core.Simulation;
using Sim.Core.Systems;

namespace Sim.Core.War;

/// <summary>Raising and standing down soldiers. Soldiers are townsfolk taken from work (the economy pays for war).</summary>
public static class Armies
{
    /// <summary>Squads the settlement's barracks can house.</summary>
    public static int SquadCapacity(GameWorld world, Settlement s) =>
        s.Buildings.Count(b => b.IsOperational && b.Def.Id == "barracks") * world.Data.War.SquadsPerBarracks;

    public static int Soldiers(Settlement s) => s.Squads.Sum(q => q.SoldierIds.Count);

    /// <summary>
    /// Adults free to be called up, best first: those without a job, then haulers and workers on standby (their
    /// work waits), then other workers from the least important jobs, and people running services (markets,
    /// taverns, chapels, wells) last, so a muster doesn't empty the town's services. Never the sick, the general, or
    /// people already serving.
    /// </summary>
    /// <summary>0 = no job, 1 = hauling or on standby, 2 = a job, 3 = a service job (taverns, markets...).</summary>
    private static int Rank(Settlement s, Citizen c)
    {
        if (c.JobBuildingId is not int j || s.FindBuilding(j) is not { } b) return 0;
        if (b.CurrentJobKind == JobKinds.Hauler || s.IsOnStandby(b)) return 1;
        return b.Def.Service != null ? 3 : 2;
    }

    /// <summary>People who could serve without leaving a real job empty: the jobless, haulers and those on standby.</summary>
    public static int SpareHands(Settlement s) =>
        s.Citizens.Count(c => c.CanWork && c.Id != s.General?.CitizenId && Rank(s, c) <= 1);

    public static List<Citizen> Recruitable(Settlement s)
    {
        int Rank(Citizen c) => Armies.Rank(s, c);
        int Priority(Citizen c) => c.JobBuildingId is int j && s.FindBuilding(j)?.CurrentJobKind is { } kind ? s.Rules.PriorityOf(kind) : int.MinValue;
        return s.Citizens.Where(c => c.CanWork && c.Id != s.General?.CitizenId)
            .OrderBy(Rank).ThenBy(Priority).ThenBy(c => c.Id).ToList();
    }

    /// <summary>Takes people from their jobs into a new squad, with gear from stock. Returns the squad, or the reason not.</summary>
    public static (Squad? Squad, string? Error) Raise(GameWorld world, Settlement s, string unitId, int size, bool levy)
    {
        var unit = world.Data.War.Unit(unitId);
        if (unit == null) return (null, "Unknown kind of soldier");
        if (size <= 0) return (null, "A squad needs soldiers");
        // Gear goes to those whose families can afford to keep it up: a rich town fields better soldiers.
        double price = Economy.HouseholdSystem.GearPrice(world, unit);
        var candidates = Recruitable(s);
        bool anyone = candidates.Count > 0;
        if (price > 0) candidates = candidates.Where(c => Economy.HouseholdSystem.FamilyPurse(s, c) >= price).ToList();
        // A levy takes up only the gear there is.
        if (levy)
            foreach (var (r, n) in unit.Gear)
                size = Math.Min(size, s.Stock.Get(r) / n);
        var people = candidates.Take(size).ToList();
        if (people.Count == 0)
            return (null, !anyone ? "Nobody free to serve"
                : price > 0 && candidates.Count == 0 ? $"No family can afford {unit.Name.ToLowerInvariant()}'s gear (needs {price:0.#} coin saved)"
                : $"Needs {string.Join(", ", unit.Gear.Select(kv => $"{kv.Value} {kv.Key}"))} per soldier");
        size = people.Count;
        var gear = unit.Gear.ToDictionary(kv => kv.Key, kv => kv.Value * size);
        if (!s.Stock.TryTakeAll(gear))
            return (null, $"Needs {string.Join(", ", gear.Select(kv => $"{kv.Value} {kv.Key}"))}");
        s.Ledger.RecordAll(gear, -1, LedgerKeys.Used);

        var squad = new Squad { Id = world.NextId(), UnitId = unitId, Levy = levy };
        foreach (var c in people)
        {
            if (c.JobBuildingId is int job) s.FindBuilding(job)?.WorkerIds.Remove(c.Id);
            c.JobBuildingId = null;
            c.SquadId = squad.Id;
            squad.SoldierIds.Add(c.Id);
            c.Remember(world.Time, $"Joined the {unit.Name.ToLowerInvariant()}");
        }
        s.Squads.Add(squad);
        return (squad, null);
    }

    /// <summary>Sends a squad home: its people go back to work and their gear back to stock.</summary>
    public static void Disband(GameWorld world, Settlement s, Squad squad)
    {
        var unit = world.Data.War.Unit(squad.UnitId);
        foreach (int id in squad.SoldierIds)
            if (s.FindCitizen(id) is { } c) c.SquadId = null;
        if (unit != null)
            foreach (var (r, n) in unit.Gear)
            {
                int back = n * squad.SoldierIds.Count;
                if (back <= 0) continue;
                s.Stock.Add(r, back);
                s.Ledger.Record(r, back, LedgerKeys.Refunds);
            }
        s.Squads.Remove(squad);
    }

    /// <summary>A citizen leaving or dying leaves their squad (and the generalship).</summary>
    public static void Forget(Settlement s, Citizen c)
    {
        foreach (var squad in s.Squads) squad.SoldierIds.Remove(c.Id);
        s.Squads.RemoveAll(q => q.SoldierIds.Count == 0);
        if (s.General?.CitizenId == c.Id) s.General = null;
    }

    /// <summary>Coin per day for the army: soldiers' and the general's pay.</summary>
    public static double DailyUpkeep(GameWorld world, Settlement s)
    {
        double factor = world.Data.Households.Government(s.Rules.Government).SoldierPay;
        double pay = s.Squads.Sum(q => (world.Data.War.Unit(q.UnitId)?.PayPerSoldierDay ?? 0) * q.SoldierIds.Count);
        if (s.General != null) pay += world.Data.Households.GeneralPay;
        return pay * factor;
    }
}

/// <summary>Raises a squad of a kind of soldier (needs a barracks with room, people, and their gear).</summary>
public sealed record RecruitSquadCommand(int SettlementId, string UnitId, int Size = 0) : ISettlementCommand
{
    public CommandResult Execute(GameWorld world)
    {
        var s = world.FindSettlement(SettlementId);
        if (s == null) return CommandResult.Fail("Unknown settlement");
        int regular = s.Squads.Count(q => !q.Levy);
        if (regular >= Armies.SquadCapacity(world, s))
            return CommandResult.Fail(Armies.SquadCapacity(world, s) == 0 ? "Needs a barracks" : "The barracks are full");
        var (squad, error) = Armies.Raise(world, s, UnitId, Size > 0 ? Size : world.Data.War.SquadSize, levy: false);
        if (squad == null) return CommandResult.Fail(error!);
        world.Log(Events.EventKind.Military, s, $"Raised a squad of {squad.SoldierIds.Count} {world.Data.War.Unit(UnitId)!.Name.ToLowerInvariant()}");
        return CommandResult.Success(squad.Id);
    }
}

/// <summary>Stands a squad down: people back to work, gear back to stock.</summary>
public sealed record DisbandSquadCommand(int SettlementId, int SquadId) : ISettlementCommand
{
    public CommandResult Execute(GameWorld world)
    {
        var s = world.FindSettlement(SettlementId);
        if (s == null) return CommandResult.Fail("Unknown settlement");
        var squad = s.Squads.FirstOrDefault(q => q.Id == SquadId);
        if (squad == null) return CommandResult.Fail("No such squad");
        Armies.Disband(world, s, squad);
        world.Log(Events.EventKind.Military, s, "A squad was stood down and went back to work");
        return CommandResult.Success();
    }
}

/// <summary>
/// Makes a citizen the settlement's general (replacing any other, who goes back to work). Generals get two traits
/// at random and grow in skill with battles.
/// </summary>
public sealed record PromoteGeneralCommand(int SettlementId, int CitizenId) : ISettlementCommand
{
    public CommandResult Execute(GameWorld world)
    {
        var s = world.FindSettlement(SettlementId);
        if (s == null) return CommandResult.Fail("Unknown settlement");
        var c = s.FindCitizen(CitizenId);
        if (c == null || !c.IsAdult) return CommandResult.Fail("Generals are chosen from the town's adults");
        if (c.SquadId != null) return CommandResult.Fail("Already serving in a squad");
        if (s.General?.CitizenId == c.Id) return CommandResult.Fail("Already the general");

        if (c.JobBuildingId is int job) s.FindBuilding(job)?.WorkerIds.Remove(c.Id);
        c.JobBuildingId = null;
        var traits = world.Data.War.GeneralTraits.ToList();
        var general = new General { CitizenId = c.Id };
        for (int i = 0; i < 2 && traits.Count > 0; i++)
        {
            var t = traits[world.Rng.Next(traits.Count)];
            traits.Remove(t);
            general.Traits.Add(t.Id);
        }
        s.General = general;
        c.Remember(world.Time, "Made general of the town's army");
        world.Log(Events.EventKind.Military, s, $"{c.FullName} is the new general (" +
            string.Join(", ", general.Traits.Select(t => world.Data.War.Trait(t)?.Name ?? t)) + ")");
        return CommandResult.Success();
    }
}
