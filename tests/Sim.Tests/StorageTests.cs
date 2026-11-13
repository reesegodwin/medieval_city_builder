using Sim.Core.Data;
using Sim.Core.Persistence;
using Sim.Core.Settlements;
using Sim.Core.Simulation;
using Sim.Core.Systems;

namespace Sim.Tests;

public class StorageTests
{
    /// <summary>Fills general storage (stockpiles) to the brim with cloth, which no workplace here makes.</summary>
    private static void FillStockpiles(Settlement town)
    {
        town.Stock.Add("cloth", town.FreeStorage);
        Assert.Equal(0, town.FreeStorage);
    }

    [Fact]
    public void WorkplacesKeepSomeOfTheirOwnGoods()
    {
        var (world, town, _) = Scenario.NewTown();
        int farms = town.Buildings.Count(b => b.Def.Id == "farm" && b.IsOperational);
        Assert.True(farms > 0);
        Assert.Equal(farms * world.Data.Building("farm").OwnStorageCapacity, town.OwnStorageOf("grain"));
        Assert.Equal(BuildingDef.DefaultOwnStorage, world.Data.Building("bakery").OwnStorageCapacity);
        Assert.Equal(0, world.Data.Building("hovel").OwnStorageCapacity);

        // Grain up to the farms' own space takes no stockpile space; beyond it, it does.
        town.Stock.TryTake("grain", town.Stock.Get("grain"));
        int free = town.FreeStorage;
        town.Stock.Add("grain", town.OwnStorageOf("grain"));
        Assert.Equal(free, town.FreeStorage);
        town.Stock.Add("grain", 10);
        Assert.Equal(free - 10, town.FreeStorage);
    }

    [Fact]
    public void FarmsKeepWorkingWhenStockpilesAreFullUntilTheirOwnStoreFills()
    {
        var (world, town, sim) = Scenario.NewTown();
        town.Rules.Disasters = false;
        town.Stock.TryTake("grain", town.Stock.Get("grain"));
        town.Rules.Reserves["grain"] = 0;
        FillStockpiles(town);

        int room = town.OwnStorageOf("grain");
        sim.RunDays(2);
        Assert.True(town.Ledger.History.Sum(d => d.Get("grain", Sim.Core.Economy.LedgerKeys.Made)) > 0, "Farms still harvest");
        Assert.True(town.Stock.Get("grain") <= room, "Only into the farms' own store");

        // Once their own store is full too (well beyond what's eaten meanwhile), farms stop with "Storage full".
        town.Stock.Add("grain", room + 500);
        sim.RunDays(2);
        Assert.Contains(town.Buildings, b => b.Def.Id == "farm" && b.Problem == "Storage full");
    }

    [Fact]
    public void EachProductUsesItsOwnStore()
    {
        var (world, town, sim) = Scenario.NewTown();
        town.Rules.Disasters = false;
        var pasture = Scenario.Place(world, town, "pasture");
        Scenario.Finish(world, town, pasture);
        Assert.Equal(world.Data.Building("pasture").OwnStorageCapacity, town.OwnStorageOf("wool"));
        Assert.Equal(world.Data.Building("pasture").OwnStorageCapacity, town.OwnStorageOf("mutton"));
        Assert.Equal(town.FreeStorage + town.OwnStorageOf("wool") - town.Stock.Get("wool"), town.RoomFor("wool"));
    }

    [Fact]
    public void WorkplaceStoresAreDerivedSoSavesStayInStep()
    {
        var (world, town, sim) = Scenario.NewTown(42, autoBuild: true);
        sim.RunDays(150);
        var copy = SaveGame.FromJson(SaveGame.ToJson(world));
        Assert.Equal(town.StoredAtWorkplaces, copy.Settlements[0].StoredAtWorkplaces);
        Assert.Equal(town.FreeStorage, copy.Settlements[0].FreeStorage);
        sim.RunDays(10);
        new Simulator(copy).RunDays(10);
        Assert.Equal(world.ComputeStateHash(), copy.ComputeStateHash());
    }
}
