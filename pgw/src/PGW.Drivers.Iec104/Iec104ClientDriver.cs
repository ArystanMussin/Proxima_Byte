using PGW.Core;

namespace PGW.Drivers.Iec104;

public sealed record Iec104DeviceSettings(
    string Host,
    int Port = 2404,
    int CommonAddress = 1,
    byte OriginatorAddress = 0,
    int InterrogationIntervalMs = 30000,
    int ConnectTimeoutMs = 5000,
    int ReconnectMinMs = 2000,
    int ReconnectMaxMs = 30000);

/// <summary>
/// IEC 60870-5-104 master/client driver (§ README "IEC 60870-5-104"). Unlike Modbus/Mercury this is
/// push-driven, not periodic polling: after connecting, it sends a General Interrogation to get the
/// station's current state, then publishes whatever spontaneous ASDUs the RTU sends in between — the
/// "poll" tick just re-sends General Interrogation as a periodic resync (and doubles as a keepalive) and
/// checks connection health.
///
/// Read-only for v1: the protocol has command types (single/double command, setpoints) for controlling
/// the remote station, but issuing those safely needs select-before-operate semantics and confirmation
/// handling that's a meaningfully different feature from "read telemetry into tags" — out of scope here.
/// </summary>
public sealed class Iec104ClientDriver : IProtocolDriver
{
    public string DriverTypeId => Iec104SourceFactory.TypeId;
    public DriverCapabilities Capabilities { get; } = new(SupportsWrite: false, SupportsBrowse: false, SupportsSubscription: true);

    private readonly Iec104DeviceSettings _cfg;
    private readonly IReadOnlyDictionary<int, string> _tagIdByIoa;
    private readonly RingLog _log;
    private Iec104Client? _client;
    private long _errorCount;
    private string? _lastError;
    private int _reconnectDelayMs;

    public Iec104ClientDriver(Iec104DeviceSettings cfg, IReadOnlyDictionary<int, string> tagIdByIoa, RingLog log)
    {
        _cfg = cfg;
        _tagIdByIoa = tagIdByIoa;
        _log = log;
        _reconnectDelayMs = cfg.ReconnectMinMs;
    }

    public async Task<DriverConnectResult> ConnectAsync(DeviceHandle device, CancellationToken ct)
    {
        try
        {
            var client = new Iec104Client();
            await client.ConnectAsync(_cfg.Host, _cfg.Port, _cfg.ConnectTimeoutMs, ct);
            _client = client;
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

    public async Task DisconnectAsync(DeviceHandle device, CancellationToken ct)
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
            _client = null;
        }
    }

    public async Task StartPollingAsync(DeviceHandle device, ITagSink sink, CancellationToken ct)
    {
        void OnAsdu(Iec104Asdu asdu)
        {
            var now = DateTime.UtcNow;
            foreach (var point in asdu.Points)
            {
                if (!_tagIdByIoa.TryGetValue(point.Ioa, out var tagId)) continue;
                var quality = (point.Quality & Iec104Quality.Invalid) != 0 ? TagQuality.Bad : TagQuality.Good;
                sink.Publish(tagId, point.Value, quality, point.Timestamp ?? now,
                    quality == TagQuality.Bad ? QualitySubCode.ProtocolError : QualitySubCode.None);
            }
        }

        if (_client is not null) _client.AsduReceived += OnAsdu;

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Max(1000, _cfg.InterrogationIntervalMs)));
        do
        {
            if (_client is null)
            {
                var result = await ConnectAsync(device, ct);
                if (!result.Ok)
                {
                    await Task.Delay(_reconnectDelayMs, ct);
                    _reconnectDelayMs = Math.Min(_reconnectDelayMs * 2, _cfg.ReconnectMaxMs);
                    continue;
                }
                _client!.AsduReceived += OnAsdu;
            }

            try
            {
                await _client.SendTestFrAsync(ct);
                await _client.SendGeneralInterrogationAsync(_cfg.CommonAddress, _cfg.OriginatorAddress, ct);
            }
            catch (Exception ex)
            {
                _errorCount++;
                _lastError = ex.Message;
                MarkAllBad(sink);
                await _client.DisposeAsync();
                _client = null;
                continue;
            }

            if (!_client.Connected)
            {
                MarkAllBad(sink);
                await _client.DisposeAsync();
                _client = null;
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    private void MarkAllBad(ITagSink sink)
    {
        var now = DateTime.UtcNow;
        foreach (var tagId in _tagIdByIoa.Values)
            sink.Publish(tagId, null, TagQuality.Bad, now, QualitySubCode.CommFailure);
    }

    public Task<WriteResult> WriteAsync(TagAddress address, TagValue value, CancellationToken ct) =>
        Task.FromResult(WriteResult.NotSupported);

    public DriverHealth GetHealth(DeviceHandle device) => new(_client?.Connected ?? false, _errorCount, _lastError, 0);
}
