using System.Text.Json.Nodes;

namespace Sim.Net;

/// <summary>
/// Wire format: one JSON object per line. The server is the authority. A joining player gets a snapshot of the
/// world (a save), then a stream of "advance to tick T" and "apply this command at tick T" messages. Every client
/// runs the same deterministic simulation, so they all stay identical; a daily state hash catches any drift.
/// <list type="bullet">
/// <item>Client to server: <c>hello</c> {name, version}, <c>cmd</c> {seq, cmd}, <c>speed</c> {x}, <c>resync</c>.</item>
/// <item>Server to client: <c>welcome</c> {name, settlement, host, save, speed}, <c>adv</c> {tick},
/// <c>apply</c> {tick, from, seq, cmd, ok, error, id}, <c>reject</c> {seq, error}, <c>hash</c> {tick, hash},
/// <c>speed</c> {x}, <c>info</c> {msg}, <c>error</c> {msg}.</item>
/// </list>
/// </summary>
public static class Protocol
{
    public const int Version = 2; // 2: accounts (the "auth" exchange)
    public const int DefaultPort = 7777;
    public const int MaxNameLength = 24;

    public static string Line(JsonObject message) => message.ToJsonString();

    public static JsonObject? Parse(string line)
    {
        try
        {
            return JsonNode.Parse(line) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public static JsonObject Message(string type, params (string Key, JsonNode? Value)[] fields)
    {
        var o = new JsonObject { ["t"] = type };
        foreach (var (k, v) in fields) o[k] = v;
        return o;
    }
}

/// <summary>One connected client, as the host sees it. Sends must be safe to call from the host's thread.</summary>
public interface IPeer
{
    string Address { get; }
    void Send(string line);
    void Close();
}
