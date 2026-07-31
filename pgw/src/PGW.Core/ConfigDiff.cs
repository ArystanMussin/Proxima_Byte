using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace PGW.Core;

/// <summary>
/// Structural content comparison for <see cref="SourceConfig"/>/<see cref="OutputConfig"/>, used by
/// hot reload (§7.3) to tell "this entity is byte-for-byte the same as before" from "this entity
/// changed" without writing a hand-rolled recursive deep-equality walker over the JSON-shaped
/// Dictionary/List settings — both are plain mutable classes, so default `==` is reference equality
/// and would treat every entity as "changed" on every reload. Serializing to canonical YAML and
/// comparing strings gets the same answer for free (the same serializer already used to persist
/// config, so there's no risk of it disagreeing with what's actually on disk).
/// </summary>
public static class ConfigDiff
{
    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    public static bool ContentEquals(SourceConfig a, SourceConfig b) =>
        a.Driver == b.Driver && Canonical(a.Settings) == Canonical(b.Settings) && Canonical(a.Tags) == Canonical(b.Tags);

    public static bool ContentEquals(OutputConfig a, OutputConfig b) =>
        a.Interface == b.Interface && Canonical(a.Settings) == Canonical(b.Settings) && Canonical(a.Map) == Canonical(b.Map);

    private static string Canonical(object? value) => Serializer.Serialize(value);
}
