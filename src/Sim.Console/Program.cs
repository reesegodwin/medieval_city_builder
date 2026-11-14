using System.Diagnostics;
using Sim.Core.Settlements;
using Sim.Core.Simulation;
using Sim.Core.Systems;
using Sim.Core.Time;
using Sim.Core.World;

// Medieval City Builder — headless simulation runner (Phase 0).
//
//   dotnet run --project src/Sim.Console -- --seed 42 --days 100 --report daily
//
// Options:
//   --seed <n>         world seed (default 42)
//   --days <n>         game days to simulate (default 100)
//   --report <mode>    daily | weekly | none (default weekly)
//   --map              print the town map at start and end
//   --citizens         print every citizen at the end
//   --realtime [x]     run at real speed (1 game day = 1 real hour), optionally sped up x times
//   --no-disasters     switch off fires, sickness and poor harvests
//   --government ID    how the town is run (households.json), e.g. common_weal
//   --npc N            start the world with N computer-run neighbour towns (more are founded over time)

var options = Options.Parse(args);
var world = new GameWorld(options.Seed);
var settlement = SettlementFounder.Found(world);
settlement.Rules.Disasters = !options.NoDisasters;
if (options.Government != null) world.Execute(new Sim.Core.Economy.SetGovernmentCommand(settlement.Id, options.Government));
world.Settings.NpcTowns = options.NpcTowns;
var sim = new Simulator(world);

Console.WriteLine($"World seed {options.Seed}. {settlement.Name} founded at ({settlement.CenterX},{settlement.CenterY}) " +
                  $"with {settlement.Citizens.Count} settlers.");
Console.WriteLine($"Time scale: 1 game day = {TimeScale.RealSecondsPerGameDay / 3600:0.##} real hour(s), " +
                  $"{TimeScale.TicksPerDay} ticks per day.");
Console.WriteLine();
if (options.ShowMap) Report.PrintMap(world, settlement);

int eventCursor = world.Events.Count;
sim.DayEnded += w =>
{
    long day = w.Time.Day;
    bool print = options.ReportMode switch
    {
        "daily" => true,
        "weekly" => day % 7 == 0,
        _ => false,
    };
    if (print)
    {
        Report.PrintDay(w, settlement);
        for (; eventCursor < w.Events.Count; eventCursor++)
        {
            var e = w.Events[eventCursor];
            if (e.Kind is Sim.Core.Events.EventKind.BuildingPlaced) continue;
            Console.WriteLine($"      - {e.Message}");
        }
    }
    else
    {
        eventCursor = w.Events.Count;
    }
};

var stopwatch = Stopwatch.StartNew();
if (options.RealtimeSpeed is double speed)
{
    var tickInterval = TimeSpan.FromSeconds(TimeScale.RealSecondsPerTick / speed);
    Console.WriteLine($"Running in real time at {speed}x (one tick every {tickInterval.TotalSeconds:0.###}s). Ctrl+C to stop.");
    long ticks = (long)options.Days * TimeScale.TicksPerDay;
    for (long i = 0; i < ticks; i++)
    {
        sim.Step();
        Thread.Sleep(tickInterval);
    }
}
else
{
    sim.RunDays(options.Days);
}
stopwatch.Stop();

Console.WriteLine();
Console.WriteLine($"=== {settlement.Name} after {options.Days} days ({world.Time}) — simulated in {stopwatch.ElapsedMilliseconds} ms ===");
Report.PrintSummary(world, settlement);
if (world.Diplomacy.Factions.Count > 1)
{
    Console.WriteLine("Realm:");
    foreach (var f in world.Diplomacy.Factions)
        foreach (var town in Sim.Core.Diplomacy.Factions.TownsOf(world, f))
            Console.WriteLine($"  {f.Name}{(f.IsNpc ? $" ({f.Personality})" : " (you)")}: {town.Name} at ({town.CenterX},{town.CenterY}), " +
                              $"pop {town.Citizens.Count}, coin {town.Stock.Get("coin")}, soldiers {Sim.Core.War.Armies.Soldiers(town)}, " +
                              $"homes {town.Buildings.Count(b => b.Def.Home != null)}, walls {town.Walls.Count}");
    var book = world.Diplomacy;
    string Name(int id) => book.Find(id)?.Name ?? "?";
    foreach (var t in book.Treaties)
        Console.WriteLine($"  Treaty: {Name(t.A)} {(t.Kind == Sim.Core.Diplomacy.TreatyKind.Vassalage ? "is overlord of" : "-")} " +
                          $"{Name(t.B)} ({t.Kind}, since day {t.StartDay})");
    foreach (var r in book.Relations)
        Console.WriteLine($"  Opinion {Name(r.A)} / {Name(r.B)}: {r.Opinion:0}");
    Console.WriteLine($"  Diplomatic events: {world.Events.Count(e => e.Kind == Sim.Core.Events.EventKind.Diplomacy)}");
}
if (options.ShowMap) Report.PrintMap(world, settlement);
if (options.ShowCitizens) Report.PrintCitizens(settlement);
Console.WriteLine($"State hash: {world.ComputeStateHash():X16}");

internal sealed record Options(ulong Seed, int Days, string ReportMode, bool ShowMap, bool ShowCitizens, double? RealtimeSpeed,
    bool NoDisasters)
{
    public string? Government { get; init; }
    public int NpcTowns { get; init; }

    public static Options Parse(string[] args)
    {
        ulong seed = 42;
        int days = 100;
        string report = "weekly";
        bool map = false, citizens = false, noDisasters = false;
        string? government = null;
        int npcTowns = 0;
        double? realtime = null;

        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--seed": seed = ulong.Parse(Next()); break;
                case "--days": days = int.Parse(Next()); break;
                case "--report": report = Next(); break;
                case "--map": map = true; break;
                case "--citizens": citizens = true; break;
                case "--no-disasters": noDisasters = true; break;
                case "--government": government = Next(); break;
                case "--npc": npcTowns = int.Parse(Next()); break;
                case "--realtime":
                    realtime = i + 1 < args.Length && double.TryParse(args[i + 1], out double x) ? x : 1;
                    if (i + 1 < args.Length && double.TryParse(args[i + 1], out _)) i++;
                    break;
                default: throw new ArgumentException($"Unknown option {args[i]}");
            }
        }
        return new Options(seed, days, report, map, citizens, realtime, noDisasters) { Government = government, NpcTowns = npcTowns };
    }
}

internal static class Report
{
    public static void PrintDay(GameWorld world, Settlement s)
    {
        int adults = s.Citizens.Count(c => c.IsAdult);
        int idle = s.Citizens.Count(c => c.IsAdult && c.JobBuildingId == null);
        int homeless = s.Citizens.Count(c => c.HomeBuildingId == null);
        int haulers = s.Buildings.Where(b => b.CurrentJobKind == JobKinds.Hauler).Sum(b => b.WorkerIds.Count);
        double mood = s.Citizens.Count > 0 ? s.Citizens.Average(c => c.Mood) : 0;
        Console.WriteLine(
            $"{world.Time,-20} Pop {s.Citizens.Count,3} ({adults} adults) | " +
            $"grain {s.Stock.Get("grain"),4} bread {s.Stock.Get("bread"),4} ({Needs.FoodDays(world, s),5:0.0}d) | " +
            $"wood {s.Stock.Get("wood"),4} stone {s.Stock.Get("stone"),4} | coin {s.Stock.Get("coin")} | cold homes {s.Buildings.Count(b => b.Cold)} sick {s.Citizens.Count(c => c.IsSick)} | mood {mood,3:0} | odd jobs {idle} haulers {haulers} ({s.HaulCoverage:P0}) homeless {homeless} | " +
            string.Join(" ", world.Data.Needs.Select(n => $"{n.Id} {ServiceSystem.Coverage(s, n.Id):P0}")));
    }

    /// <summary>The town's books: each good's average daily flows over the last week, and where coin comes and goes.</summary>
    public static void PrintLedger(GameWorld world, Settlement s)
    {
        var ledger = s.Ledger;
        Console.WriteLine("Ledger (per day, last 7 days):  good          made     used   traded      net");
        foreach (var r in world.Data.Resources.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (r == Sim.Core.Economy.Inventory.Coin) continue;
            var parts = Sim.Core.Economy.LedgerRow.Of(ledger, r);
            if (Math.Abs(parts.Made) + Math.Abs(parts.Used) + Math.Abs(parts.Traded) < 0.05) continue;
            Console.WriteLine($"{"",32}{r,-12}{parts.Made,8:0.#}{parts.Used,9:0.#}{parts.Traded,9:0.#}{parts.Net,9:0.#}");
        }
        var coin = ledger.Breakdown(Sim.Core.Economy.Inventory.Coin);
        Console.WriteLine("Coin per day: " + string.Join(", ", coin.Where(kv => Math.Abs(kv.Value) >= 0.05).Select(kv => $"{kv.Key} {kv.Value:+0.#;-0.#}")));
    }

    public static void PrintSummary(GameWorld world, Settlement s)
    {
        PrintDay(world, s);
        Console.WriteLine($"Births {world.TotalBirths}, deaths {world.TotalDeaths}. Storage {s.Stock.Total - s.StoredAtWorkplaces}/{s.StorageCapacity} " +
                          $"(+{s.StoredAtWorkplaces} at workplaces), {s.Buildings.Count(b => b.Problem == "Storage full")} workplaces with storage full, " +
                          $"housing {s.HousingCapacity - s.FreeHousing}/{s.HousingCapacity}, road tiles {s.Roads.Count}.");
        PrintLedger(world, s);
        double spoiled = world.Data.FoodsByPreference.Sum(f => -s.Ledger.History.Sum(d => d.Get(f.Id, Sim.Core.Economy.LedgerKeys.Spoiled)));
        Console.WriteLine($"Food: {SeasonSystem.FoodStock(world, s)} stored, granaries keep {s.FoodStoreCapacity} from rot; " +
                          $"{spoiled:0} rotted in the last {s.Ledger.History.Count} days. Firewood {s.Stock.Get("firewood")}.");
        var enclosure = WallSystem.Enclose(world, s);
        int safeHomes = s.Buildings.Count(b => b.Def.Housing > 0 && b.IsOperational && WallSystem.IsInside(world, s, b));
        Console.WriteLine($"Walls: {s.Walls.Count} tiles (" + string.Join(", ", s.Walls.Values.GroupBy(w => w.Tier).OrderBy(g => g.Key)
                              .Select(g => $"{g.Count()} {world.Data.WallTiers[g.Key].Name.ToLowerInvariant()}")) +
                          $"), {s.Walls.Values.Count(w => w.Piece == WallPiece.Gate)} gates, {s.Walls.Values.Count(w => w.Piece == WallPiece.Gatehouse)} gatehouses, " +
                          $"{s.Walls.Values.Count(w => w.Piece == WallPiece.Tower)} towers, {s.WallPlans.Count} planned, ring {s.WallRing.Count} tiles; " +
                          $"{safeHomes} homes safe behind {(enclosure.RingTier >= 0 ? world.Data.WallTiers[enclosure.RingTier].Name.ToLowerInvariant() : "no closed ring")}; " +
                          $"wall-walk {WallSystem.WalkwayTiles(world, s)} tiles for {WallSystem.DefenderCapacity(world, s)} defenders; " +
                          $"{s.Buildings.Count(b => b.NoRoute)} buildings cut off.");
        var units = world.Data.War;
        int Said(string start) => world.Events.Count(e => e.SettlementId == s.Id && e.Message.StartsWith(start, StringComparison.Ordinal));
        Console.WriteLine($"Sieges: {Said("Siege:")} laid, {Said("Breach!")} breaches, {Said("Sally:")} sallies, " +
                          $"{Said("The town paid the raiders")} paid off, {Said("The raiders gave up") + Said("Too few raiders")} given up; " +
                          $"walls damaged now: {s.Walls.Values.Count(w => w.Hp < Sim.Core.Commands.Walls.PieceHitPoints(world, w.Tier, w.Piece))}.");
        Console.WriteLine($"Army: {Sim.Core.War.Armies.Soldiers(s)} soldiers in {s.Squads.Count} squads (" +
                          string.Join(", ", s.Squads.GroupBy(q => q.UnitId).Select(g => $"{g.Sum(q => q.SoldierIds.Count)} {g.Key}")) + ")" +
                          (s.General is { } gen ? $"; general {s.FindCitizen(gen.CitizenId)?.FullName} (skill {gen.Skill:0}, {gen.Battles} battles)" : "; no general") +
                          $"; battles {world.War.Reports.Count(r => r.SettlementId == s.Id)} " +
                          $"(won {world.War.Reports.Count(r => r.SettlementId == s.Id && r.DefendersWon)}); " +
                          $"fell defending {world.Events.Count(e => e.SettlementId == s.Id && e.Message.Contains("fell defending"))}; " +
                          $"gear: {s.Stock.Get("weapons")} weapons, {s.Stock.Get("bows")} bows, {s.Stock.Get("armour")} armour.");
        var odd = OddJobs.Allocate(world, s);
        string gathered = string.Join(", ", world.Data.OddJobs.Gather.Select(g =>
            $"{s.Ledger.History.Sum(d => d.Get(g.Resource, Sim.Core.Economy.LedgerKeys.Gathered)):0} {g.Resource}"));
        Console.WriteLine($"Odd jobs: {odd.People} people ({odd.AtSites} building, {odd.Roads} on roads, {odd.Gatherers} gathering); " +
                          $"gathered in the last {s.Ledger.History.Count} days: {gathered}.");
        var mine = world.Events.Where(e => e.SettlementId == s.Id).ToList();
        int Count(string text) => mine.Count(e => e.Kind == Sim.Core.Events.EventKind.Disaster && e.Message.StartsWith(text));
        Console.WriteLine($"Disasters: {Count("Fire")} fires, {Count("Sickness")} outbreaks of sickness, {Count("A poor harvest")} poor harvests. " +
                          "Shortage warnings: " + string.Join(", ", mine.Where(e => e.Kind == Sim.Core.Events.EventKind.Shortage && e.Message.Contains("runs out"))
                              .GroupBy(e => e.Message.Split(' ')[0]).Select(g => $"{g.Key} x{g.Count()}")) + ".");
        var homes = s.Buildings.Where(b => b.Def.Home != null && b.IsOperational).GroupBy(b => b.Def.Name)
            .Select(g => $"{g.Count()} {g.Key.ToLowerInvariant()}s");
        var classes = s.Citizens.GroupBy(c => HomeSystem.ClassOf(world, s, c).Name).Select(g => $"{g.Count()} {g.Key.ToLowerInvariant()}");
        Console.WriteLine($"Homes: {string.Join(", ", homes)}. People: {string.Join(", ", classes)}.");
        var trade = world.Trade;
        {
            var occupied = s.Buildings.Where(b => b.Def.Home != null && b.IsOperational && b.ResidentIds.Count > 0).ToList();
            string byClass = string.Join(", ", occupied.GroupBy(b => b.Def.Home!.Class).OrderBy(g => g.Key)
                .Select(g => $"{g.Key} {g.Count()} homes avg {g.Average(h => Sim.Core.Economy.HouseholdSystem.Purse(s, h)):0.#}"));
            double wages = s.Citizens.Sum(c => Sim.Core.Economy.HouseholdSystem.Wage(world, s, c).Pay);
            int jobless = s.Citizens.Count(c => c.CanWork && c.JobBuildingId == null);
            Console.WriteLine($"Households ({world.Data.Households.Government(s.Rules.Government).Name}): {byClass}; wages {wages:0.#}/day; " +
                              $"{jobless} jobless; {s.ReliefHouseholds} homes on relief, {s.PoorHouseholds} going without; {s.Crimes} thefts.");
        }
        Console.WriteLine($"Treasury: taxes {EconomySystem.DailyTax(world, s):0.#}/day, upkeep {EconomySystem.DailyUpkeep(world, s):0.#}/day" +
                          (s.UpkeepUnpaid ? " (UNPAID)" : "") + ".");
        Console.WriteLine($"Trade: {trade.CompletedTrades} trades, {trade.MerchantVisits} merchant visits, coin {s.Stock.Get("coin")}, " +
                          $"{trade.Offers.Count(o => o.SettlementId == s.Id)} open offers, {trade.Caravans.Count} caravans on the road.");
        Console.WriteLine($"Territory {s.Territory.Count}/{s.TerritoryAllowance} tiles (cap {s.TerritoryCap}, {s.TownCentre?.Def.Name ?? "no town centre"}).");
        Console.WriteLine("Stock: " + string.Join(", ", s.Stock.Items.Select(kv => $"{kv.Key} {kv.Value}")));
        Console.WriteLine("Buildings:");
        foreach (var group in s.Buildings.GroupBy(b => b.Def.Name))
        {
            int done = group.Count(b => b.IsOperational);
            string workers = group.First().Def.Jobs > 0
                ? $", workers {group.Sum(b => b.WorkerIds.Count)}/{group.Where(b => b.IsOperational).Sum(b => b.Def.Jobs)}"
                : "";
            string problems = string.Join("; ", group.Where(b => b.Problem != null).Select(b => b.Problem).Distinct());
            Console.WriteLine($"  {group.Key,-18} {done}/{group.Count()} built{workers}{(problems.Length > 0 ? $"  [{problems}]" : "")}");
        }
    }

    public static void PrintCitizens(Settlement s)
    {
        Console.WriteLine("Citizens:");
        foreach (var c in s.Citizens)
        {
            string job = c.JobBuildingId is int j ? s.FindBuilding(j)?.Def.Name ?? "?" : c.IsAdult ? "idle" : "child";
            string best = c.Skills.Count > 0
                ? string.Join(", ", c.Skills.OrderByDescending(kv => kv.Value).Take(2).Select(kv => $"{kv.Key} {kv.Value:0}"))
                : "-";
            string traits = c.Traits.Count > 0 ? string.Join(", ", c.Traits) : "-";
            Console.WriteLine($"  {c.FullName,-22} {(int)c.AgeYears,3}y {c.Sex,-6} mood {c.Mood,3:0}  {job,-18} skills: {best,-28} traits: {traits}");
        }
    }

    public static void PrintMap(GameWorld world, Settlement s, int radius = 30)
    {
        Console.WriteLine("Map ('.' grass, ',' fertile, 'T' forest, '^' rock, '*' iron, '~' water; = road; letters = buildings, lowercase = under construction)");
        for (int y = s.CenterY - radius / 2; y <= s.CenterY + radius / 2; y++)
        {
            var line = new System.Text.StringBuilder();
            for (int x = s.CenterX - radius; x <= s.CenterX + radius; x++)
            {
                var tile = world.Map.GetTile(x, y);
                if (tile.BuildingId != 0 && s.FindBuilding(tile.BuildingId) is { } b)
                {
                    char ch = b.Def.Id switch
                    {
                        "stockpile" => 'S',
                        "hovel" => 'H',
                        "farm" => 'F',
                        "woodcutter" => 'W',
                        "quarry" => 'Q',
                        "bakery" => 'B',
                        "well" => 'O',
                        "garden" => 'G',
                        "chapel" => 'C',
                        "brewery" => 'R',
                        "tavern" => 'V',
                        "market" => 'M',
                        _ => '#',
                    };
                    line.Append(b.IsOperational ? ch : char.ToLowerInvariant(ch));
                    continue;
                }
                if (tile.IsRoad)
                {
                    line.Append('=');
                    continue;
                }
                line.Append(tile.Terrain switch
                {
                    Terrain.Grass => '.',
                    Terrain.Fertile => ',',
                    Terrain.Forest => 'T',
                    Terrain.Rock => '^',
                    Terrain.IronDeposit => '*',
                    Terrain.Water => '~',
                    _ => '?',
                });
            }
            Console.WriteLine(line);
        }
        Console.WriteLine();
    }
}
