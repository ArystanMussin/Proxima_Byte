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

public sealed record TagWireInfo(TagDataType Type, WordOrder Order);

/// <summary>Modbus TCP master (§4). One instance = one physical device / TCP connection.</summary>
public sealed class ModbusTcpClientDriver : IProtocolDriver
{
    public string DriverTypeId => "modbus_tcp_client";
    public DriverCapabilities Capabilities { get; } = new(SupportsWrite: true, SupportsBrowse: false, SupportsSubscription: false);

    private readonly ModbusDeviceSettings _cfg;
    private readonly IReadOnlyDictionary<int, List<ReadBlock>> _blocksByScanRate;
    private readonly IReadOnlyDictionary<string, TagWireInfo> _wireByNativeAddress;
    private readonly IReadOnlyList<string> _allTagIds;
    private readonly RingLog _log;
    private readonly ModbusTcpClient _client = new();
    private readonly SemaphoreSlim _io = new(1, 1);

    private long _errorCount;
    private string? _lastError;
    private double _lastLatencyMs;
    private int _consecutiveFailures;
    private int _reconnectDelayMs;
    private DateTime _demotedUntil = DateTime.MinValue;

    public ModbusTcpClientDriver(ModbusDeviceSettings cfg, IReadOnlyDictionary<int, List<ReadBlock>> blocksByScanRate,
        IReadOnlyDictionary<string, TagWireInfo> wireByNativeAddress, IReadOnlyList<string> allTagIds, RingLog log)
    {
        _cfg = cfg;
        _blocksByScanRate = blocksByScanRate;
        _wireByNativeAddress = wireByNativeAddress;
        _allTagIds = allTagIds;
        _log = log;
        _reconnectDelayMs = cfg.ReconnectMinMs;
    }

    public async Task<DriverConnectResult> ConnectAsync(DeviceHandle device, CancellationToken ct)
    {
        try
        {
            _client.ConnectTimeout = _cfg.ConnectTimeoutMs;
            _client.ReadTimeout = _cfg.TimeoutMs;
            _client.WriteTimeout = _cfg.TimeoutMs;
            var ip = System.Net.IPAddress.TryParse(_cfg.Host, out var addr)
                ? addr
                : (await System.Net.Dns.GetHostAddressesAsync(_cfg.Host, ct)).First();
            await Task.Run(() => _client.Connect(new System.Net.IPEndPoint(ip, _cfg.Port)), ct);
            _reconnectDelayMs = _cfg.ReconnectMinMs;
            _log.Add(device.ToString(), "connected");
            return DriverConnectResult.Success;
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _log.Add(device.ToString(), $"connect failed: {ex.Message}", "ERROR");
            return DriverConnectResult.Failure(ex.Message);
        }
    }

    public Task DisconnectAsync(DeviceHandle device, CancellationToken ct)
    {
        if (_client.IsConnected) _client.Disconnect();
        return Task.CompletedTask;
    }

    public async Task StartPollingAsync(DeviceHandle device, ITagSink sink, CancellationToken ct)
    {
        var loops = _blocksByScanRate.Select(kv => PollLoopAsync(device, kv.Key, kv.Value, sink, ct));
        await Task.WhenAll(loops);
    }

    private async Task PollLoopAsync(DeviceHandle device, int scanRateMs, List<ReadBlock> blocks, ITagSink sink, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Max(50, scanRateMs)));
        do
        {
            if (DateTime.UtcNow < _demotedUntil) continue;

            if (!_client.IsConnected && !await TryReconnectAsync(device, ct))
                continue;

            foreach (var block in blocks)
            {
                await PollBlockAsync(block, sink, ct);
                if (_cfg.InterRequestDelayMs > 0) await Task.Delay(_cfg.InterRequestDelayMs, ct);
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    private async Task<bool> TryReconnectAsync(DeviceHandle device, CancellationToken ct)
    {
        var result = await ConnectAsync(device, ct);
        if (result.Ok) return true;
        await Task.Delay(_reconnectDelayMs, ct);
        _reconnectDelayMs = Math.Min(_reconnectDelayMs * 2, _cfg.ReconnectMaxMs);
        return false;
    }

    private async Task PollBlockAsync(ReadBlock block, ITagSink sink, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await _io.WaitAsync(ct);
        try
        {
            if (block.Area is ModbusArea.HoldingRegister or ModbusArea.InputRegister)
            {
                var regs = block.Area == ModbusArea.HoldingRegister
                    ? (await _client.ReadHoldingRegistersAsync<ushort>(_cfg.UnitId, block.Start, block.Length, ct)).ToArray()
                    : (await _client.ReadInputRegistersAsync<ushort>(_cfg.UnitId, block.Start, block.Length, ct)).ToArray();

                foreach (var (tagId, addr, type, bit) in block.Items)
                {
                    var offset = addr.Register - block.Start;
                    var order = _wireByNativeAddress.TryGetValue(addr.ToString(), out var w) ? w.Order : _cfg.DefaultWordOrder;
                    var value = ModbusCodec.Decode(regs.AsSpan(offset, ModbusCodec.RegisterCount(type)), type, order, addr.Bit);
                    sink.Publish(tagId, value, TagQuality.Good, now);
                }
            }
            else
            {
                var packed = block.Area == ModbusArea.Coil
                    ? (await _client.ReadCoilsAsync(_cfg.UnitId, block.Start, block.Length, ct)).ToArray()
                    : (await _client.ReadDiscreteInputsAsync(_cfg.UnitId, block.Start, block.Length, ct)).ToArray();

                foreach (var (tagId, addr, _, _) in block.Items)
                {
                    var value = ModbusCodec.GetPackedBit(packed, addr.Register - block.Start);
                    sink.Publish(tagId, value, TagQuality.Good, now);
                }
            }
            _consecutiveFailures = 0;
            _lastLatencyMs = sw.Elapsed.TotalMilliseconds;
        }
        catch (Exception ex)
        {
            _errorCount++;
            _lastError = ex.Message;
            _consecutiveFailures++;
            foreach (var (tagId, _, _, _) in block.Items)
                sink.Publish(tagId, null, TagQuality.Bad, now, QualitySubCode.CommFailure);

            if (_consecutiveFailures >= _cfg.AutoDemoteAfter)
            {
                _demotedUntil = DateTime.UtcNow.AddSeconds(_cfg.AutoDemoteSeconds);
                _log.Add(block.Area.ToString(), $"device demoted for {_cfg.AutoDemoteSeconds}s after {_consecutiveFailures} failures: {ex.Message}", "WARN");
                foreach (var tagId in _allTagIds)
                    sink.Publish(tagId, null, TagQuality.Bad, now, QualitySubCode.CommFailure);
                if (_client.IsConnected) _client.Disconnect();
            }
        }
        finally
        {
            _io.Release();
        }
    }

    public async Task<WriteResult> WriteAsync(TagAddress address, TagValue value, CancellationToken ct)
    {
        if (!_client.IsConnected) return WriteResult.Failure("not_connected");
        var addr = ModbusAddress.Parse(address.Native);
        if (!_wireByNativeAddress.TryGetValue(address.Native, out var wire))
            return WriteResult.Failure("unknown_address");

        await _io.WaitAsync(ct);
        try
        {
            switch (addr.Area)
            {
                case ModbusArea.Coil:
                    await _client.WriteSingleCoilAsync(_cfg.UnitId, addr.Register, Convert.ToBoolean(value.Value), ct);
                    return WriteResult.Success();

                case ModbusArea.HoldingRegister when addr.Bit is int bitIdx:
                    var current = (await _client.ReadHoldingRegistersAsync<ushort>(_cfg.UnitId, addr.Register, 1, ct)).ToArray();
                    var updated = current[0];
                    if (Convert.ToBoolean(value.Value)) updated |= (ushort)(1 << bitIdx); else updated &= (ushort)~(1 << bitIdx);
                    await _client.WriteSingleRegisterAsync(_cfg.UnitId, addr.Register, updated, ct);
                    return WriteResult.Success();

                case ModbusArea.HoldingRegister:
                    var regs = ModbusCodec.Encode(value.Value, wire.Type, wire.Order, ModbusCodec.RegisterCount(wire.Type));
                    if (regs.Length == 1) await _client.WriteSingleRegisterAsync(_cfg.UnitId, addr.Register, regs[0], ct);
                    else await _client.WriteMultipleRegistersAsync(_cfg.UnitId, addr.Register, regs, ct);
                    return WriteResult.Success();

                default:
                    return WriteResult.Failure("read_only_area");
            }
        }
        catch (Exception ex)
        {
            return WriteResult.Failure(ex.Message);
        }
        finally
        {
            _io.Release();
        }
    }

    public DriverHealth GetHealth(DeviceHandle device) =>
        new(_client.IsConnected && DateTime.UtcNow >= _demotedUntil, _errorCount, _lastError, _lastLatencyMs);
}
