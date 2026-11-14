using Sim.Core.Time;

namespace Sim.Core.Simulation;

/// <summary>Who may change how fast a shared world runs.</summary>
public enum SpeedPolicy
{
    /// <summary>Always the world's base speed (typical for dedicated servers).</summary>
    Fixed,

    /// <summary>The host player chooses (host-and-play sessions).</summary>
    Host,

    /// <summary>Everyone connected must ask for a faster speed; the slowest request wins (like Total War battles).</summary>
    Unanimous,
}

/// <summary>What happens to a world while nobody is playing in it.</summary>
public enum EmptyWorldPolicy
{
    /// <summary>Time stops (Minecraft-style).</summary>
    Pause,

    /// <summary>Time stops, but the world catches up on the missed time (up to a cap) when someone comes back.</summary>
    CatchUp,

    /// <summary>Time keeps running (a 24/7 dedicated server).</summary>
    Run,
}

/// <summary>
/// Chosen when a world is created and stored with it, so everyone knows what to expect. The balance never
/// changes between worlds: everything is measured in game days, and only the clock differs.
/// </summary>
public sealed class WorldSettings
{
    /// <summary>Real seconds per game day at normal speed.</summary>
    public double SecondsPerGameDay { get; set; } = TimeScale.RealSecondsPerGameDay;

    public SpeedPolicy SpeedPolicy { get; set; } = SpeedPolicy.Host;

    public EmptyWorldPolicy WhenEmpty { get; set; } = EmptyWorldPolicy.Pause;

    /// <summary>Most game days a world catches up on when someone returns.</summary>
    public double CatchUpCapDays { get; set; } = 7;

    /// <summary>Fastest players may speed the world up to, as a multiple of normal speed.</summary>
    public double MaxSpeedMultiplier { get; set; } = 8;

    /// <summary>
    /// Computer-run (NPC) towns the world starts with; more are founded over time (factions.json). Zero for none.
    /// New worlds made in the game or on a server use factions.json's startTowns; the default here is none.
    /// </summary>
    public int NpcTowns { get; set; }

    public WorldSettings Clone() => (WorldSettings)MemberwiseClone();
}

/// <summary>Named world speeds offered when creating a world.</summary>
public static class WorldSpeeds
{
    /// <summary>(name, real seconds per game day, description).</summary>
    public static readonly (string Name, double SecondsPerDay, string Description)[] Presets =
    {
        ("persistent", 3600, "1 game day per real hour: check in once or twice a day (24/7 servers)"),
        ("weekend", 600, "1 game day per 10 minutes: a world that lasts a few weekends"),
        ("evening", 60, "1 game day per minute: village to castle in an evening or two"),
        ("sprint", 20, "1 game day per 20 seconds: quick sessions and testing"),
    };

    public static double? SecondsPerDay(string name) =>
        Presets.Where(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(p => (double?)p.SecondsPerDay).FirstOrDefault();
}

/// <summary>Converts real time to game time for a world, including catch-up after time away.</summary>
public static class WorldClock
{
    /// <summary>
    /// Ticks owed for real time spent away, under the world's policy. Uses UTC, ignores clocks that went
    /// backwards, and never exceeds the catch-up cap.
    /// </summary>
    public static long CatchUpTicks(WorldSettings settings, DateTimeOffset? lastActiveUtc, DateTimeOffset nowUtc)
    {
        if (settings.WhenEmpty != EmptyWorldPolicy.CatchUp || lastActiveUtc is not { } last) return 0;
        double seconds = (nowUtc - last).TotalSeconds;
        if (seconds <= 0) return 0;
        double days = Math.Min(settings.CatchUpCapDays, seconds / settings.SecondsPerGameDay);
        return TimeScale.DaysToTicks(days);
    }

    /// <summary>Real seconds per simulation tick at a speed multiplier.</summary>
    public static double SecondsPerTick(WorldSettings settings, double multiplier) =>
        settings.SecondsPerGameDay / TimeScale.TicksPerDay / Math.Max(multiplier, 1e-6);
}
