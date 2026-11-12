namespace Sim.Core.Economy;

/// <summary>A settlement's pooled stock of goods. Sorted keys keep iteration deterministic.</summary>
public sealed class Inventory
{
    private readonly SortedDictionary<string, int> _amounts = new(StringComparer.Ordinal);

    public IEnumerable<KeyValuePair<string, int>> Items => _amounts;

    public int Total => _amounts.Values.Sum();

    public int Get(string resource) => _amounts.TryGetValue(resource, out int amount) ? amount : 0;

    public void Add(string resource, int amount)
    {
        if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount));
        if (amount == 0) return;
        _amounts[resource] = Get(resource) + amount;
    }

    public bool TryTake(string resource, int amount)
    {
        if (Get(resource) < amount) return false;
        _amounts[resource] = Get(resource) - amount;
        return true;
    }

    public bool Has(IReadOnlyDictionary<string, int> cost) => cost.All(kv => Get(kv.Key) >= kv.Value);

    public bool TryTakeAll(IReadOnlyDictionary<string, int> cost)
    {
        if (!Has(cost)) return false;
        foreach (var (resource, amount) in cost) _amounts[resource] = Get(resource) - amount;
        return true;
    }
}
