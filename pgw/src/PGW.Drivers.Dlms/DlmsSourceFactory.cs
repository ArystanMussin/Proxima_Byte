using PGW.Core;

namespace PGW.Drivers.Dlms;

/// <summary>Builds a DLMS/COSEM client driver + tag definitions from a YAML `sources:` entry. See
/// README "DLMS/COSEM" for config shape, confidence caveats, and what's in/out of scope.</summary>
public static class DlmsSourceFactory
{
    public const string TypeId = "dlms_client";

    public static (IProtocolDriver Driver, DeviceHandle Device, List<(TagDefinition Def, TagAddress Addr)> Tags) Build(SourceConfig src, RingLog log)
    {
        var s = src.Settings;
        var host = s.GetStr("host");
        if (string.IsNullOrWhiteSpace(host))
            throw new FormatException($"source '{src.Name}': dlms_client requires 'host'");

        var auth = ParseAuthentication(src.Name, s.GetStr("security", "none"));
        var passwordStr = s.GetStr("password");
        if (auth == DlmsAuthentication.None && !string.IsNullOrEmpty(passwordStr))
            throw new FormatException($"source '{src.Name}': 'password' is set but security is 'none' — set security: lls to use it");

        var cfg = new DlmsDeviceSettings(
            Host: host,
            Port: s.GetInt("port", 4059),
            ClientAddress: (ushort)s.GetInt("client_address", 1),
            LogicalDeviceAddress: (ushort)s.GetInt("logical_device_address", 1),
            Authentication: auth,
            Password: string.IsNullOrEmpty(passwordStr) ? null : System.Text.Encoding.ASCII.GetBytes(passwordStr),
            MaxPduSize: (ushort)s.GetInt("max_pdu_size", 1024),
            ScanRateMs: s.GetInt("scan_rate_ms", 10000),
            ConnectTimeoutMs: s.GetInt("connect_timeout_ms", 5000),
            ResponseTimeoutMs: s.GetInt("response_timeout_ms", 5000),
            ReconnectMinMs: s.GetInt("reconnect_min_ms", 2000),
            ReconnectMaxMs: s.GetInt("reconnect_max_ms", 30000));

        var device = new DeviceHandle(src.Name, src.Name);
        var defs = new List<(TagDefinition, TagAddress)>();
        var bindings = new List<DlmsTagBinding>();

        foreach (var t in src.Tags)
        {
            var name = t.GetStr("name");
            var tagId = $"{src.Name}.{name}";
            var obis = t.GetStr("obis");
            if (string.IsNullOrWhiteSpace(obis))
                throw new FormatException($"source '{src.Name}', tag '{name}': requires 'obis' (e.g. \"1.0.1.8.0.255\")");
            DlmsCodec.ParseObis(obis); // validates format eagerly, at load time rather than first poll
            var classId = (ushort)t.GetInt("class_id", 3); // 3 = Register, the common case
            var attributeId = (byte)t.GetInt("attribute_id", 2); // 2 = value, on Data/Register/Extended Register
            var type = Enum.Parse<TagDataType>(t.GetStr("type", "float64"), ignoreCase: true);
            var nativeAddress = $"{classId}:{obis}:{attributeId}";

            var def = new TagDefinition(
                Id: tagId,
                DataType: type,
                Access: TagAccess.RO, // GET only — see DlmsClientDriver remarks
                OwnerDevice: device,
                NativeAddress: nativeAddress,
                ScanRateMs: cfg.ScanRateMs,
                Description: OrNull(t.GetStr("description")),
                Units: OrNull(t.GetStr("units")));

            defs.Add((def, new TagAddress(device, nativeAddress)));
            bindings.Add(new DlmsTagBinding(tagId, classId, obis, attributeId));
        }

        var driver = new DlmsClientDriver(cfg, bindings, log);
        return (driver, device, defs);
    }

    private static DlmsAuthentication ParseAuthentication(string sourceName, string s) => s.ToLowerInvariant() switch
    {
        "none" or "" => DlmsAuthentication.None,
        "lls" => DlmsAuthentication.Lls,
        _ => throw new FormatException($"source '{sourceName}': security must be 'none' or 'lls', got '{s}'"),
    };

    private static string? OrNull(string s) => s.Length == 0 ? null : s;
}
