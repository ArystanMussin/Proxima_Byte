using System.IO.Ports;
using PGW.Core;

namespace PGW.Drivers.Modbus;

/// <summary>Builds a Modbus RTU (serial/RS-485) client driver instance + tag definitions from a YAML
/// `sources:` entry — same register/tag model as <see cref="ModbusSourceFactory"/> (§4, §7.2), just over
/// a serial port instead of TCP. This is the transport most industrial meters/PLCs in the field actually
/// speak, TCP being comparatively rare outside newer equipment.</summary>
public static class ModbusRtuSourceFactory
{
    public const string TypeId = "modbus_rtu_client";

    public static (IProtocolDriver Driver, DeviceHandle Device, List<(TagDefinition Def, TagAddress Addr)> Tags) Build(SourceConfig src, RingLog log)
    {
        var s = src.Settings;
        var oneBased = s.GetBool("one_based");
        var defaultOrder = ModbusSourceFactory.ParseWordOrder(s.GetStr("word_order"), WordOrder.ABCD);

        var cfg = new ModbusRtuDeviceSettings(
            // Deliberately not called "port" — that key already means the numeric TCP port on
            // modbus_tcp_client sources, and the dashboard's source form shows every driver's fields
            // together (§ "Веб-панель"); reusing the name would make a serial port name like COM3
            // land in a number input.
            PortName: s.GetStr("serial_port"),
            BaudRate: s.GetInt("baud_rate", 9600),
            Parity: ParseParity(s.GetStr("parity", "even")),
            StopBits: ParseStopBits(s.GetStr("stop_bits", "one")),
            Handshake: Handshake.None,
            UnitId: (byte)s.GetInt("unit_id", 1),
            TimeoutMs: s.GetInt("timeout_ms", 1000),
            Retries: s.GetInt("retries", 2),
            // Serial buses are half-duplex and often shared by several devices; a small gap between
            // requests avoids hammering a slow meter back-to-back the way a TCP socket tolerates fine.
            InterRequestDelayMs: s.GetInt("inter_request_delay_ms", 20),
            AutoDemoteAfter: s.GetInt("auto_demote_after", 3),
            AutoDemoteSeconds: s.GetInt("auto_demote_seconds", 30),
            DefaultWordOrder: defaultOrder,
            OneBased: oneBased,
            GapTolerance: s.GetInt("block_gap_tolerance", 5));

        if (string.IsNullOrWhiteSpace(cfg.PortName))
            throw new FormatException($"source '{src.Name}': modbus_rtu_client requires 'serial_port' (e.g. COM3 or /dev/ttyUSB0)");

        var device = new DeviceHandle(src.Name, src.Name);
        var parsed = ModbusSourceFactory.ParseTags(src, device, oneBased, defaultOrder, s.GetInt("scan_rate_ms", 1000), cfg.GapTolerance);

        var driver = ModbusClientDriverFactory.CreateRtu(cfg, parsed.BlocksByScanRate, parsed.WireByAddr, parsed.AllTagIds, log);
        return (driver, device, parsed.Defs);
    }

    private static Parity ParseParity(string s) => s.ToLowerInvariant() switch
    {
        "none" => Parity.None,
        "odd" => Parity.Odd,
        "mark" => Parity.Mark,
        "space" => Parity.Space,
        _ => Parity.Even,
    };

    private static StopBits ParseStopBits(string s) => s.ToLowerInvariant() switch
    {
        "none" => StopBits.None,
        "two" or "2" => StopBits.Two,
        "onepointfive" or "1.5" => StopBits.OnePointFive,
        _ => StopBits.One,
    };
}
