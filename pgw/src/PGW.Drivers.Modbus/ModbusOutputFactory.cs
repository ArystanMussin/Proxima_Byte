using PGW.Core;

namespace PGW.Drivers.Modbus;

/// <summary>Builds a Modbus TCP server interface from a YAML `outputs:` entry (§6, §7.2).</summary>
public static class ModbusOutputFactory
{
    public const string TypeId = "modbus_tcp_server";

    public static (IProtocolInterface Interface, List<RegisterMapEntry> Map, List<string> Errors) Build(
        OutputConfig output, Func<string, object?, CancellationToken, Task<WriteResult>> coreWrite, RingLog log)
    {
        var s = output.Settings;
        var defaultWordOrder = ModbusSourceFactory.ParseWordOrder(s.GetStr("word_order"), WordOrder.ABCD);
        var (map, errors) = RegisterMapBuilder.Build(output, defaultWordOrder);

        var cfg = new ModbusOutputSettings(
            Bind: s.GetStr("bind", "0.0.0.0"),
            Port: s.GetInt("port", 502),
            MaxConnections: s.GetInt("max_connections", 16),
            WhitelistRead: (s.TryGetValue("whitelist", out var wl) && wl is List<object?> l ? l.Select(x => x!.ToString()!).ToList() : new()),
            GlobalReadOnly: s.GetBool("read_only", true),
            IgnoreUnknownUnit: s.GetStr("unknown_unit", "exception") == "ignore");

        var iface = new ModbusTcpServerInterface(cfg, map, coreWrite, log);
        return (iface, map, errors);
    }
}
