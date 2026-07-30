using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace PGW.Core;

/// <summary>
/// Writes a <see cref="GatewayProjectConfig"/> back to the project YAML (§7.1) — the inverse of
/// <see cref="ConfigLoader.Load"/>. Used by the `/config/*` write endpoints so edits made through the
/// dashboard persist exactly like a hand-edited file (and remain hand-editable afterwards).
/// </summary>
public static class ConfigWriter
{
    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    public static void Save(GatewayProjectConfig cfg, string path)
    {
        var root = new Dictionary<string, object?>
        {
            ["gateway"] = new Dictionary<string, object?>
            {
                ["name"] = cfg.Gateway.Name,
                ["log_level"] = cfg.Gateway.LogLevel,
                ["persist_last_values"] = cfg.Gateway.PersistLastValues,
            },
            ["sources"] = cfg.Sources.Select(Flatten).ToList(),
            ["outputs"] = cfg.Outputs.Select(Flatten).ToList(),
        };

        var tmp = path + ".tmp";
        File.WriteAllText(tmp, Serializer.Serialize(root));
        File.Move(tmp, path, overwrite: true);
    }

    private static Dictionary<string, object?> Flatten(SourceConfig s)
    {
        var d = new Dictionary<string, object?>(s.Settings) { ["name"] = s.Name, ["driver"] = s.Driver };
        if (s.Tags.Count > 0) d["tags"] = s.Tags;
        return d;
    }

    private static Dictionary<string, object?> Flatten(OutputConfig o)
    {
        var d = new Dictionary<string, object?>(o.Settings) { ["name"] = o.Name, ["interface"] = o.Interface };
        if (o.Map.Count > 0) d["map"] = o.Map;
        return d;
    }
}
