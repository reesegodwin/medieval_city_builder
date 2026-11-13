namespace Sim.Core.Economy;

/// <summary>Standard ledger keys (taxes and upkeep add a detail, e.g. "taxes:Burghers", "upkeep:Market").</summary>
public static class LedgerKeys
{
    public const string Made = "made";
    public const string Inputs = "inputs";
    public const string Eaten = "eaten";
    public const string Used = "used";
    public const string Building = "building";
    public const string TradeIn = "trade-in";
    public const string TradeOut = "trade-out";

    /// <summary>Goods set aside for open offers (negative), or returned when an offer ends (positive).</summary>
    public const string Offers = "offers";

    public const string Refunds = "refunds";
    public const string Taxes = "taxes";
    public const string Upkeep = "upkeep";

    /// <summary>Food that rotted outside a granary.</summary>
    public const string Spoiled = "spoiled";

    /// <summary>Firewood burned to heat homes in winter.</summary>
    public const string Heating = "heating";

    /// <summary>Goods lost to a disaster, e.g. "disaster:fire".</summary>
    public const string Disaster = "disaster";

    public static string Taxes_(string className) => $"{Taxes}:{className}";
    public static string Upkeep_(string buildingName) => $"{Upkeep}:{buildingName}";
    public static string Disaster_(string what) => $"{Disaster}:{what}";

    /// <summary>The broad category of a key ("taxes:Burghers" is "taxes").</summary>
    public static string Category(string key) => key.Split(':')[0];
}

/// <summary>One day's flows: per resource, per key, the signed amount (positive in, negative out).</summary>
public sealed class LedgerDay
{
    public long Day { get; set; }
    public SortedDictionary<string, SortedDictionary<string, double>> Flows { get; set; } = new(StringComparer.Ordinal);

    public double Get(string resource, string key) =>
        Flows.TryGetValue(resource, out var keys) ? keys.GetValueOrDefault(key) : 0;

    /// <summary>Sum for a resource over keys whose category matches.</summary>
    public double Sum(string resource, Func<string, bool> keyFilter) =>
        Flows.TryGetValue(resource, out var keys) ? keys.Where(kv => keyFilter(kv.Key)).Sum(kv => kv.Value) : 0;
}

/// <summary>
/// A settlement's books: where each good and coin came from and went, per day, for the last
/// <see cref="HistoryDays"/> days. It never changes stock itself; forecasts (<see cref="Forecast"/>) read it.
/// </summary>
public sealed class Ledger
{
    public const int HistoryDays = 30;

    public LedgerDay Today { get; set; } = new();

    /// <summary>Finished days, oldest first.</summary>
    public List<LedgerDay> History { get; set; } = new();

    public void Record(string resource, double amount, string key)
    {
        if (amount == 0) return;
        if (!Today.Flows.TryGetValue(resource, out var keys))
            Today.Flows[resource] = keys = new SortedDictionary<string, double>(StringComparer.Ordinal);
        keys[key] = keys.GetValueOrDefault(key) + amount;
    }

    public void RecordAll(IReadOnlyDictionary<string, int> goods, int sign, string key)
    {
        foreach (var (resource, amount) in goods) Record(resource, sign * amount, key);
    }

    /// <summary>Closes today and starts a new day.</summary>
    public void EndDay(long newDay)
    {
        History.Add(Today);
        if (History.Count > HistoryDays) History.RemoveAt(0);
        Today = new LedgerDay { Day = newDay };
    }

    /// <summary>The most recent finished days (up to <paramref name="days"/>).</summary>
    public IEnumerable<LedgerDay> Recent(int days) => History.Skip(Math.Max(0, History.Count - days));

    /// <summary>Average per day over recent finished days of the flows a filter selects.</summary>
    public double Average(string resource, Func<string, bool> keyFilter, int days = 7)
    {
        var recent = Recent(days).ToList();
        return recent.Count == 0 ? 0 : recent.Sum(d => d.Sum(resource, keyFilter)) / recent.Count;
    }

    /// <summary>Per finished day, oldest first, the sum of the flows a filter selects (for charts).</summary>
    public double[] Series(string resource, Func<string, bool> keyFilter) =>
        History.Select(d => d.Sum(resource, keyFilter)).ToArray();

    /// <summary>
    /// Stock at the end of each finished day, oldest first, worked back from today's stock and each day's
    /// net flow. Only as accurate as the books, which cover every change.
    /// </summary>
    public double[] StockSeries(string resource, int stockNow)
    {
        var result = new double[History.Count];
        double stock = stockNow - Today.Sum(resource, _ => true);
        for (int i = History.Count - 1; i >= 0; i--)
        {
            result[i] = stock;
            stock -= History[i].Sum(resource, _ => true);
        }
        return result;
    }

    /// <summary>Average per day of every key for a resource (for breakdowns like coin income by source).</summary>
    public SortedDictionary<string, double> Breakdown(string resource, int days = 7)
    {
        var recent = Recent(days).ToList();
        var result = new SortedDictionary<string, double>(StringComparer.Ordinal);
        if (recent.Count == 0) return result;
        foreach (var day in recent)
            if (day.Flows.TryGetValue(resource, out var keys))
                foreach (var (key, amount) in keys) result[key] = result.GetValueOrDefault(key) + amount / recent.Count;
        return result;
    }
}

/// <summary>A good's average daily flows, grouped for display.</summary>
public readonly record struct LedgerRow(double Made, double Used, double Traded)
{
    public double Net => Made + Used + Traded;

    /// <summary>Made: production, taxes, refunds. Used: inputs, food, services, building, upkeep, disasters. Traded: trade and offers.</summary>
    public static LedgerRow Of(Ledger ledger, string resource, int days = 7)
    {
        static string Cat(string key) => LedgerKeys.Category(key);
        return new LedgerRow(
            ledger.Average(resource, k => Cat(k) is LedgerKeys.Made or LedgerKeys.Taxes or LedgerKeys.Refunds, days),
            ledger.Average(resource, k => Cat(k) is LedgerKeys.Inputs or LedgerKeys.Eaten or LedgerKeys.Used or LedgerKeys.Building or LedgerKeys.Upkeep or LedgerKeys.Disaster or LedgerKeys.Heating or LedgerKeys.Spoiled, days),
            ledger.Average(resource, k => Cat(k) is LedgerKeys.TradeIn or LedgerKeys.TradeOut or LedgerKeys.Offers, days));
    }
}
