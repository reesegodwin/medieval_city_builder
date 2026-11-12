using Sim.Core.Data;

namespace Sim.Core.Settlements;

public enum BuildingState
{
    /// <summary>Placed, but the construction materials have not been delivered yet.</summary>
    AwaitingMaterials,
    UnderConstruction,
    Operational,
}

public sealed class Building
{
    public int Id { get; }
    public BuildingDef Def { get; }
    public int X { get; }
    public int Y { get; }

    public BuildingState State { get; set; }

    /// <summary>Construction labour completed, in worker-days.</summary>
    public double ConstructionProgress { get; set; }

    /// <summary>Progress through the current production cycle, in worker-days.</summary>
    public double CycleProgress { get; set; }

    /// <summary>Whether the inputs for the current production cycle have been taken from stock.</summary>
    public bool InputsLoaded { get; set; }

    /// <summary>Output multiplier from terrain (e.g. fertile soil under a farm).</summary>
    public double TerrainMultiplier { get; set; } = 1.0;

    /// <summary>Efficiency lost to carrying goods to and from the nearest stockpile (0..1, 1 = none lost).</summary>
    public double HaulFactor { get; set; } = 1.0;

    /// <summary>Human-readable reason the building is idle, if any.</summary>
    public string? Problem { get; set; }

    public List<int> WorkerIds { get; } = new();
    public List<int> ResidentIds { get; } = new();

    public Building(int id, BuildingDef def, int x, int y)
    {
        Id = id;
        Def = def;
        X = x;
        Y = y;
    }

    public bool IsOperational => State == BuildingState.Operational;
    public bool IsConstructionSite => State != BuildingState.Operational;

    public int CenterX => X + Def.Width / 2;
    public int CenterY => Y + Def.Height / 2;

    /// <summary>Job slots right now: builders while under construction, the building's jobs once finished.</summary>
    public int JobSlots => State switch
    {
        BuildingState.UnderConstruction => Def.BuilderSlots,
        BuildingState.Operational => Def.Jobs,
        _ => 0,
    };

    /// <summary>The kind of job its workers currently do.</summary>
    public string? CurrentJobKind => State switch
    {
        BuildingState.UnderConstruction => JobKinds.Builder,
        BuildingState.Operational => Def.JobKind,
        _ => null,
    };

    public int FreeHousing => IsOperational ? Def.Housing - ResidentIds.Count : 0;

    public int DistanceTo(Building other) => Math.Abs(CenterX - other.CenterX) + Math.Abs(CenterY - other.CenterY);

    public override string ToString() => $"{Def.Name} #{Id}";
}

public static class JobKinds
{
    public const string Builder = "builder";
}
