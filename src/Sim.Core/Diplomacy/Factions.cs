using Sim.Core.Events;
using Sim.Core.Settlements;
using Sim.Core.Simulation;

namespace Sim.Core.Diplomacy;

/// <summary>How an NPC faction tends to behave (factions.json "personalities").</summary>
public sealed class PersonalityDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>0..1: how readily it demands, threatens and goes to war.</summary>
    public double Aggression { get; set; } = 0.5;

    /// <summary>0..1: how much it values trade pacts.</summary>
    public double TradeBias { get; set; } = 0.5;

    /// <summary>0..1: how much it fears stronger neighbours (seeks pacts, offers vassalage).</summary>
    public double Caution { get; set; } = 0.5;
}

/// <summary>NPC faction tuning (factions.json).</summary>
public sealed class FactionRules
{
    /// <summary>NPC towns a new world made in the game or on a server starts with (the in-core default is none).</summary>
    public int StartTowns { get; set; } = 3;

    /// <summary>Most NPC towns a world grows to.</summary>
    public int MaxNpcTowns { get; set; } = 6;

    /// <summary>Another NPC town is founded this often (game days), up to <see cref="MaxNpcTowns"/>.</summary>
    public int NewTownEveryDays { get; set; } = 360;

    public List<string> HouseWords { get; set; } = new() { "House" };
    public List<string> Colours { get; set; } = new() { "#b33a3a" };
    public List<PersonalityDef> Personalities { get; set; } = new() { new() { Id = "cautious", Name = "Cautious" } };

    public DiplomacyRules Diplomacy { get; set; } = new();
    public War.FactionWarRules War { get; set; } = new();

    public PersonalityDef Personality(string id) => Personalities.FirstOrDefault(p => p.Id == id) ?? Personalities[0];
}

/// <summary>A faction: a player's realm or a computer-run one. Towns belong to one (<see cref="Settlement.FactionId"/>).</summary>
public sealed class Faction
{
    public int Id { get; set; }
    public string Name { get; set; } = "";

    /// <summary>Banner colour, "#rrggbb".</summary>
    public string Colour { get; set; } = "#ffffff";

    /// <summary>Run by the computer (its towns are governed, and it makes its own decisions).</summary>
    public bool IsNpc { get; set; }

    /// <summary>The player who leads it (null for NPC factions and single-player towns).</summary>
    public string? PlayerName { get; set; }

    /// <summary>An NPC faction's temperament (factions.json).</summary>
    public string Personality { get; set; } = "";

    /// <summary>Day it was founded.</summary>
    public long FoundedDay { get; set; }
}

/// <summary>Factions and (later) their relations, treaties and proposals (saved with the world).</summary>
public sealed class DiplomacyBook
{
    public List<Faction> Factions { get; set; } = new();

    /// <summary>Opinions between pairs of factions (A &lt; B, sorted).</summary>
    public List<Relation> Relations { get; set; } = new();

    public List<Treaty> Treaties { get; set; } = new();
    public List<Proposal> Proposals { get; set; } = new();
    public List<War.WarState> Wars { get; set; } = new();

    public Faction? Find(int id)
    {
        foreach (var f in Factions)
            if (f.Id == id) return f;
        return null;
    }
}

/// <summary>
/// Factions (Phase 5). Every town belongs to one: a player's own, or a computer-run realm. Worlds with
/// <see cref="WorldSettings.NpcTowns"/> above zero start with that many NPC towns on the founding ring around the
/// first town, and found another every <see cref="FactionRules.NewTownEveryDays"/> up to
/// <see cref="FactionRules.MaxNpcTowns"/>. NPC towns are run by the same governor as any "Auto" town.
/// </summary>
public static class Factions
{
    public static Faction? Of(GameWorld world, Settlement s) => world.Diplomacy.Find(s.FactionId);

    public static bool IsNpc(GameWorld world, Settlement s) => Of(world, s)?.IsNpc == true;

    public static IEnumerable<Settlement> TownsOf(GameWorld world, Faction f) => world.Settlements.Where(s => s.FactionId == f.Id);

    /// <summary>NPC towns the world should have by now.</summary>
    public static int WantedNpcTowns(GameWorld world) => WantedNpcTowns(world, world.Time.Day);

    /// <summary>NPC towns the world should have on a given day.</summary>
    public static int WantedNpcTowns(GameWorld world, long day)
    {
        int start = world.Settings.NpcTowns;
        if (start <= 0) return 0;
        var rules = world.Data.Factions;
        long grown = rules.NewTownEveryDays > 0 ? day / rules.NewTownEveryDays : 0;
        int cap = Math.Max(start, rules.MaxNpcTowns); // a world asked for more at the start keeps them
        return (int)Math.Min(cap, start + grown);
    }

    public static void Daily(GameWorld world)
    {
        EnsureFactions(world);
        int npc = world.Settlements.Count(s => IsNpc(world, s));
        if (npc < WantedNpcTowns(world) && world.Settlements.Count > 0) FoundNpcTown(world); // one a day at most
        if (world.Diplomacy.Factions.Count > 1)
        {
            Statecraft.Daily(world);
            War.FactionWar.Daily(world);
        }
    }

    /// <summary>Gives every town without a faction its own (a player's realm, or a single-player town's).</summary>
    public static void EnsureFactions(GameWorld world)
    {
        foreach (var s in world.Settlements)
        {
            if (s.FactionId != 0 && world.Diplomacy.Find(s.FactionId) != null) continue;
            var f = NewFaction(world, isNpc: false, s.OwnerId != null ? $"Realm of {s.OwnerId}" : $"Realm of {s.Name}");
            f.PlayerName = s.OwnerId;
            s.FactionId = f.Id;
        }
    }

    private static Faction NewFaction(GameWorld world, bool isNpc, string? name = null)
    {
        var rules = world.Data.Factions;
        int index = world.Diplomacy.Factions.Count;
        var f = new Faction
        {
            Id = world.NextId(),
            IsNpc = isNpc,
            Colour = rules.Colours[index % rules.Colours.Count],
            FoundedDay = world.Time.Day,
        };
        if (isNpc)
        {
            string word = rules.HouseWords[world.Rng.Next(rules.HouseWords.Count)];
            // A family no other realm bears.
            string family = Citizens.Names.FamilyName(world.Rng);
            for (int tries = 0; tries < 20 && world.Diplomacy.Factions.Any(o => o.Name.EndsWith(" " + family)); tries++)
                family = Citizens.Names.FamilyName(world.Rng);
            f.Name = name ?? $"{word} {family}";
            f.Personality = rules.Personalities[world.Rng.Next(rules.Personalities.Count)].Id;
        }
        else f.Name = name ?? "Realm";
        world.Diplomacy.Factions.Add(f);
        return f;
    }

    /// <summary>Founds a new NPC realm with one town on the next free spot of the founding ring.</summary>
    public static Settlement FoundNpcTown(GameWorld world)
    {
        var origin = world.Settlements[0];
        var (dx, dy) = Commands.FoundSettlementCommand.SpotOffset(world.Settlements.Count);
        // A name no other town has.
        string name = Citizens.Names.Place(world.Rng);
        for (int tries = 0; tries < 20 && world.Settlements.Any(s => s.Name == name); tries++) name = Citizens.Names.Place(world.Rng);
        if (world.Settlements.Any(s => s.Name == name)) name = $"New {name}";
        var town = SettlementFounder.Found(world, name, nearX: origin.CenterX + dx * Commands.FoundSettlementCommand.Spacing,
            nearY: origin.CenterY + dy * Commands.FoundSettlementCommand.Spacing);
        var faction = NewFaction(world, isNpc: true);
        town.FactionId = faction.Id;
        town.Rules.AutoBuild = true;
        town.Rules.AutoExpandTerritory = true;
        world.Log(EventKind.SettlementFounded, town, $"{faction.Name} ({world.Data.Factions.Personality(faction.Personality).Name.ToLowerInvariant()}) " +
                                                     $"founded {town.Name} nearby");
        return town;
    }
}
