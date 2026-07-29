using System.Text.Json;

namespace PGW.Core;

/// <summary>Optional last-known-value persistence across restarts (§3.2 p.5, flag `persist_last_values`).</summary>
public static class TagPersistence
{
    private sealed record Row(string Id, object? Value, DateTime Ts);

    public static void Save(TagSpace ts, string path)
    {
        var rows = ts.GetAll().Select(s => new Row(s.Definition.Id, s.Value, s.SourceTimestampUtc)).ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(rows));
    }

    public static void Restore(TagSpace ts, string path)
    {
        if (!File.Exists(path)) return;
        var rows = JsonSerializer.Deserialize<List<Row>>(File.ReadAllText(path)) ?? new();
        foreach (var r in rows)
            ts.RestoreLastValue(r.Id, Unbox(r.Value), r.Ts);
    }

    private static object? Unbox(object? v) => v is JsonElement je ? je.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => je.GetDouble(),
        JsonValueKind.String => je.GetString(),
        _ => null,
    } : v;
}
