using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace PGW.Core;

public sealed class GatewaySection
{
    public string Name { get; set; } = "PGW";
    public string LogLevel { get; set; } = "INFO";
    public bool PersistLastValues { get; set; }
}

/// <summary>
/// One source (channel+device). <see cref="Settings"/>/<see cref="Tags"/> stay untyped here — each
/// driver parses only the keys it knows via <see cref="ConfigDictExtensions"/>, so the core never needs
/// to know a single Modbus- or OPC UA-specific field name.
/// </summary>
public sealed class SourceConfig
{
    public string Name { get; set; } = "";
    public string Driver { get; set; } = "";
    public Dictionary<string, object?> Settings { get; set; } = new();
    public List<Dictionary<string, object?>> Tags { get; set; } = new();
}

public sealed class OutputConfig
{
    public string Name { get; set; } = "";
    public string Interface { get; set; } = "";
    public Dictionary<string, object?> Settings { get; set; } = new();
    public List<Dictionary<string, object?>> Map { get; set; } = new();
}

public sealed class GatewayProjectConfig
{
    public GatewaySection Gateway { get; set; } = new();
    public List<SourceConfig> Sources { get; set; } = new();
    public List<OutputConfig> Outputs { get; set; } = new();
}

public static class ConfigLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly string[] SourceKnownKeys = { "name", "driver", "tags" };
    private static readonly string[] OutputKnownKeys = { "name", "interface", "map" };

    public static GatewayProjectConfig Load(string path)
    {
        var yaml = File.ReadAllText(path);
        var root = Deserializer.Deserialize<Dictionary<string, object?>>(yaml) ?? new();

        var cfg = new GatewayProjectConfig();
        if (root.TryGetValue("gateway", out var gw) && gw is Dictionary<object, object?> gwMap)
        {
            var d = Normalize(gwMap);
            cfg.Gateway.Name = d.GetStr("name", cfg.Gateway.Name);
            cfg.Gateway.LogLevel = d.GetStr("log_level", cfg.Gateway.LogLevel);
            cfg.Gateway.PersistLastValues = d.GetBool("persist_last_values");
        }

        if (root.TryGetValue("sources", out var srcs) && srcs is List<object> srcList)
        {
            foreach (var raw in srcList.OfType<Dictionary<object, object?>>())
            {
                var d = Normalize(raw);
                cfg.Sources.Add(new SourceConfig
                {
                    Name = d.GetStr("name"),
                    Driver = d.GetStr("driver"),
                    Tags = ExtractMapList(d, "tags"),
                    Settings = d.Where(kv => !SourceKnownKeys.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value),
                });
            }
        }

        if (root.TryGetValue("outputs", out var outs) && outs is List<object> outList)
        {
            foreach (var raw in outList.OfType<Dictionary<object, object?>>())
            {
                var d = Normalize(raw);
                cfg.Outputs.Add(new OutputConfig
                {
                    Name = d.GetStr("name"),
                    Interface = d.GetStr("interface"),
                    Map = ExtractMapList(d, "map"),
                    Settings = d.Where(kv => !OutputKnownKeys.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value),
                });
            }
        }

        return cfg;
    }

    /// <summary>
    /// Extracts a list-of-maps value (e.g. `tags:`/`map:`). <paramref name="d"/> was already produced by
    /// <see cref="Normalize"/>, which unwraps nested structures recursively, so entries here are already
    /// plain <c>Dictionary&lt;string, object?&gt;</c> — no further normalization needed.
    /// </summary>
    private static List<Dictionary<string, object?>> ExtractMapList(Dictionary<string, object?> d, string key) =>
        d.TryGetValue(key, out var v) && v is List<object?> list
            ? list.OfType<Dictionary<string, object?>>().ToList()
            : new();

    private static Dictionary<string, object?> Normalize(Dictionary<object, object?> src) =>
        src.ToDictionary(kv => kv.Key.ToString()!, kv => Unwrap(kv.Value));

    private static object? Unwrap(object? v) => v switch
    {
        Dictionary<object, object?> m => Normalize(m),
        List<object> l => l.Select(Unwrap).ToList(),
        _ => v,
    };
}

public static class ConfigDictExtensions
{
    public static string GetStr(this IReadOnlyDictionary<string, object?> d, string key, string def = "") =>
        d.TryGetValue(key, out var v) && v is not null ? v.ToString()! : def;

    public static int GetInt(this IReadOnlyDictionary<string, object?> d, string key, int def = 0) =>
        d.TryGetValue(key, out var v) && v is not null ? Convert.ToInt32(v) : def;

    public static double GetDouble(this IReadOnlyDictionary<string, object?> d, string key, double def = 0) =>
        d.TryGetValue(key, out var v) && v is not null ? Convert.ToDouble(v) : def;

    public static bool GetBool(this IReadOnlyDictionary<string, object?> d, string key, bool def = false) =>
        d.TryGetValue(key, out var v) && v is not null ? Convert.ToBoolean(v) : def;

    public static Dictionary<string, object?>? GetMap(this IReadOnlyDictionary<string, object?> d, string key) =>
        d.TryGetValue(key, out var v) ? v as Dictionary<string, object?> : null;
}

public static class ConfigValidator
{
    public static List<string> Validate(GatewayProjectConfig cfg)
    {
        var errors = new List<string>();

        var sourceNames = new HashSet<string>();
        foreach (var s in cfg.Sources)
        {
            if (string.IsNullOrWhiteSpace(s.Name)) errors.Add("source without a name");
            else if (!sourceNames.Add(s.Name)) errors.Add($"duplicate source name '{s.Name}'");

            if (string.IsNullOrWhiteSpace(s.Driver)) errors.Add($"source '{s.Name}' has no driver");

            var tagNames = new HashSet<string>();
            foreach (var t in s.Tags)
            {
                var name = t.GetStr("name");
                if (string.IsNullOrWhiteSpace(name)) errors.Add($"source '{s.Name}' has a tag without a name");
                else if (!tagNames.Add(name)) errors.Add($"duplicate tag '{s.Name}.{name}'");
            }
        }

        var outputNames = new HashSet<string>();
        foreach (var o in cfg.Outputs)
        {
            if (string.IsNullOrWhiteSpace(o.Name)) errors.Add("output without a name");
            else if (!outputNames.Add(o.Name)) errors.Add($"duplicate output name '{o.Name}'");

            if (string.IsNullOrWhiteSpace(o.Interface)) errors.Add($"output '{o.Name}' has no interface");

            foreach (var m in o.Map)
            {
                var tag = m.GetStr("tag");
                if (string.IsNullOrWhiteSpace(tag)) { errors.Add($"output '{o.Name}' has a map entry without 'tag'"); continue; }
                if (!sourceNames.Contains(tag.Split('.')[0]))
                    errors.Add($"output '{o.Name}' maps unknown tag '{tag}'");
            }
        }

        return errors;
    }
}
