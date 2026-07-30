using System.Text.Json;

namespace PGW.Core;

/// <summary>
/// Converts a JSON request body (bound as <c>Dictionary&lt;string, object?&gt;</c>, whose nested values
/// arrive as <see cref="JsonElement"/>) into the same plain-CLR-object shape the YAML loader produces —
/// so <see cref="ConfigLoader.ParseSource"/>/<see cref="ConfigLoader.ParseOutput"/> and every
/// <c>Get*</c> helper in <see cref="ConfigDictExtensions"/> work identically for both.
/// </summary>
public static class JsonUnbox
{
    public static Dictionary<string, object?> ToPlainDict(Dictionary<string, object?> d) =>
        d.ToDictionary(kv => kv.Key, kv => ToPlain(kv.Value));

    public static object? ToPlain(object? v) => v is JsonElement je ? FromElement(je) : v;

    private static object? FromElement(JsonElement je) => je.ValueKind switch
    {
        JsonValueKind.Object => je.EnumerateObject().ToDictionary(p => p.Name, p => FromElement(p.Value)),
        JsonValueKind.Array => je.EnumerateArray().Select(FromElement).ToList(),
        JsonValueKind.String => je.GetString(),
        JsonValueKind.Number => je.TryGetInt64(out var l) ? l : je.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };
}
