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
    /// <summary>What this building is. Homes change it in place when they upgrade or downgrade.</summary>
    public BuildingDef Def { get; internal set; }

    /// <summary>Days in a row the residents have had the next home tier's needs met.</summary>
    public int UpgradeReadyDays { get; set; }

    /// <summary>Days in a row this home's own tier's needs have gone unmet.</summary>
    public int NeedsUnmetDays { get; set; }
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

    /// <summary>True when no walkable route leads from this building to a stockpile.</summary>
    public bool NoRoute { get; set; }

    /// <summary>People this service building served at the last daily check.</summary>
    public int ServedCount { get; set; }

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

    public bool Covers(int x, int y) => x >= X && x < X + Def.Width && y >= Y && y < Y + Def.Height;

    /// <summary>Walkable tiles directly beside the footprint (4-neighbours), where goods are carried in and out.</summary>
    public IEnumerable<(int X, int Y)> AccessTiles()
    {
        for (int x = X; x < X + Def.Width; x++)
        {
            yield return (x, Y - 1);
            yield return (x, Y + Def.Height);
        }
        for (int y = Y; y < Y + Def.Height; y++)
        {
            yield return (X - 1, y);
            yield return (X + Def.Width, y);
        }
    }

    /// <summary>The tile in front of the door (middle of the south side).</summary>
    public (int X, int Y) DoorTile => (X + Def.Width / 2, Y + Def.Height);

    public override string ToString() => $"{Def.Name} #{Id}";
}

public static class JobKinds
{
    public const string Builder = "builder";

    /// <summary>Carries goods between stockpiles and workplaces, so the workers there don't have to.</summary>
    public const string Hauler = "hauler";

    /// <summary>Builds the settlement's planned roads; based at the town centre.</summary>
    public const string Roadworker = "roadworker";
}
