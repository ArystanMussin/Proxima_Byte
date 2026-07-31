using PGW.Core;

namespace PGW.Drivers.Mercury;

public sealed record MercuryDeviceSettings(
    byte Address = 1,
    byte AccessLevel = 1,
    byte[]? Password = null,
    int ScanRateMs = 5000,
    int ResponseTimeoutMs = 1000,
    int InterByteGapMs = 80,
    int ReconnectMinMs = 2000,
    int ReconnectMaxMs = 30000);

/// <summary>Ties a configured tag id to the Mercury parameter it reads (§ "Меркурий" in README).</summary>
public sealed record MercuryTagBinding(string TagId, MercuryParam Param);

/// <summary>
/// Mercury (Меркурий) electricity meter client driver (§ README "Меркурий"). One instance = one meter
/// address on an RS-485 bus. Read-only — the protocol has write commands (parameter/time programming)
/// but they're an admin/commissioning operation, not something a SCADA gateway should expose as a
/// writable tag.
/// </summary>
public sealed class MercuryClientDriver : IProtocolDriver
{
    public string DriverTypeId => MercurySourceFactory.TypeId;
    public DriverCapabilities Capabilities { get; } = new(SupportsWrite: false, SupportsBrowse: false, SupportsSubscription: false);

    private readonly IMercuryTransport _transport;
    private readonly MercuryDeviceSettings _cfg;
    private readonly IReadOnlyList<MercuryTagBinding> _tags;
    private readonly RingLog _log;

    private MercuryProtocol? _protocol;
    private long _errorCount;
    private string? _lastError;
    private double _lastLatencyMs;
    private int _reconnectDelayMs;

    public MercuryClientDriver(IMercuryTransport transport, MercuryDeviceSettings cfg, IReadOnlyList<MercuryTagBinding> tags, RingLog log)
    {
        _transport = transport;
        _cfg = cfg;
        _tags = tags;
        _log = log;
        _reconnectDelayMs = cfg.ReconnectMinMs;
    }

    public async Task<DriverConnectResult> ConnectAsync(DeviceHandle device, CancellationToken ct)
    {
        try
        {
            if (!_transport.IsOpen) await _transport.OpenAsync(ct);
            var protocol = new MercuryProtocol(_transport, _cfg.Address, _cfg.ResponseTimeoutMs, _cfg.InterByteGapMs);
            await protocol.OpenChannelAsync(_cfg.AccessLevel, _cfg.Password ?? DefaultPassword(_cfg.AccessLevel), ct);
            _protocol = protocol;
            _reconnectDelayMs = _cfg.ReconnectMinMs;
            _log.Add(device.ToString(), "connected");
            return DriverConnectResult.Success;
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _log.Add(device.ToString(), $"connect failed: {ex.Message}", "ERROR");
            if (_transport.IsOpen) _transport.Close();
            _protocol = null;
            return DriverConnectResult.Failure(ex.Message);
        }
    }

    public async Task DisconnectAsync(DeviceHandle device, CancellationToken ct)
    {
        if (_protocol is not null)
        {
            try { await _protocol.CloseChannelAsync(ct); }
            catch (Exception) { /* best-effort on shutdown */ }
        }
        if (_transport.IsOpen) _transport.Close();
        _protocol = null;
    }

    public async Task StartPollingAsync(DeviceHandle device, ITagSink sink, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Max(200, _cfg.ScanRateMs)));
        do
        {
            if (_protocol is null && !await TryReconnectAsync(device, ct))
                continue;

            await PollOnceAsync(sink, ct);
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

    private async Task PollOnceAsync(ITagSink sink, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            foreach (var group in _tags.Select(t => MercuryParamInfo.Resolve(t.Param).Group).Distinct())
            {
                var values = await _protocol!.ReadGroupAsync(group, ct);
                foreach (var tag in _tags)
                {
                    var (g, index) = MercuryParamInfo.Resolve(tag.Param);
                    if (g != group) continue;
                    sink.Publish(tag.TagId, values[index], TagQuality.Good, now);
                }
            }
            _lastLatencyMs = sw.Elapsed.TotalMilliseconds;
        }
        catch (Exception ex)
        {
            _errorCount++;
            _lastError = ex.Message;
            foreach (var tag in _tags)
                sink.Publish(tag.TagId, null, TagQuality.Bad, now, QualitySubCode.CommFailure);
            // A framing/CRC error can leave the shared serial line in an unknown state (a partial
            // response from a previous transaction still in flight); safest is to drop the connection
            // and let the poll loop's reconnect path start clean rather than keep trusting a possibly
            // desynchronized stream.
            if (_transport.IsOpen) _transport.Close();
            _protocol = null;
        }
    }

    public Task<WriteResult> WriteAsync(TagAddress address, TagValue value, CancellationToken ct) =>
        Task.FromResult(WriteResult.NotSupported);

    public DriverHealth GetHealth(DeviceHandle device) => new(_protocol is not null, _errorCount, _lastError, _lastLatencyMs);

    private static byte[] DefaultPassword(byte accessLevel) => Enumerable.Repeat(accessLevel, 6).ToArray();
}
