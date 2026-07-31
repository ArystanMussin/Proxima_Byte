using PGW.Core;

namespace PGW.Drivers.Iec104;

/// <summary>Builds an IEC 60870-5-104 client driver + tag definitions from a YAML `sources:` entry. See
/// README "IEC 60870-5-104" for config shape.</summary>
public static class Iec104SourceFactory
{
    public const string TypeId = "iec104_client";

    public static (IProtocolDriver Driver, DeviceHandle Device, List<(TagDefinition Def, TagAddress Addr)> Tags) Build(SourceConfig src, RingLog log)
    {
        var s = src.Settings;
        var host = s.GetStr("host");
        if (string.IsNullOrWhiteSpace(host))
            throw new FormatException($"source '{src.Name}': iec104_client requires 'host'");

        var cfg = new Iec104DeviceSettings(
            Host: host,
            Port: s.GetInt("port", 2404),
            CommonAddress: s.GetInt("common_address", 1),
            OriginatorAddress: (byte)s.GetInt("originator_address", 0),
            InterrogationIntervalMs: s.GetInt("interrogation_interval_ms", 30000),
            ConnectTimeoutMs: s.GetInt("connect_timeout_ms", 5000),
            ReconnectMinMs: s.GetInt("reconnect_min_ms", 2000),
            ReconnectMaxMs: s.GetInt("reconnect_max_ms", 30000));

        var device = new DeviceHandle(src.Name, src.Name);
        var defs = new List<(TagDefinition, TagAddress)>();
        var tagIdByIoa = new Dictionary<int, string>();

        foreach (var t in src.Tags)
        {
            var name = t.GetStr("name");
            var tagId = $"{src.Name}.{name}";
            var ioa = t.GetInt("ioa", -1);
            if (ioa < 0) throw new FormatException($"source '{src.Name}', tag '{name}': requires 'ioa' (Information Object Address)");
            var type = Enum.Parse<TagDataType>(t.GetStr("type", "float64"), ignoreCase: true);
            var nativeAddress = $"IOA:{ioa}";

            var def = new TagDefinition(
                Id: tagId,
                DataType: type,
                Access: TagAccess.RO, // see Iec104ClientDriver remarks — control types are out of scope for v1
                OwnerDevice: device,
                NativeAddress: nativeAddress,
                Description: OrNull(t.GetStr("description")),
                Units: OrNull(t.GetStr("units")));

            defs.Add((def, new TagAddress(device, nativeAddress)));
            if (!tagIdByIoa.TryAdd(ioa, tagId))
                throw new FormatException($"source '{src.Name}': IOA {ioa} is used by more than one tag");
        }

        var driver = new Iec104ClientDriver(cfg, tagIdByIoa, log);
        return (driver, device, defs);
    }

    private static string? OrNull(string s) => s.Length == 0 ? null : s;
}
