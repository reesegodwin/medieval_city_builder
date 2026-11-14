using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sim.Core.Commands;

/// <summary>
/// Turns commands into JSON and back, so they can travel between clients and servers (and later be logged
/// for replays). Only the command types listed here can be sent.
/// </summary>
public static class CommandCodec
{
    private static readonly Type[] Types =
    {
        typeof(PlaceBuildingCommand), typeof(SetJobPriorityCommand), typeof(SetReserveCommand),
        typeof(SetProductionLimitCommand), typeof(SetAutoBuildCommand), typeof(PlaceRoadCommand),
        typeof(RemoveRoadCommand), typeof(DemolishBuildingCommand), typeof(ClaimTerritoryCommand),
        typeof(ReleaseTerritoryCommand), typeof(ExpandTerritoryCommand), typeof(SetAutoExpandTerritoryCommand),
        typeof(UpgradeTownCentreCommand), typeof(FoundSettlementCommand), typeof(SetAutoUpgradeHomesCommand),
        typeof(SetDisastersCommand), typeof(PlaceWallCommand), typeof(PlaceGateCommand), typeof(RemoveWallCommand),
        typeof(PlaceTowerCommand), typeof(PlaceGatehouseCommand),
        typeof(War.RecruitSquadCommand), typeof(War.DisbandSquadCommand), typeof(War.PromoteGeneralCommand),
        typeof(War.SetRaidsCommand), typeof(War.SetMusterCommand),
        typeof(Economy.SetGovernmentCommand), typeof(Systems.SetCrimeCommand),
        typeof(War.SallyCommand), typeof(War.PayTributeCommand),
        typeof(Diplomacy.ProposeCommand), typeof(Diplomacy.AnswerProposalCommand), typeof(Diplomacy.EndTreatyCommand),
        typeof(Diplomacy.SendGiftCommand), typeof(War.DeclareWarCommand), typeof(War.MarchCommand), typeof(War.RecallArmyCommand),
        typeof(Trade.PostOfferCommand), typeof(Trade.CancelOfferCommand), typeof(Trade.AcceptOfferCommand),
        typeof(Trade.ProposeRouteCommand), typeof(Trade.AcceptRouteCommand), typeof(Trade.EndRouteCommand),
    };

    private static readonly Dictionary<string, Type> ByName = Types.ToDictionary(t => t.Name);

    private static readonly JsonSerializerOptions Options = new() { IncludeFields = true };

    public static JsonObject Encode(ICommand command)
    {
        var type = command.GetType();
        if (!ByName.ContainsKey(type.Name)) throw new ArgumentException($"{type.Name} can't be sent over the network");
        return new JsonObject
        {
            ["type"] = type.Name,
            ["data"] = JsonSerializer.SerializeToNode(command, type, Options),
        };
    }

    public static ICommand Decode(JsonNode node)
    {
        string name = node["type"]?.GetValue<string>() ?? throw new InvalidDataException("Command without a type");
        if (!ByName.TryGetValue(name, out var type)) throw new InvalidDataException($"Unknown command '{name}'");
        return (ICommand)(node["data"].Deserialize(type, Options) ?? throw new InvalidDataException("Empty command"));
    }
}
