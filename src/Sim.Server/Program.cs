using Sim.Core.Persistence;
using Sim.Core.Simulation;
using Sim.Net;

// Medieval City Builder dedicated server: hosts one shared world over TCP.
//
//   dotnet run --project src/Sim.Server -- --world myworld.json --speed persistent --when-empty run
//
// Options:
//   --world <file>         save file to load, or create if missing (default world.json)
//   --port <n>             TCP port (default 7777)
//   New worlds only (stored in the save afterwards):
//   --seed <n>             world seed (default: random)
//   --speed <preset|secs>  persistent | weekend | evening | sprint, or real seconds per game day (default persistent)
//   --speed-policy <p>     fixed | host | unanimous (default fixed)
//   --when-empty <p>       pause | catchup | run (default run)
//   --catch-up-days <n>    most game days caught up on after time away (default 7)
//   --max-speed <x>        fastest players may speed the world up to (default 8)
//   --npc-towns <n>        computer-run neighbour realms to start with (default from factions.json, 3; 0 for none)
//   --admin <name>         the player who controls speed under the "host" policy
//   --autosave-minutes <n> (default 5)
//   --accounts <open|off>  open (default): the first join with a name registers it with that player's password,
//                          later joins must prove it. off: names are trusted (only for private servers).
//   --reset-password <name> forget a player's account, so they can register again with a new password

var options = ParseArgs(args);
string path = options.GetValueOrDefault("world", "world.json");
int port = int.Parse(options.GetValueOrDefault("port", Protocol.DefaultPort.ToString()));

GameWorld world;
if (File.Exists(path))
{
    world = SaveGame.Load(path);
    Console.WriteLine($"Loaded {path}: {world.Time}, {world.Settlements.Count} settlement(s).");
}
else
{
    ulong seed = options.TryGetValue("seed", out var s) ? ulong.Parse(s) : (ulong)Random.Shared.NextInt64();
    world = new GameWorld(seed);
    string speed = options.GetValueOrDefault("speed", "persistent");
    world.Settings.SecondsPerGameDay = WorldSpeeds.SecondsPerDay(speed) ?? double.Parse(speed);
    world.Settings.SpeedPolicy = Enum.Parse<SpeedPolicy>(options.GetValueOrDefault("speed-policy", "fixed"), ignoreCase: true);
    world.Settings.WhenEmpty = Enum.Parse<EmptyWorldPolicy>(options.GetValueOrDefault("when-empty", "run"), ignoreCase: true);
    world.Settings.CatchUpCapDays = double.Parse(options.GetValueOrDefault("catch-up-days", "7"));
    world.Settings.MaxSpeedMultiplier = double.Parse(options.GetValueOrDefault("max-speed", "8"));
    world.Settings.NpcTowns = int.Parse(options.GetValueOrDefault("npc-towns", world.Data.Factions.StartTowns.ToString()));
    world.LastActiveUtc = DateTimeOffset.UtcNow;
    SaveGame.Save(world, path);
    Console.WriteLine($"Created {path} (seed {seed}).");
}

var settings = world.Settings;
Console.WriteLine($"Speed: 1 game day = {settings.SecondsPerGameDay:0.#} real seconds; speed control: {settings.SpeedPolicy}; " +
                  $"when empty: {settings.WhenEmpty} (catch up at most {settings.CatchUpCapDays:0.#} days).");

AccountBook? accounts = null;
if (options.GetValueOrDefault("accounts", "open") != "off")
{
    accounts = AccountBook.Load(path + ".accounts.json");
    if (options.TryGetValue("reset-password", out var forget))
        Console.WriteLine(accounts.Forget(forget) ? $"Forgot {forget}'s account; they can register again." : $"No account named {forget}.");
    Console.WriteLine($"Accounts: {accounts.Accounts.Count} registered in {accounts.Path}.");
}
else Console.WriteLine("Accounts are off: anyone may join under any name.");

var host = new WorldHost(world, options.GetValueOrDefault("admin"), accounts: accounts);
host.Log += message => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
if (host.CatchUpTicksLeft >= Sim.Core.Time.TimeScale.TicksPerDay / 10) Console.WriteLine($"Catching up {host.CatchUpTicksLeft / (double)Sim.Core.Time.TimeScale.TicksPerDay:0.#} game days of downtime...");

void Save(WorldHost h)
{
    // Write to a temporary file first so a crash mid-save can't destroy the world.
    string temp = path + ".tmp";
    SaveGame.Save(h.World, temp);
    File.Move(temp, path, overwrite: true);
}

using var listener = new TcpHostListener(host, port);
var runner = new HostRunner(host, Save, TimeSpan.FromMinutes(double.Parse(options.GetValueOrDefault("autosave-minutes", "5"))));
Console.WriteLine($"Listening on port {listener.Port}. Press Ctrl+C to save and stop.");

// Ctrl+C, `docker stop` and systemd all ask politely (SIGINT / SIGTERM): save before exiting.
var stop = new ManualResetEventSlim();
void RequestStop(System.Runtime.InteropServices.PosixSignalContext context)
{
    context.Cancel = true; // we exit ourselves once the world is saved
    stop.Set();
}
using var onInterrupt = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGINT, RequestStop);
using var onTerminate = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, RequestStop);
stop.Wait();

runner.Dispose(); // stops the loop and saves
Console.WriteLine($"Saved {path} at {world.Time}. Bye.");

static Dictionary<string, string> ParseArgs(string[] args)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (int i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--")) continue;
        string key = args[i][2..];
        result[key] = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "true";
    }
    return result;
}
