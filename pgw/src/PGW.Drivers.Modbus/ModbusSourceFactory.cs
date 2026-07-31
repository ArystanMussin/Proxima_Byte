using PGW.Core;

namespace PGW.Drivers.Modbus;

/// <summary>Builds a Modbus TCP client driver instance + tag definitions from a YAML `sources:` entry (§4, §7.2).</summary>
public static class ModbusSourceFactory
{
    public const string TypeId = "modbus_tcp_client";

    public static (IProtocolDriver Driver, DeviceHandle Device, List<(TagDefinition Def, TagAddress Addr)> Tags) Build(SourceConfig src, RingLog log)
    {
        var s = src.Settings;
        var oneBased = s.GetBool("one_based");
        var defaultOrder = ParseWordOrder(s.GetStr("word_order"), WordOrder.ABCD);

        var cfg = new ModbusDeviceSettings(
            Host: s.GetStr("host"),
            Port: s.GetInt("port", 502),
            UnitId: (byte)s.GetInt("unit_id", 1),
            ConnectTimeoutMs: s.GetInt("connect_timeout_ms", s.GetInt("timeout_ms", 2000)),
            TimeoutMs: s.GetInt("timeout_ms", 1000),
            Retries: s.GetInt("retries", 2),
            InterRequestDelayMs: s.GetInt("inter_request_delay_ms"),
            AutoDemoteAfter: s.GetInt("auto_demote_after", 3),
            AutoDemoteSeconds: s.GetInt("auto_demote_seconds", 30),
            DefaultWordOrder: defaultOrder,
            OneBased: oneBased,
            GapTolerance: s.GetInt("block_gap_tolerance", 5));

        var device = new DeviceHandle(src.Name, src.Name);
        var parsed = ParseTags(src, device, oneBased, defaultOrder, s.GetInt("scan_rate_ms", 1000), cfg.GapTolerance);

        var driver = ModbusClientDriverFactory.CreateTcp(cfg, parsed.BlocksByScanRate, parsed.WireByAddr, parsed.AllTagIds, log);
        return (driver, device, parsed.Defs);
    }

    /// <summary>The register-addressing/tag-modeling half of building a Modbus source — identical for
    /// every transport (TCP, RTU, ...), since it has nothing to do with how bytes reach the wire.</summary>
    internal readonly record struct ParsedTags(
        List<(TagDefinition Def, TagAddress Addr)> Defs,
        Dictionary<string, TagWireInfo> WireByAddr,
        Dictionary<int, List<ReadBlock>> BlocksByScanRate,
        List<string> AllTagIds);

    internal static ParsedTags ParseTags(SourceConfig src, DeviceHandle device, bool oneBased, WordOrder defaultOrder, int defaultScanRate, int gapTolerance)
    {
        var defs = new List<(TagDefinition, TagAddress)>();
        var wireByAddr = new Dictionary<string, TagWireInfo>();
        var perScanRate = new Dictionary<int, List<(string TagId, ModbusAddress Addr, TagDataType Type)>>();

        foreach (var t in src.Tags)
        {
            var name = t.GetStr("name");
            var tagId = $"{src.Name}.{name}";
            var type = Enum.Parse<TagDataType>(t.GetStr("type", "float32"), ignoreCase: true);
            var addr = ModbusAddress.FromConfig(t, oneBased);
            var scanRate = t.GetInt("scan_rate_ms", defaultScanRate);
            var wordOrder = ParseWordOrder(t.GetStr("word_order"), defaultOrder);
            var access = t.GetStr("access", "RO").Equals("RW", StringComparison.OrdinalIgnoreCase) ? TagAccess.RW : TagAccess.RO;

            var def = new TagDefinition(
                Id: tagId,
                DataType: type,
                Access: access,
                OwnerDevice: device,
                NativeAddress: addr.ToString(),
                ScanRateMs: scanRate,
                Deadband: t.GetDouble("deadband"),
                Scaling: ParseScaling(t.GetMap("scaling")),
                Description: OrNull(t.GetStr("description")),
                Units: OrNull(t.GetStr("units")));

            defs.Add((def, new TagAddress(device, addr.ToString())));
            wireByAddr[addr.ToString()] = new TagWireInfo(type, wordOrder);

            if (!perScanRate.TryGetValue(scanRate, out var list))
                perScanRate[scanRate] = list = new();
            list.Add((tagId, addr, type));
        }

        var blocksByScanRate = perScanRate.ToDictionary(kv => kv.Key, kv => ModbusBlockPlanner.Plan(kv.Value, gapTolerance));
        var allTagIds = defs.Select(d => d.Item1.Id).ToList();
        return new ParsedTags(defs, wireByAddr, blocksByScanRate, allTagIds);
    }

    public static WordOrder ParseWordOrder(string? s, WordOrder fallback) =>
        !string.IsNullOrWhiteSpace(s) && Enum.TryParse<WordOrder>(s, true, out var wo) ? wo : fallback;

    internal static ScalingConfig? ParseScaling(Dictionary<string, object?>? m)
    {
        if (m is null) return null;
        var mode = m.GetStr("mode", "linear").ToLowerInvariant() switch
        {
            "sqrt" => ScalingMode.Sqrt,
            "none" => ScalingMode.None,
            _ => ScalingMode.Linear,
        };
        return new ScalingConfig(mode, m.GetDouble("raw_lo"), m.GetDouble("raw_hi"), m.GetDouble("eu_lo"), m.GetDouble("eu_hi"), m.GetBool("clamp", true));
    }

    private static string? OrNull(string s) => s.Length == 0 ? null : s;
}
