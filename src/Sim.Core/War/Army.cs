namespace Sim.Core.War;

/// <summary>A kind of soldier (units.json): gear each soldier needs and how well they fight.</summary>
public sealed class UnitDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>Gear each soldier takes from stock when recruited (returned on disbanding).</summary>
    public Dictionary<string, int> Gear { get; set; } = new();

    public double Attack { get; set; } = 1;
    public double Defence { get; set; } = 1;

    /// <summary>Shoots from behind the line (and gains most from walls and towers).</summary>
    public bool Ranged { get; set; }

    /// <summary>A soldier's pay a day (from the treasury, into their family's purse); dearer gear, better pay.</summary>
    public double PayPerSoldierDay { get; set; }

    /// <summary>Raiders' kinds: not recruited by towns.</summary>
    public bool Raider { get; set; }
}

/// <summary>A general's trait (units.json): bonuses as fractions (0.1 = +10%).</summary>
public sealed class GeneralTraitDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public double Attack { get; set; }
    public double Defence { get; set; }
    public double Morale { get; set; }
    public double Ranged { get; set; }

    /// <summary>Extra benefit from (or against) walls.</summary>
    public double Walls { get; set; }
}

/// <summary>Army tuning (units.json).</summary>
public sealed class WarRules
{
    public List<UnitDef> Units { get; set; } = new();
    public int SquadSize { get; set; } = 10;
    public int SquadsPerBarracks { get; set; } = 4;
    public List<GeneralTraitDef> GeneralTraits { get; set; } = new();
    public RaidRules Raids { get; set; } = new();
    public BattleRules Battle { get; set; } = new();
    public SiegeRules Siege { get; set; } = new();

    /// <summary>Governed towns keep this share of their people as a standing garrison...</summary>
    public double GarrisonShare { get; set; } = 0.04;

    /// <summary>...from this size on.</summary>
    public int GarrisonPopulation { get; set; } = 150;

    public UnitDef? Unit(string id) => Units.FirstOrDefault(u => u.Id == id);
    public GeneralTraitDef? Trait(string id) => GeneralTraits.FirstOrDefault(t => t.Id == id);
}

/// <summary>A squad of townsfolk under arms. Its soldiers are real citizens: the dead stay dead.</summary>
public sealed class Squad
{
    public int Id { get; set; }
    public string UnitId { get; set; } = "";
    public List<int> SoldierIds { get; set; } = new();

    /// <summary>Experience from battles; veterans fight better (see BattleResolver).</summary>
    public double Xp { get; set; }

    /// <summary>Raised for an emergency (call to arms): sent home when the threat passes.</summary>
    public bool Levy { get; set; }
}

/// <summary>The settlement's general: a citizen with skill that grows with battles, and traits.</summary>
public sealed class General
{
    public int CitizenId { get; set; }
    public double Skill { get; set; } = 10;
    public double Xp { get; set; }
    public List<string> Traits { get; set; } = new();
    public int Battles { get; set; }
}
