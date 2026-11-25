using System.Text.Json;
using System.Text.Json.Nodes;

namespace CoWork.Server;

/// <summary>
/// Events live forever, so a changed shape needs a converter from the old JSON to the new. An event carries the `schema` it was written with;
/// every read passes it through <see cref="ToCurrent"/>, one step at a time (1→2, 2→3 …) up to <see cref="CurrentSchema"/>.
/// To change an event's shape: raise its number in <see cref="Current"/>, add the step to <see cref="Steps"/>, add a test.
/// </summary>
public static class Upcasting
{
    /// <summary>The schema new events of a type are written with. A type that isn't listed is at 1.</summary>
    private static readonly Dictionary<string, int> Current = new()
    {
        // 2: a deleted channel's name is in the event, so the audit can still say which channel it was (round 1 wrote only the id)
        ["ChannelDeleted"] = 2,
    };

    /// <summary>(type, from schema) → how to make the data the next schema.</summary>
    private static readonly Dictionary<(string, int), Func<JsonObject, JsonObject>> Steps = new()
    {
        [("ChannelDeleted", 1)] = d => { d["name"] ??= null; return d; },
    };

    public static int CurrentSchema(string type) => Current.GetValueOrDefault(type, 1);

    public static (int Schema, JsonElement Data) ToCurrent(string type, int schema, JsonElement data)
    {
        var target = CurrentSchema(type);
        if (schema >= target) return (schema, data);
        var node = JsonNode.Parse(data.GetRawText())!.AsObject();
        for (var s = schema; s < target; s++)
            if (Steps.TryGetValue((type, s), out var step)) node = step(node);
        return (target, JsonDocument.Parse(node.ToJsonString()).RootElement.Clone());
    }
}
