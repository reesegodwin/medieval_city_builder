namespace Sim.Core.Trade;

/// <summary>"Give these goods, want those": a barter, a sale (want coin) or a purchase (give coin).</summary>
public sealed class TradeOffer
{
    public int Id { get; set; }

    /// <summary>The town that posted it; its goods to give are set aside until the offer ends.</summary>
    public int SettlementId { get; set; }

    public SortedDictionary<string, int> Give { get; set; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, int> Want { get; set; } = new(StringComparer.Ordinal);
    public long PostedTick { get; set; }
    public long ExpiresTick { get; set; }
}

/// <summary>A standing agreement: every interval, the proposer sends <see cref="Give"/> and the partner sends <see cref="Want"/>.</summary>
public sealed class TradeRoute
{
    public int Id { get; set; }
    public int FromId { get; set; }
    public int ToId { get; set; }
    public SortedDictionary<string, int> Give { get; set; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, int> Want { get; set; } = new(StringComparer.Ordinal);
    public double IntervalDays { get; set; }

    /// <summary>False while waiting for the partner to accept.</summary>
    public bool Accepted { get; set; }

    public long NextTick { get; set; }
    public int Rounds { get; set; }
    public int Skipped { get; set; }
}

public enum CaravanKind
{
    /// <summary>Goods or coin travelling between two towns.</summary>
    Delivery,

    /// <summary>A merchant on the way to a town's Trading Post; trades when it arrives.</summary>
    MerchantArriving,

    /// <summary>A merchant heading home with what it bought.</summary>
    MerchantLeaving,
}

/// <summary>Carts carrying goods (or coin) across the map. Cargo only reaches its destination when the caravan does.</summary>
public sealed class Caravan
{
    public int Id { get; set; }
    public CaravanKind Kind { get; set; }

    /// <summary>Sending town, or 0 for merchants.</summary>
    public int FromId { get; set; }

    /// <summary>Receiving town, or 0 for merchants heading away.</summary>
    public int ToId { get; set; }

    public SortedDictionary<string, int> Cargo { get; set; } = new(StringComparer.Ordinal);
    public int StartX { get; set; }
    public int StartY { get; set; }
    public int EndX { get; set; }
    public int EndY { get; set; }
    public long DepartTick { get; set; }
    public long ArriveTick { get; set; }

    /// <summary>0 at departure, 1 on arrival.</summary>
    public double Progress(long tick) =>
        ArriveTick <= DepartTick ? 1 : Math.Clamp((tick - DepartTick) / (double)(ArriveTick - DepartTick), 0, 1);
}

/// <summary>All trade state in a world, in id order (deterministic iteration).</summary>
public sealed class TradeBook
{
    public List<TradeOffer> Offers { get; set; } = new();
    public List<TradeRoute> Routes { get; set; } = new();
    public List<Caravan> Caravans { get; set; } = new();

    /// <summary>Tick the next merchant sets out for each town (by settlement id).</summary>
    public SortedDictionary<int, long> NextMerchant { get; set; } = new();

    public int CompletedTrades { get; set; }
    public int MerchantVisits { get; set; }
}
