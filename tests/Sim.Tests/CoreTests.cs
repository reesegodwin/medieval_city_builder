using Sim.Core.Economy;
using Sim.Core.Time;
using Sim.Core.Util;
using Sim.Core.World;

namespace Sim.Tests;

public class TimeTests
{
    [Fact]
    public void OneGameDayIsOneRealHour()
    {
        Assert.Equal(3600, TimeScale.RealSecondsPerGameDay);
        Assert.Equal(15, TimeScale.RealSecondsPerTick);
    }

    [Fact]
    public void GameTimeTracksDaysSeasonsAndYears()
    {
        var t = new GameTime(TimeScale.DaysToTicks(TimeScale.DaysPerYear + TimeScale.DaysPerSeason));
        Assert.Equal(2, t.Year);
        Assert.Equal(Season.Summer, t.Season);
        Assert.True(t.IsStartOfDay);
        Assert.Equal(TimeScale.TicksPerDay / 2, new GameTime(TimeScale.DaysToTicks(0.5)).Tick);
    }
}

public class RandomTests
{
    [Fact]
    public void SameSeedGivesSameSequence()
    {
        var a = new DeterministicRandom(123);
        var b = new DeterministicRandom(123);
        for (int i = 0; i < 100; i++) Assert.Equal(a.NextULong(), b.NextULong());
    }

    [Fact]
    public void NextStaysInRange()
    {
        var rng = new DeterministicRandom(5);
        for (int i = 0; i < 10_000; i++)
        {
            int v = rng.Next(3, 9);
            Assert.InRange(v, 3, 8);
            Assert.InRange(rng.NextDouble(), 0.0, 0.9999999999);
        }
    }
}

public class WorldGenerationTests
{
    [Fact]
    public void ChunksDoNotDependOnGenerationOrder()
    {
        var first = new WorldMap(99);
        var second = new WorldMap(99);

        // Explore in different orders.
        first.GetChunk(3, -2);
        second.GetChunk(-5, 4);
        second.GetChunk(0, 0);

        for (int y = -200; y < -64; y += 7)
        for (int x = 150; x < 260; x += 5)
        {
            Assert.Equal(first.GetTile(x, y).Terrain, second.GetTile(x, y).Terrain);
        }
    }

    [Fact]
    public void DifferentSeedsGiveDifferentWorlds()
    {
        var a = new WorldMap(1);
        var b = new WorldMap(2);
        int differences = 0;
        for (int i = 0; i < 400; i++)
        {
            if (a.GetTile(i * 3, i).Terrain != b.GetTile(i * 3, i).Terrain) differences++;
        }
        Assert.True(differences > 50);
    }

    [Fact]
    public void WorldHasEveryKindOfTerrain()
    {
        var map = new WorldMap(42);
        var seen = new HashSet<Terrain>();
        for (int y = -400; y < 400; y += 3)
        for (int x = -400; x < 400; x += 3)
        {
            seen.Add(map.GetTile(x, y).Terrain);
        }
        Assert.Equal(Enum.GetValues<Terrain>().Length, seen.Count);
    }

    [Fact]
    public void ChunkBordersAreSeamlessAcrossNegativeCoordinates()
    {
        var map = new WorldMap(7);
        var generator = new WorldGenerator(7);
        foreach (int x in new[] { -65, -64, -1, 0, 63, 64 })
        {
            Assert.Equal(generator.TerrainAt(x, -1), map.GetTile(x, -1).Terrain);
        }
    }
}

public class InventoryTests
{
    [Fact]
    public void TakeAllIsAllOrNothing()
    {
        var inv = new Inventory();
        inv.Add("wood", 10);
        inv.Add("stone", 2);

        Assert.False(inv.TryTakeAll(new Dictionary<string, int> { ["wood"] = 5, ["stone"] = 3 }));
        Assert.Equal(10, inv.Get("wood"));
        Assert.Equal(2, inv.Get("stone"));

        Assert.True(inv.TryTakeAll(new Dictionary<string, int> { ["wood"] = 5, ["stone"] = 2 }));
        Assert.Equal(5, inv.Get("wood"));
        Assert.Equal(0, inv.Get("stone"));
    }
}
