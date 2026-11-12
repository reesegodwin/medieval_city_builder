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
            h.Add(s.Id).Add(s.Territory.Count).Add(s.TerritoryCap).Add(s.HaulCoverage);
            foreach (var (resource, amount) in s.Stock.Items) h.Add(resource).Add(amount);
            foreach (var b in s.Buildings)
            {
                h.Add(b.Id).Add(b.Def.Id).Add((int)b.State).Add(b.ConstructionProgress).Add(b.CycleProgress);
                foreach (int w in b.WorkerIds) h.Add(w);
                foreach (int r in b.ResidentIds) h.Add(r);
            }
            foreach (var c in s.Citizens)
            {
                h.Add(c.Id).Add(c.AgeDays).Add(c.Satiation).Add(c.Mood)
                    .Add(c.JobBuildingId ?? 0).Add(c.HomeBuildingId ?? 0).Add(c.SpouseId ?? 0);
                foreach (var (kind, skill) in c.Skills) h.Add(kind).Add(skill);
            }
        }
        return h.Value;
    }
}
