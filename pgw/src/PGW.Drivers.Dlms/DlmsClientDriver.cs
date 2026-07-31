using PGW.Core;

namespace PGW.Drivers.Dlms;

public sealed record DlmsDeviceSettings(
    string Host,
    int Port = 4059,
    ushort ClientAddress = 1,
    ushort LogicalDeviceAddress = 1,
    DlmsAuthentication Authentication = DlmsAuthentication.None,
    byte[]? Password = null,
    ushort MaxPduSize = 1024,
    int ScanRateMs = 10000,
    int ConnectTimeoutMs = 5000,
    int ResponseTimeoutMs = 5000,
    int ReconnectMinMs = 2000,
    int ReconnectMaxMs = 30000);

/// <summary>Ties a configured tag id to the COSEM object it reads (§ README "DLMS/COSEM").</summary>
public sealed record DlmsTagBinding(string TagId, ushort ClassId, string Obis, byte AttributeId);

/// <summary>
/// DLMS/COSEM client driver over the TCP wrapper transport (§ README "DLMS/COSEM"). One instance = one
/// logical device (meter) association. Read-only: GET.request-normal only, one attribute per request —
/// no block transfer/SET/ACTION, which is a meaningfully different scope than "read a value into a tag".
/// </summary>
public sealed class DlmsClientDriver : IProtocolDriver
{
    public string DriverTypeId => DlmsSourceFactory.TypeId;
    public DriverCapabilities Capabilities { get; } = new(SupportsWrite: false, SupportsBrowse: false, SupportsSubscription: false);

    private readonly DlmsDeviceSettings _cfg;
    private readonly IReadOnlyList<DlmsTagBinding> _tags;
    private readonly RingLog _log;

    private DlmsClient? _client;
    private long _errorCount;
    private string? _lastError;
    private double _lastLatencyMs;
    private int _reconnectDelayMs;

    public DlmsClientDriver(DlmsDeviceSettings cfg, IReadOnlyList<DlmsTagBinding> tags, RingLog log)
    {
        _cfg = cfg;
        _tags = tags;
        _log = log;
        _reconnectDelayMs = cfg.ReconnectMinMs;
    }

    public async Task<DriverConnectResult> ConnectAsync(DeviceHandle device, CancellationToken ct)
    {
        var client = new DlmsClient();
        try
        {
            await client.ConnectAsync(_cfg.Host, _cfg.Port, _cfg.ClientAddress, _cfg.LogicalDeviceAddress,
                _cfg.Authentication, _cfg.Password, _cfg.MaxPduSize, _cfg.ConnectTimeoutMs, _cfg.ResponseTimeoutMs, ct);
            _client = client;
            _reconnectDelayMs = _cfg.ReconnectMinMs;
            _log.Add(device.ToString(), "connected");
            return DriverConnectResult.Success;
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _log.Add(device.ToString(), $"connect failed: {ex.Message}", "ERROR");
            await client.DisposeAsync();
            _client = null;
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
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Max(1000, _cfg.ScanRateMs)));
        do
        {
            if (_client is null && !await TryReconnectAsync(device, ct))
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
            foreach (var tag in _tags)
            {
                var value = await _client!.GetAsync(tag.ClassId, tag.Obis, tag.AttributeId, _cfg.ResponseTimeoutMs, ct);
                sink.Publish(tag.TagId, value, TagQuality.Good, now);
            }
            _lastLatencyMs = sw.Elapsed.TotalMilliseconds;
        }
        catch (Exception ex)
        {
            _errorCount++;
            _lastError = ex.Message;
            foreach (var tag in _tags)
                sink.Publish(tag.TagId, null, TagQuality.Bad, now, QualitySubCode.CommFailure);
            // A malformed/partial APDU can desync the request/response stream (this client doesn't
            // pipeline invoke IDs to recover from that) — drop the connection and let the poll loop's
            // reconnect path re-associate cleanly, same reasoning as the Mercury/Modbus RTU drivers.
            await _client!.DisposeAsync();
            _client = null;
        }
    }

    public Task<WriteResult> WriteAsync(TagAddress address, TagValue value, CancellationToken ct) =>
        Task.FromResult(WriteResult.NotSupported);

    public DriverHealth GetHealth(DeviceHandle device) => new(_client?.Connected ?? false, _errorCount, _lastError, _lastLatencyMs);
}
