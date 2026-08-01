using System.IO.Ports;
using PGW.Core;

namespace PGW.Drivers.Mercury;

/// <summary>Builds a Mercury (Меркурий) meter client driver + tag definitions from a YAML `sources:`
/// entry. See README "Меркурий" for config shape, confidence caveats, and which parameters are supported.</summary>
public static class MercurySourceFactory
{
    public const string TypeId = "mercury_client";

    public static (IProtocolDriver Driver, DeviceHandle Device, List<(TagDefinition Def, TagAddress Addr)> Tags) Build(SourceConfig src, RingLog log, SerialTransportRegistry? transports = null)
    {
        var s = src.Settings;
        var portName = s.GetStr("serial_port");
        if (string.IsNullOrWhiteSpace(portName))
            throw new FormatException($"source '{src.Name}': mercury_client requires 'serial_port' (e.g. COM3 or /dev/ttyUSB0)");

        var accessLevel = (byte)s.GetInt("access_level", 1);
        var passwordStr = s.GetStr("password");

        var cfg = new MercuryDeviceSettings(
            Address: (byte)s.GetInt("unit_id", 1),
            AccessLevel: accessLevel,
            Password: string.IsNullOrEmpty(passwordStr) ? null : ParsePassword(src.Name, passwordStr),
            ScanRateMs: s.GetInt("scan_rate_ms", 5000),
            ResponseTimeoutMs: s.GetInt("timeout_ms", 1000),
            InterByteGapMs: s.GetInt("inter_byte_gap_ms", 80),
            ReconnectMinMs: s.GetInt("reconnect_min_ms", 2000),
            ReconnectMaxMs: s.GetInt("reconnect_max_ms", 30000));

        var shared = ResolveSharedTransport(src, transports);
        IMercuryTransport transport = shared is not null
            ? new SharedSerialMercuryTransport(shared)
            : new SerialMercuryTransport(
                portName,
                s.GetInt("baud_rate", 9600),
                ParseParity(s.GetStr("parity", "none")),
                ParseStopBits(s.GetStr("stop_bits", "one")));

        var device = new DeviceHandle(src.Name, src.Name);
        var defs = new List<(TagDefinition, TagAddress)>();
        var bindings = new List<MercuryTagBinding>();

        foreach (var t in src.Tags)
        {
            var name = t.GetStr("name");
            var tagId = $"{src.Name}.{name}";
            var paramName = MercuryParamInfo.ParseParam(t.GetStr("param"));
            var param = Enum.Parse<MercuryParam>(paramName);
            var type = Enum.Parse<TagDataType>(t.GetStr("type", "float64"), ignoreCase: true);

            var def = new TagDefinition(
                Id: tagId,
                DataType: type,
                Access: TagAccess.RO, // metering values only — see MercuryClientDriver's WriteAsync
                OwnerDevice: device,
                NativeAddress: paramName,
                ScanRateMs: cfg.ScanRateMs,
                Description: OrNull(t.GetStr("description")),
                Units: OrNull(t.GetStr("units")));

            defs.Add((def, new TagAddress(device, paramName)));
            bindings.Add(new MercuryTagBinding(tagId, param));
        }

        var driver = new MercuryClientDriver(transport, cfg, bindings, log);
        return (driver, device, defs);
    }

    /// <summary>§4.1.1: resolves this source's shared bus (if <c>serial_transport</c> is set) via
    /// <paramref name="transports"/>, or returns null for a dedicated per-source port. Split out from
    /// <see cref="Build"/> so config validation can probe for baud/parity/stop_bits mismatches against a
    /// throwaway registry before anything actually opens a port or builds a driver.</summary>
    public static SerialTransport? ResolveSharedTransport(SourceConfig src, SerialTransportRegistry? transports)
    {
        var s = src.Settings;
        var name = s.GetStr("serial_transport");
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (transports is null)
            throw new InvalidOperationException($"source '{src.Name}': serial_transport is set but no registry was provided");

        return transports.GetOrCreate(
            name,
            s.GetStr("serial_port"),
            s.GetInt("baud_rate", 9600),
            ParseParity(s.GetStr("parity", "none")),
            ParseStopBits(s.GetStr("stop_bits", "one")));
    }

    private static byte[] ParsePassword(string sourceName, string password)
    {
        if (password.Length != 6 || password.Any(c => c is < '0' or > '9'))
            throw new FormatException($"source '{sourceName}': mercury password must be exactly 6 digits (0-9), e.g. \"123456\"");
        return password.Select(c => (byte)(c - '0')).ToArray();
    }

    private static Parity ParseParity(string s) => s.ToLowerInvariant() switch
    {
        "even" => Parity.Even,
        "odd" => Parity.Odd,
        "mark" => Parity.Mark,
        "space" => Parity.Space,
        _ => Parity.None,
    };

    private static StopBits ParseStopBits(string s) => s.ToLowerInvariant() switch
    {
        "two" or "2" => StopBits.Two,
        "onepointfive" or "1.5" => StopBits.OnePointFive,
        _ => StopBits.One,
    };

    private static string? OrNull(string s) => s.Length == 0 ? null : s;
}
