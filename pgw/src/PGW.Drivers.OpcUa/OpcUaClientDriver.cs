using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using PGW.Core;

namespace PGW.Drivers.OpcUa;

public sealed record OpcUaTagInfo(string NodeId, TagDataType Type);

/// <summary>OPC UA client driver (§5): subscription-based read, Write, certificate-file trust model.</summary>
public sealed class OpcUaClientDriver : IProtocolDriver
{
    public string DriverTypeId => "opcua_client";
    public DriverCapabilities Capabilities { get; } = new(SupportsWrite: true, SupportsBrowse: false, SupportsSubscription: true);

    private readonly OpcUaDeviceSettings _cfg;
    private readonly IReadOnlyDictionary<string, OpcUaTagInfo> _tagsById;
    private readonly Dictionary<string, (string TagId, TagDataType Type)> _byNodeId;
    private readonly IReadOnlyList<string> _allTagIds;
    private readonly RingLog _log;

    private Session? _session;
    private Subscription? _subscription;
    private ITagSink? _sink;
    private DeviceHandle _device;
    private long _errorCount;
    private string? _lastError;
    private int _reconnectDelayMs;
    private volatile bool _reconnecting;

    public OpcUaClientDriver(OpcUaDeviceSettings cfg, IReadOnlyDictionary<string, OpcUaTagInfo> tags, RingLog log)
    {
        _cfg = cfg;
        _tagsById = tags;
        _byNodeId = tags.ToDictionary(kv => kv.Value.NodeId, kv => (kv.Key, kv.Value.Type));
        _allTagIds = tags.Keys.ToList();
        _log = log;
        _reconnectDelayMs = cfg.ReconnectMinMs;
    }

    public async Task<DriverConnectResult> ConnectAsync(DeviceHandle device, CancellationToken ct)
    {
        _device = device;
        try
        {
            var appConfig = await BuildApplicationConfigurationAsync(device.Device, ct);
            var useSecurity = _cfg.SecurityMode != "None";
            var endpoint = await Task.Run(() => CoreClientUtils.SelectEndpoint(appConfig, _cfg.EndpointUrl, useSecurity), ct);
            var configuredEndpoint = new ConfiguredEndpoint(null, endpoint, EndpointConfiguration.Create(appConfig));

            IUserIdentity identity = _cfg.AuthMode == OpcUaAuthMode.UsernamePassword
                ? new UserIdentity(_cfg.Username ?? "", System.Text.Encoding.UTF8.GetBytes(_cfg.Password ?? ""))
                : new UserIdentity();

            _session = await Session.CreateAsync(appConfig, null, configuredEndpoint, false, false,
                $"PGW-{device}", (uint)_cfg.SessionTimeoutMs, identity, null, ct);
            _session.KeepAliveInterval = _cfg.KeepAliveIntervalMs;
            _session.KeepAlive += OnKeepAlive;

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

    /// <summary>File-based certificate trust model per §5.1: certs/own, certs/trusted, certs/rejected under the device's cert path.</summary>
    private async Task<ApplicationConfiguration> BuildApplicationConfigurationAsync(string deviceName, CancellationToken ct)
    {
        var app = new ApplicationInstance
        {
            ApplicationName = $"PGW-{deviceName}",
            ApplicationType = ApplicationType.Client,
        };

        var config = await app.Build($"urn:{Environment.MachineName}:pgw:{deviceName}", "uri:pgw:gateway")
            .AsClient()
            .AddSecurityConfiguration($"CN=PGW-{deviceName}, O=PGW", _cfg.CertsPath)
            .SetAutoAcceptUntrustedCertificates(_cfg.AutoAcceptUntrustedCertificates)
            .SetRejectSHA1SignedCertificates(false)
            .SetMinimumCertificateKeySize(1024)
            .CreateAsync(ct);

        app.ApplicationConfiguration = config;
        await app.CheckApplicationInstanceCertificatesAsync(false, null, ct);
        return config;
    }

    public async Task DisconnectAsync(DeviceHandle device, CancellationToken ct)
    {
        if (_session is null) return;
        try { await _session.CloseAsync(ct); }
        catch (Exception) { /* best-effort on shutdown */ }
        _session.Dispose();
        _session = null;
    }

    public async Task StartPollingAsync(DeviceHandle device, ITagSink sink, CancellationToken ct)
    {
        _sink = sink;
        if (_session is null) throw new InvalidOperationException("driver not connected");

        _subscription = new Subscription
        {
            PublishingInterval = _cfg.PublishingIntervalMs,
            PublishingEnabled = true,
            KeepAliveCount = 10,
            LifetimeCount = 100,
        };
        _session.AddSubscription(_subscription);
        await _subscription.CreateAsync(ct);

        foreach (var (tagId, info) in _tagsById)
        {
            var item = new MonitoredItem
            {
                StartNodeId = NodeId.Parse(info.NodeId),
                AttributeId = Attributes.Value,
                SamplingInterval = _cfg.SamplingIntervalMs,
                QueueSize = _cfg.QueueSize,
                DiscardOldest = _cfg.DiscardOldest,
                DisplayName = tagId,
                Handle = tagId,
            };
            item.Notification += OnNotification;
            _subscription.AddItem(item);
        }
        await _subscription.ApplyChangesAsync(ct);

        try { await Task.Delay(Timeout.Infinite, ct); }
        catch (OperationCanceledException) { }
    }

    private void OnNotification(MonitoredItem item, MonitoredItemNotificationEventArgs e)
    {
        if (_sink is null || item.Handle is not string tagId || !_tagsById.TryGetValue(tagId, out var info)) return;
        foreach (var dv in item.DequeueValues())
        {
            var quality = MapQuality(dv.StatusCode);
            var value = quality == TagQuality.Bad ? null : TagTypeConversion.Coerce(dv.Value, info.Type);
            _sink.Publish(tagId, value, quality, dv.SourceTimestamp, quality == TagQuality.Bad ? QualitySubCode.ProtocolError : QualitySubCode.None, dv.ServerTimestamp);
        }
    }

    private static TagQuality MapQuality(StatusCode sc) =>
        StatusCode.IsGood(sc) ? TagQuality.Good : StatusCode.IsUncertain(sc) ? TagQuality.Uncertain : TagQuality.Bad;

    private void OnKeepAlive(ISession session, KeepAliveEventArgs e)
    {
        if (!ServiceResult.IsBad(e.Status)) return;

        _errorCount++;
        _lastError = e.Status?.ToString() ?? "unknown";
        _log.Add(_device.ToString(), $"keep-alive failed: {_lastError}", "WARN");
        if (_sink is not null)
            foreach (var tagId in _allTagIds)
                _sink.Publish(tagId, null, TagQuality.Bad, DateTime.UtcNow, QualitySubCode.CommFailure);

        if (!_reconnecting) _ = ReconnectAsync();
    }

    private async Task ReconnectAsync()
    {
        _reconnecting = true;
        try
        {
            while (_session is not null)
            {
                await Task.Delay(_reconnectDelayMs);
                try
                {
                    _session = await Session.RecreateAsync(_session, CancellationToken.None);
                    _session.KeepAliveInterval = _cfg.KeepAliveIntervalMs;
                    _session.KeepAlive += OnKeepAlive;
                    _reconnectDelayMs = _cfg.ReconnectMinMs;
                    _log.Add(_device.ToString(), "reconnected");
                    return;
                }
                catch (Exception ex)
                {
                    _lastError = ex.Message;
                    _reconnectDelayMs = Math.Min(_reconnectDelayMs * 2, _cfg.ReconnectMaxMs);
                }
            }
        }
        finally { _reconnecting = false; }
    }

    public async Task<WriteResult> WriteAsync(TagAddress address, TagValue value, CancellationToken ct)
    {
        if (_session is null) return WriteResult.Failure("not_connected");
        if (!_byNodeId.TryGetValue(address.Native, out var info))
            return WriteResult.Failure("unknown_address");

        var request = new WriteValueCollection
        {
            new WriteValue
            {
                NodeId = NodeId.Parse(address.Native),
                AttributeId = Attributes.Value,
                Value = new DataValue(new Variant(TagTypeConversion.Coerce(value.Value, info.Type))),
            },
        };

        try
        {
            var response = await _session.WriteAsync(null, request, ct);
            var status = response.Results[0];
            return StatusCode.IsGood(status) ? WriteResult.Success() : WriteResult.Failure(status.ToString());
        }
        catch (Exception ex)
        {
            return WriteResult.Failure(ex.Message);
        }
    }

    public DriverHealth GetHealth(DeviceHandle device) =>
        new(_session is not null && !_reconnecting, _errorCount, _lastError, 0);
}
