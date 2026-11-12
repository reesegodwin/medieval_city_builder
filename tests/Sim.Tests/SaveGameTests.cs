using Sim.Core.Commands;
using Sim.Core.Persistence;
using Sim.Core.Simulation;

namespace Sim.Tests;

public class SaveGameTests
{
    [Fact]
    public void LoadedWorldContinuesExactlyLikeTheOriginal()
    {
        var (original, town, sim) = Scenario.NewTown(seed: 7, autoBuild: true);
        sim.RunDays(40);

        var loaded = SaveGame.FromJson(SaveGame.ToJson(original));
        Assert.Equal(original.ComputeStateHash(), loaded.ComputeStateHash());

        sim.RunDays(60);
        new Simulator(loaded).RunDays(60);

        Assert.Equal(original.ComputeStateHash(), loaded.ComputeStateHash());
        Assert.Equal(town.Citizens.Count, loaded.Settlements[0].Citizens.Count);
        Assert.Equal(town.Roads.Count, loaded.Settlements[0].Roads.Count);
    }

    [Fact]
    public void MidDaySaveWithEmptiedStockRoundTrips()
    {
        var (world, town, sim) = Scenario.NewTown(seed: 42, autoBuild: true);
        sim.RunTicks(20L * Sim.Core.Time.TimeScale.TicksPerDay + 77);
        town.Stock.TryTake("bread", town.Stock.Get("bread")); // an item that ran out

        var loaded = SaveGame.FromJson(SaveGame.ToJson(world));

        Assert.Equal(world.ComputeStateHash(), loaded.ComputeStateHash());
    }

    [Fact]
    public void SaveRestoresTilesRulesAndPeople()
    {
        var (world, town, sim) = Scenario.NewTown();
        world.Execute(new SetJobPriorityCommand(town.Id, "baker", 42));
        world.Execute(new SetAutoBuildCommand(town.Id, false));
        sim.RunDays(5);

        var loaded = SaveGame.FromJson(SaveGame.ToJson(world));
        var copy = loaded.Settlements.Single();

        Assert.Equal(42, copy.Rules.PriorityOf("baker"));
        Assert.False(copy.Rules.AutoBuild);
        Assert.Equal(world.Time, loaded.Time);
        foreach (var b in town.Buildings)
            Assert.Equal(b.Id, loaded.Map.GetTile(b.X, b.Y).BuildingId);
        foreach (var r in town.Roads)
            Assert.True(loaded.Map.GetTile(r.X, r.Y).IsRoad);

        var person = town.Citizens[0];
        var restored = copy.FindCitizen(person.Id)!;
        Assert.Equal(person.FullName, restored.FullName);
        Assert.Equal(person.History, restored.History);
        Assert.Equal(person.Skills, restored.Skills);

        // New ids continue after the saved ones.
        Assert.True(loaded.NextId() > town.Citizens.Max(c => c.Id));
    }

    [Fact]
    public void RejectsSavesFromNewerVersions()
    {
        var (world, _, _) = Scenario.NewTown();
        string json = SaveGame.ToJson(world).Replace($"\"Version\":{SaveGame.CurrentVersion}", "\"Version\":999");
        Assert.Throws<InvalidDataException>(() => SaveGame.FromJson(json));
    }
}
