using Sim.Core.Time;

namespace Sim.Core.Events;

public enum EventKind
{
    SettlementFounded,
    BuildingPlaced,
    BuildingCompleted,
    BuildingDemolished,
    Birth,
    Death,
    Marriage,
    Arrival,
    Departure,
    Starvation,
    Shortage,
    Trade,
    Disaster,
}

/// <summary>
/// Something notable that happened. The event log later feeds the faction chronicle and
/// out-of-game notifications (Discord / email).
/// </summary>
public sealed record GameEvent(GameTime Time, EventKind Kind, int SettlementId, string Message);
