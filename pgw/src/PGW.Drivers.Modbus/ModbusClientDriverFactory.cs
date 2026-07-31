using System.Net;
using System.IO.Ports;
using FluentModbus;
using PGW.Core;

namespace PGW.Drivers.Modbus;

public sealed record ModbusDeviceSettings(
    string Host,
    int Port = 502,
    byte UnitId = 1,
    int ConnectTimeoutMs = 2000,
    int TimeoutMs = 1000,
    int Retries = 2,
    int InterRequestDelayMs = 0,
    int AutoDemoteAfter = 3,
    int AutoDemoteSeconds = 30,
    int ReconnectMinMs = 1000,
    int ReconnectMaxMs = 30000,
    WordOrder DefaultWordOrder = WordOrder.ABCD,
    bool OneBased = false,
    int GapTolerance = 5);

public sealed record ModbusRtuDeviceSettings(
    string PortName,
    int BaudRate = 9600,
    Parity Parity = Parity.Even,
    StopBits StopBits = StopBits.One,
    Handshake Handshake = Handshake.None,
    byte UnitId = 1,
    int TimeoutMs = 1000,
    int Retries = 2,
    int InterRequestDelayMs = 20,
    int AutoDemoteAfter = 3,
    int AutoDemoteSeconds = 30,
    int ReconnectMinMs = 1000,
    int ReconnectMaxMs = 30000,
    WordOrder DefaultWordOrder = WordOrder.ABCD,
    bool OneBased = false,
    int GapTolerance = 5);

/// <summary>Builds transport-specific <see cref="ModbusClientDriverBase"/> instances — the only place
/// that knows how each concrete FluentModbus client type opens/closes its connection.</summary>
public static class ModbusClientDriverFactory
{
    public static IProtocolDriver CreateTcp(ModbusDeviceSettings cfg, IReadOnlyDictionary<int, List<ReadBlock>> blocksByScanRate,
        IReadOnlyDictionary<string, TagWireInfo> wireByNativeAddress, IReadOnlyList<string> allTagIds, RingLog log)
    {
        var client = new ModbusTcpClient();

        async Task Connect(ModbusClient c, CancellationToken ct)
        {
            var tcp = (ModbusTcpClient)c;
            tcp.ConnectTimeout = cfg.ConnectTimeoutMs;
            tcp.ReadTimeout = cfg.TimeoutMs;
            tcp.WriteTimeout = cfg.TimeoutMs;
            var ip = IPAddress.TryParse(cfg.Host, out var addr) ? addr : (await Dns.GetHostAddressesAsync(cfg.Host, ct)).First();
            await Task.Run(() => tcp.Connect(new IPEndPoint(ip, cfg.Port)), ct);
        }
        void Disconnect(ModbusClient c) => ((ModbusTcpClient)c).Disconnect();

        var poll = ToPollSettings(cfg);
        return new ModbusClientDriverBase(ModbusSourceFactory.TypeId, client, Connect, Disconnect, poll,
            blocksByScanRate, wireByNativeAddress, allTagIds, log);
    }

    public static IProtocolDriver CreateRtu(ModbusRtuDeviceSettings cfg, IReadOnlyDictionary<int, List<ReadBlock>> blocksByScanRate,
        IReadOnlyDictionary<string, TagWireInfo> wireByNativeAddress, IReadOnlyList<string> allTagIds, RingLog log)
    {
        var client = new ModbusRtuClient();

        Task Connect(ModbusClient c, CancellationToken ct)
        {
            var rtu = (ModbusRtuClient)c;
            rtu.BaudRate = cfg.BaudRate;
            rtu.Parity = cfg.Parity;
            rtu.StopBits = cfg.StopBits;
            rtu.Handshake = cfg.Handshake;
            rtu.ReadTimeout = cfg.TimeoutMs;
            rtu.WriteTimeout = cfg.TimeoutMs;
            rtu.Connect(cfg.PortName);
            return Task.CompletedTask;
        }
        void Disconnect(ModbusClient c) => ((ModbusRtuClient)c).Close();

        var poll = ToPollSettings(cfg);
        return new ModbusClientDriverBase(ModbusRtuSourceFactory.TypeId, client, Connect, Disconnect, poll,
            blocksByScanRate, wireByNativeAddress, allTagIds, log);
    }

    private static ModbusPollSettings ToPollSettings(ModbusDeviceSettings cfg) => new(
        cfg.UnitId, cfg.TimeoutMs, cfg.Retries, cfg.InterRequestDelayMs, cfg.AutoDemoteAfter, cfg.AutoDemoteSeconds,
        cfg.ReconnectMinMs, cfg.ReconnectMaxMs, cfg.DefaultWordOrder, cfg.OneBased, cfg.GapTolerance);

    private static ModbusPollSettings ToPollSettings(ModbusRtuDeviceSettings cfg) => new(
        cfg.UnitId, cfg.TimeoutMs, cfg.Retries, cfg.InterRequestDelayMs, cfg.AutoDemoteAfter, cfg.AutoDemoteSeconds,
        cfg.ReconnectMinMs, cfg.ReconnectMaxMs, cfg.DefaultWordOrder, cfg.OneBased, cfg.GapTolerance);
}
