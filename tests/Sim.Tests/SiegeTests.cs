using Sim.Core.Commands;
using Sim.Core.Persistence;
using Sim.Core.Settlements;
using Sim.Core.Simulation;
using Sim.Core.Systems;
using Sim.Core.War;
using Sim.Core.World;

namespace Sim.Tests;

public class SiegeTests
{
    private static void Grow(GameWorld world, Settlement town, int people)
    {
        while (people > 0)
            foreach (var c in CitizenFactory.CreateHousehold(world, Math.Min(4, people)))
            {
                town.AddCitizen(c);
                people--;
            }
    }

    /// <summary>A town with a closed ring of walls (tier 0 palisade, 1 stone) around an empty 8x8 patch with a hovel.</summary>
    private static (GameWorld World, Settlement Town, List<(int X, int Y)> Ring) WalledTown(byte tier = 0)
    {
        var (world, town, _) = Scenario.NewTown();
        town.Rules.Disasters = false;
        town.Stock.Add("wood", 1000);
        town.Stock.Add("stone", 1000);
        const int size = 8;
        bool Free(int x, int y) => world.Map.GetTile(x, y) is { BuildingId: 0, IsRoad: false, RoadPlanned: false } t &&
                                   t.Terrain is Terrain.Grass or Terrain.Fertile && town.OwnsTile(world.Map, x, y);
        (int X, int Y) corner = default;
        bool found = false;
        foreach (var (tx, ty) in town.Territory.OrderBy(t => Math.Abs(t.X - town.CenterX) + Math.Abs(t.Y - town.CenterY)))
        {
            found = true;
            for (int y = ty - 1; y <= ty + size && found; y++)
            for (int x = tx - 1; x <= tx + size && found; x++)
                found = Free(x, y);
            if (found) { corner = (tx, ty); break; }
        }
        Assert.True(found, "an empty patch");
        Assert.True(world.Execute(new PlaceBuildingCommand(town.Id, "hovel", corner.X + 3, corner.Y + 3)).Ok);
        var ring = new List<(int X, int Y)>();
        for (int k = 0; k < size; k++)
        {
            ring.Add((corner.X + k, corner.Y));
            ring.Add((corner.X + k, corner.Y + size - 1));
            if (k > 0 && k < size - 1) { ring.Add((corner.X, corner.Y + k)); ring.Add((corner.X + size - 1, corner.Y + k)); }
        }
        Assert.True(world.Execute(new PlaceWallCommand(town.Id, ring)).Ok);
        Walls.FinishAllPlans(world, town);
        if (tier > 0)
        {
            Assert.True(world.Execute(new PlaceWallCommand(town.Id, ring, tier)).Ok);
            Walls.FinishAllPlans(world, town);
        }
        Assert.True(WallSystem.Enclose(world, town).RingTier >= 0);
        return (world, town, ring);
    }

    private static Warband BandAtTheWalls(GameWorld world, Settlement town, int raiders, int archers = 0)
    {
        var band = new Warband
        {
            Id = world.NextId(), TargetId = town.Id, Raiders = raiders, Archers = archers,
            X = town.CenterX + Raids.ArriveRadius(town) - 1, Y = town.CenterY,
        };
        world.War.Warbands.Add(band);
        return band;
    }

    [Fact]
    public void SmallBandsAttackButBigOnesLaySiegeToWalledTowns()
    {
        var (world, town, _) = WalledTown();
        BandAtTheWalls(world, town, 8);
        Raids.Daily(world);
        Assert.Single(world.War.Reports); // fought at once
        Assert.Empty(world.War.Warbands);

        var big = BandAtTheWalls(world, town, world.Data.War.Siege.MinSize + 5);
        Raids.Daily(world);
        Assert.True(big.Siege);
        Assert.True(Siege.Besieged(world, town));
        Assert.Single(world.War.Reports); // no battle yet
    }

    [Fact]
    public void EnginesBreachTheWallAndTheBandStormsIn()
    {
        var (world, town, _) = WalledTown(); // a palisade: rams make short work of it
        var band = BandAtTheWalls(world, town, 30);
        int walls = town.Walls.Count;
        var hovel = town.Buildings.Last(b => b.Def.Id == "hovel");
        Assert.True(WallSystem.IsInside(world, town, hovel));
        Siege.Begin(world, town, band);
        for (int day = 0; day < 10 && world.War.Warbands.Contains(band); day++) Siege.Daily(world, town, band);

        Assert.DoesNotContain(band, world.War.Warbands); // stormed in and fought
        Assert.Equal(walls - 1, town.Walls.Count);       // one tile knocked down
        Assert.Single(world.War.Reports);
        Assert.False(WallSystem.IsInside(world, town, hovel), "the hovel is no longer walled in");
        Assert.Contains(world.Events, e => e.Message.StartsWith("Breach!"));
    }

    [Fact]
    public void EachWallNeedsTheRightEngine()
    {
        var (world, _, _) = WalledTown();
        var rules = world.Data.War.Siege;
        var ram = rules.Engine("ram")!;
        var catapult = rules.Engine("catapult")!;
        var trebuchet = rules.Engine("trebuchet")!;
        var stoneGate = new WallSegment { Tier = 1, Piece = WallPiece.Gate, Hp = 400 };
        var largeWall = new WallSegment { Tier = 2, Piece = WallPiece.Wall, Hp = 1000 };
        Assert.True(Siege.Rate(ram, stoneGate) > Siege.Rate(catapult, stoneGate));
        Assert.True(Siege.Rate(trebuchet, largeWall) > Siege.Rate(catapult, largeWall));
        Assert.True(Siege.Rate(trebuchet, largeWall) > Siege.Rate(ram, largeWall));
    }

    [Fact]
    public void BandsGiveUpAfterAWhile()
    {
        var (world, town, _) = WalledTown(1);
        var band = BandAtTheWalls(world, town, 30);
        Siege.Begin(world, town, band);
        band.SiegeDays = world.Data.War.Siege.MaxDays;
        Siege.Daily(world, town, band);
        Assert.Empty(world.War.Warbands);
        Assert.Empty(world.War.Reports);
    }

    [Fact]
    public void AWinningSallyBurnsTheEngines()
    {
        var (world, town, _) = WalledTown(1);
        Grow(world, town, 160);
        var band = BandAtTheWalls(world, town, 26);
        Siege.Begin(world, town, band);
        band.Engines.Add(new SiegeEngine { Kind = "catapult", Progress = 1 });
        Assert.False(world.Execute(new SallyCommand(town.Id)).Ok); // no soldiers yet
        for (int i = 0; i < 8; i++) Assert.NotNull(Armies.Raise(world, town, "militia", 10, levy: true).Squad);

        Assert.True(world.Execute(new SallyCommand(town.Id)).Ok);
        Assert.Empty(band.Engines);
        Assert.True(band.Size < 26);
        Assert.False(world.Execute(new SallyCommand(town.Id)).Ok); // once a day
    }

    [Fact]
    public void TributeBuysThemOff()
    {
        var (world, town, _) = WalledTown();
        var band = BandAtTheWalls(world, town, 30);
        Siege.Begin(world, town, band);
        int price = Siege.Tribute(world, band);
        town.Stock.TryTake("coin", town.Stock.Get("coin"));
        Assert.False(world.Execute(new PayTributeCommand(town.Id)).Ok);
        town.Stock.Add("coin", price + 5);
        Assert.True(world.Execute(new PayTributeCommand(town.Id)).Ok);
        Assert.Equal(5, town.Stock.Get("coin"));
        Assert.False(Siege.Besieged(world, town));
    }

    [Fact]
    public void ASiegePinchesTheTown()
    {
        var (world, town, _) = WalledTown();
        var outside = town.Buildings.First(b => b.Def.Recipe != null && !WallSystem.IsInside(world, town, b));
        double before = EconomySystem.OutputFactor(world, town, outside);
        var band = BandAtTheWalls(world, town, 30);
        Siege.Begin(world, town, band);
        Assert.Equal(before * world.Data.War.Siege.OutsideOutput, EconomySystem.OutputFactor(world, town, outside), 9);
        Assert.Contains(Needs.MoodFactors(world, town, town.Citizens[0]), f => f.Label == "Under siege");

        town.Stock.Add("cloth", 10);
        Scenario.Finish(world, town, Scenario.Place(world, town, "trading_post"));
        var refused = world.Execute(new Sim.Core.Trade.PostOfferCommand(town.Id,
            new Dictionary<string, int> { ["cloth"] = 5 }, new Dictionary<string, int> { ["coin"] = 10 }));
        Assert.False(refused.Ok);
        Assert.Contains("siege", refused.Error);
    }

    [Fact]
    public void WallsAreRepairedAfterwards()
    {
        var (world, town, ring) = WalledTown(1);
        var wall = town.Walls[ring[0]];
        int max = wall.Hp;
        wall.Hp = 100;
        var band = BandAtTheWalls(world, town, 30);
        Siege.Begin(world, town, band);
        Siege.Repair(world, town);
        Assert.Equal(100, wall.Hp); // not while besieged

        world.War.Warbands.Clear();
        int stone = town.Stock.Get("stone");
        Siege.Repair(world, town);
        Assert.Equal(100 + world.Data.War.Siege.RepairHpPerDay, wall.Hp);
        Assert.True(town.Stock.Get("stone") < stone);
        for (int day = 0; day < 20; day++) Siege.Repair(world, town);
        Assert.Equal(max, wall.Hp);
    }

    [Fact]
    public void ArchersOnlyShootEnginesThatComeWithinRange()
    {
        var (world, town, _) = WalledTown(1);
        Grow(world, town, 60);
        town.Stock.Add("bows", 30);
        for (int i = 0; i < 2; i++) Assert.NotNull(Armies.Raise(world, town, "archers", 10, levy: true).Squad);
        var rules = world.Data.War.Siege;
        Assert.True(Siege.ArrowDamage(world, town) > 0);
        Assert.True(rules.ArcherRange < rules.Engine("trebuchet")!.Range); // trebuchets out-range archers
        Assert.True(Siege.InArcherRange(rules, rules.Engine("catapult")!));
        Assert.True(Siege.InArcherRange(rules, rules.Engine("ram")!));

        var band = BandAtTheWalls(world, town, 40);
        Siege.Begin(world, town, band);
        var building = new SiegeEngine { Kind = "catapult", Progress = 0.5 };     // still being built, out of bowshot
        var trebuchet = new SiegeEngine { Kind = "trebuchet", Progress = 1, Hp = 150 }; // working from beyond range
        var catapult = new SiegeEngine { Kind = "catapult", Progress = 1, Hp = 80 };    // rolled up within range
        band.Engines.AddRange(new[] { building, trebuchet, catapult });

        Siege.ShootAtEngines(world, town, band);
        Assert.True(catapult.Hp < 80);
        Assert.Equal(150, trebuchet.Hp);
        Assert.Equal(0, building.Hp);

        for (int day = 0; day < 40 && band.Engines.Contains(catapult); day++) Siege.ShootAtEngines(world, town, band);
        Assert.DoesNotContain(catapult, band.Engines);  // wrecked
        Assert.Contains(trebuchet, band.Engines);       // untouched
        Assert.Contains(building, band.Engines);
        Assert.Contains(world.Events, e => e.Message.Contains("archers wrecked"));
    }

    [Fact]
    public void SiegesSurviveSaveAndLoad()
    {
        var (world, town, _) = WalledTown(1);
        var band = BandAtTheWalls(world, town, 30);
        Siege.Begin(world, town, band);
        for (int day = 0; day < 3; day++) Siege.Daily(world, town, band);
        Assert.NotEmpty(band.Engines);

        var loaded = SaveGame.FromJson(SaveGame.ToJson(world));
        Assert.Equal(world.ComputeStateHash(), loaded.ComputeStateHash());
        var copy = loaded.War.Warbands.Single();
        Assert.True(copy.Siege);
        Assert.Equal(band.Engines.Count, copy.Engines.Count);
        Assert.Equal(band.SiegeDays, copy.SiegeDays);
    }
}
