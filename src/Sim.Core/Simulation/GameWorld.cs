using Sim.Core.Commands;
using Sim.Core.Data;
using Sim.Core.Events;
using Sim.Core.Settlements;
using Sim.Core.Time;
using Sim.Core.Util;
using Sim.Core.World;

namespace Sim.Core.Simulation;

/// <summary>
/// The root of all simulation state. Everything that changes goes through the simulator's
/// systems or through <see cref="Execute"/> (player / AI commands), so a world can be
/// reproduced exactly from its seed plus the list of commands.
/// </summary>
public sealed class GameWorld
{
    private int _nextId;

    public ulong Seed { get; }
    public GameData Data { get; }
    public WorldMap Map { get; }
    public DeterministicRandom Rng { get; }
    public GameTime Time { get; private set; }

    public List<Settlement> Settlements { get; } = new();
    public List<GameEvent> Events { get; } = new();
    public int TotalDeaths { get; set; }
    public int TotalBirths { get; set; }

    /// <summary>Offers, trade routes and caravans between towns.</summary>
    public Trade.TradeBook Trade { get; set; } = new();

    /// <summary>Warbands on the map and recent battles.</summary>
    public War.WarBook War { get; set; } = new();

    /// <summary>Speed and offline rules, chosen when the world is created.</summary>
    public WorldSettings Settings { get; set; } = new();

    /// <summary>Real UTC time the world was last played (or saved); used to catch up on time away.</summary>
    public DateTimeOffset? LastActiveUtc { get; set; }

    public GameWorld(ulong seed, GameData? data = null)
    {
        Seed = seed;
        Data = data ?? GameData.LoadDefault();
        Map = new WorldMap(seed);
        Rng = new DeterministicRandom(Hashing.Mix(seed, 0xC0FFEE));
    }

    public int NextId() => ++_nextId;

    /// <summary>The last id handed out (saved so loaded worlds keep numbering where they left off).</summary>
    public int LastId => _nextId;

    /// <summary>Used by the save system to restore the clock and id counter.</summary>
    internal void Restore(long tick, int lastId)
    {
        Time = new GameTime(tick);
        _nextId = lastId;
    }

    internal void AdvanceTime() => Time = Time.Next();

    public CommandResult Execute(ICommand command) => command.Execute(this);

    public Settlement? FindSettlement(int id) => Settlements.FirstOrDefault(s => s.Id == id);

    public void Log(EventKind kind, Settlement settlement, string message) =>
        Events.Add(new GameEvent(Time, kind, settlement.Id, message));

    /// <summary>Fingerprint of the simulation state, used to prove runs are deterministic.</summary>
    public ulong ComputeStateHash()
    {
        var h = new StateHasher();
        h.Add(Time.Tick).Add(TotalBirths).Add(TotalDeaths).Add(Events.Count).Add(Rng.StateFingerprint);
        foreach (var s in Settlements)
        {
            h.Add(s.Id).Add(s.Territory.Count).Add(s.TerritoryCap).Add(s.HaulCoverage).Add(s.Roads.Count)
                .Add(s.TaxAccrued).Add(s.UpkeepOwed).Add(s.UpkeepUnpaid)
                .Add(s.Ledger.History.Count).Add(s.Ledger.Today.Flows.Sum(f => f.Value.Values.Sum()))
                .Add(s.Rules.Disasters).Add(s.PoorHarvestDays).Add(s.HeatingOwed)
                .Add(s.Rules.Raids).Add(s.Rules.MusterPercent).Add((int)s.Alert).Add(s.QuietDays);
            foreach (string warning in s.ShortageWarnings) h.Add(warning);
            foreach (var (food, owed) in s.SpoilageOwed) h.Add(food).Add(owed);
            foreach (var plan in s.RoadPlans) h.Add(plan.X).Add(plan.Y).Add(plan.Progress).Add(plan.Tier);
            foreach (var ((x, y), wall) in s.Walls) h.Add(x).Add(y).Add(wall.Tier).Add((int)wall.Piece).Add(wall.Hp);
            foreach (var plan in s.WallPlans) h.Add(plan.X).Add(plan.Y).Add(plan.Tier).Add((int)plan.Piece).Add(plan.Progress);
            foreach (var (x, y, gate) in s.WallRing) h.Add(x).Add(y).Add(gate);
            foreach (var q in s.Squads)
            {
                h.Add(q.Id).Add(q.UnitId).Add(q.Xp).Add(q.Levy);
                foreach (int id in q.SoldierIds) h.Add(id);
            }
            if (s.General is { } g)
            {
                h.Add(g.CitizenId).Add(g.Skill).Add(g.Xp).Add(g.Battles);
                foreach (string t in g.Traits) h.Add(t);
            }
            foreach (var (x, y) in s.Roads)
                if (Map.GetTile(x, y).RoadTier > 0) h.Add(x).Add(y).Add(Map.GetTile(x, y).RoadTier);
            foreach (var (tool, wear) in s.ToolWear) h.Add(tool).Add(wear);
            foreach (var (good, use) in s.GoodsUse) h.Add(good).Add(use);
            foreach (var (resource, amount) in s.Stock.Items) h.Add(resource).Add(amount);
            foreach (var b in s.Buildings)
            {
                h.Add(b.Id).Add(b.Def.Id).Add((int)b.State).Add(b.ConstructionProgress).Add(b.CycleProgress)
                    .Add(b.UpgradeReadyDays).Add(b.NeedsUnmetDays).Add(b.Cold);
                foreach (int w in b.WorkerIds) h.Add(w);
                foreach (int r in b.ResidentIds) h.Add(r);
            }
            foreach (var c in s.Citizens)
            {
                h.Add(c.Id).Add(c.AgeDays).Add(c.Satiation).Add(c.Mood).Add(c.SickDays).Add(c.SquadId ?? 0)
                    .Add(c.JobBuildingId ?? 0).Add(c.HomeBuildingId ?? 0).Add(c.SpouseId ?? 0);
                foreach (var (kind, skill) in c.Skills) h.Add(kind).Add(skill);
            }
        }
        foreach (var w in War.Warbands) h.Add(w.Id).Add(w.TargetId).Add(w.X).Add(w.Y).Add(w.Raiders).Add(w.Archers);
        h.Add(War.Reports.Count);
        var trade = Trade;
        h.Add(trade.CompletedTrades).Add(trade.MerchantVisits);
        foreach (var o in trade.Offers)
        {
            h.Add(o.Id).Add(o.SettlementId).Add(o.ExpiresTick);
            foreach (var (r, n) in o.Give) h.Add(r).Add(n);
            foreach (var (r, n) in o.Want) h.Add(r).Add(-n);
        }
        foreach (var r in trade.Routes) h.Add(r.Id).Add(r.FromId).Add(r.ToId).Add(r.Accepted).Add(r.NextTick).Add(r.Rounds).Add(r.Skipped);
        foreach (var c in trade.Caravans)
        {
            h.Add(c.Id).Add((int)c.Kind).Add(c.FromId).Add(c.ToId).Add(c.ArriveTick).Add(c.Path.Count);
            foreach (var (r, n) in c.Cargo) h.Add(r).Add(n);
        }
        foreach (var (id, tick) in trade.NextMerchant) h.Add(id).Add(tick);
        return h.Value;
    }
}
