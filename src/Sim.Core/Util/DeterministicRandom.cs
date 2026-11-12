namespace Sim.Core.Util;

/// <summary>
/// Seedable xoshiro256** generator. Unlike System.Random, its output is guaranteed stable
/// across .NET versions, which the simulation needs for replays and tests.
/// </summary>
public sealed class DeterministicRandom
{
    private ulong _s0, _s1, _s2, _s3;

    public DeterministicRandom(ulong seed)
    {
        ulong x = seed;
        _s0 = Hashing.SplitMix(ref x);
        _s1 = Hashing.SplitMix(ref x);
        _s2 = Hashing.SplitMix(ref x);
        _s3 = Hashing.SplitMix(ref x);
    }

    /// <summary>Summary of the internal state, for determinism checks. Does not advance the generator.</summary>
    public ulong StateFingerprint => _s0 ^ RotL(_s1, 16) ^ RotL(_s2, 32) ^ RotL(_s3, 48);

    public ulong NextULong()
    {
        ulong result = RotL(_s1 * 5, 7) * 9;
        ulong t = _s1 << 17;
        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;
        _s2 ^= t;
        _s3 = RotL(_s3, 45);
        return result;
    }

    /// <summary>Uniform double in [0, 1).</summary>
    public double NextDouble() => (NextULong() >> 11) * (1.0 / (1UL << 53));

    /// <summary>Uniform int in [0, maxExclusive).</summary>
    public int Next(int maxExclusive)
    {
        if (maxExclusive <= 0) throw new ArgumentOutOfRangeException(nameof(maxExclusive));
        return (int)(((NextULong() >> 32) * (ulong)maxExclusive) >> 32);
    }

    /// <summary>Uniform int in [minInclusive, maxExclusive).</summary>
    public int Next(int minInclusive, int maxExclusive) => minInclusive + Next(maxExclusive - minInclusive);

    public bool Chance(double probability) => NextDouble() < probability;

    public T Pick<T>(IReadOnlyList<T> items) => items[Next(items.Count)];

    private static ulong RotL(ulong x, int k) => (x << k) | (x >> (64 - k));
}

public static class Hashing
{
    public static ulong SplitMix(ref ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        ulong z = x;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    public static ulong Mix(ulong a, ulong b)
    {
        ulong x = a ^ (b * 0x9E3779B97F4A7C15UL);
        return SplitMix(ref x);
    }

    public static ulong Hash(ulong seed, int x, int y) => Mix(Mix(seed, (ulong)(uint)x), (ulong)(uint)y);

    /// <summary>Hash to a double in [0, 1).</summary>
    public static double Hash01(ulong seed, int x, int y) => (Hash(seed, x, y) >> 11) * (1.0 / (1UL << 53));
}

/// <summary>Incremental FNV-1a hash used to fingerprint world state (determinism checks).</summary>
public sealed class StateHasher
{
    private ulong _hash = 14695981039346656037UL;

    public ulong Value => _hash;

    public StateHasher Add(ulong value)
    {
        for (int i = 0; i < 8; i++)
        {
            _hash ^= (value >> (i * 8)) & 0xFF;
            _hash *= 1099511628211UL;
        }
        return this;
    }

    public StateHasher Add(long value) => Add((ulong)value);
    public StateHasher Add(int value) => Add((ulong)(uint)value);
    public StateHasher Add(bool value) => Add(value ? 1UL : 0UL);
    public StateHasher Add(double value) => Add((ulong)BitConverter.DoubleToInt64Bits(value));

    public StateHasher Add(string value)
    {
        foreach (char c in value) Add((ulong)c);
        return Add(value.Length);
    }
}
