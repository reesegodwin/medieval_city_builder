namespace Sim.Core.Time;

/// <summary>
/// Global time scale. All gameplay durations are written in game days and converted to ticks,
/// so retuning <see cref="RealSecondsPerGameDay"/> rescales the whole game consistently.
/// </summary>
public static class TimeScale
{
    /// <summary>One tick = 6 game minutes.</summary>
    public const int TicksPerDay = 240;

    public const int TicksPerHour = TicksPerDay / 24;

    public const int DaysPerSeason = 90;
    public const int DaysPerYear = DaysPerSeason * 4;

    /// <summary>1 game day = 1 real hour (design decision; tunable).</summary>
    public const double RealSecondsPerGameDay = 3600;

    public static double RealSecondsPerTick => RealSecondsPerGameDay / TicksPerDay;

    public static long DaysToTicks(double days) => (long)Math.Round(days * TicksPerDay);

    public static double YearsToDays(double years) => years * DaysPerYear;
}

public enum Season
{
    Spring,
    Summer,
    Autumn,
    Winter,
}

/// <summary>An absolute point in game time, measured in ticks since the world was created.</summary>
public readonly record struct GameTime(long Tick)
{
    public long Day => Tick / TimeScale.TicksPerDay;
    public int TickOfDay => (int)(Tick % TimeScale.TicksPerDay);
    public int Year => (int)(Day / TimeScale.DaysPerYear) + 1;
    public int DayOfYear => (int)(Day % TimeScale.DaysPerYear) + 1;
    public Season Season => (Season)((DayOfYear - 1) / TimeScale.DaysPerSeason);

    public bool IsStartOfDay => TickOfDay == 0;
    public bool IsStartOfHour => Tick % TimeScale.TicksPerHour == 0;

    public GameTime Next() => new(Tick + 1);

    public override string ToString() => $"Y{Year} D{DayOfYear} ({Season})";
}
