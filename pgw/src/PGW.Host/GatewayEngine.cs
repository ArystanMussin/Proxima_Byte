using PGW.Core;
using PGW.Drivers.Modbus;
using PGW.Drivers.OpcUa;

namespace PGW.Host;

/// <summary>
/// Composition root (§3.5): the only place that knows about concrete driver/interface types. Adding a
/// third protocol means adding one more case to the two switches below — the core and the interfaces
/// stay untouched.
/// </summary>
public sealed class GatewayEngine
{
    public GatewayProjectConfig Config { get; private set; } = new();
    public TagSpace TagSpace { get; } = new();
    public RingLog EventLog { get; } = new();
    public RingLog ApiLog { get; } = new();
    public DateTime StartedAtUtc { get; } = DateTime.UtcNow;
    public int ConfigVersion { get; private set; }

    private readonly IPathProvider _paths;
    private readonly string _configPath;
    private readonly List<(IProtocolDriver Driver, DeviceHandle Device)> _drivers = new();
    private readonly List<(OutputConfig Output, IProtocolInterface Interface)> _interfaces = new();
    private CancellationTokenSource _runCts = new();
    private CancellationToken _hostCt;

    public GatewayEngine(IPathProvider paths, string configPath)
    {
        _paths = paths;
        _configPath = configPath;
    }

    public List<string> LoadAndValidate()
    {
        var cfg = ConfigLoader.Load(_configPath);
        var errors = ConfigValidator.Validate(cfg);

        foreach (var o in cfg.Outputs.Where(o => o.Interface == ModbusOutputFactory.TypeId))
        {
            var wo = ModbusSourceFactory.ParseWordOrder(o.Settings.GetStr("word_order"), WordOrder.ABCD);
            errors.AddRange(RegisterMapBuilder.Build(o, wo).Errors);
        }

        if (errors.Count == 0) Config = cfg;
        return errors;
    }

    public async Task StartAsync(CancellationToken hostCt)
    {
        _hostCt = hostCt;
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(hostCt);
        var ct = _runCts.Token;

        RegisterSystemTags();

        foreach (var src in Config.Sources)
        {
            var (driver, device, tags) = src.Driver switch
            {
                ModbusSourceFactory.TypeId => ModbusSourceFactory.Build(src, EventLog),
                OpcUaSourceFactory.TypeId => OpcUaSourceFactory.Build(src, EventLog, _paths),
                _ => throw new NotSupportedException($"unknown driver '{src.Driver}'"),
            };
            foreach (var (def, addr) in tags) TagSpace.Register(def, driver, addr);
            _drivers.Add((driver, device));
        }

        if (Config.Gateway.PersistLastValues)
            TagPersistence.Restore(TagSpace, LastValuesPath);

        foreach (var (driver, device) in _drivers)
        {
            var result = await driver.ConnectAsync(device, ct);
            EventLog.Add(device.ToString(), result.Ok ? "connected" : $"connect failed: {result.Error}", result.Ok ? "INFO" : "ERROR");
            _ = driver.StartPollingAsync(device, TagSpace, ct).ContinueWith(t =>
            {
                if (t.IsFaulted && !ct.IsCancellationRequested)
                    EventLog.Add(device.ToString(), $"polling stopped: {t.Exception?.GetBaseException().Message}", "ERROR");
            }, TaskScheduler.Default);
        }

        foreach (var o in Config.Outputs)
        {
            switch (o.Interface)
            {
                case ModbusOutputFactory.TypeId:
                    var (iface, _, errors) = ModbusOutputFactory.Build(o, CoreWriteAsync, EventLog);
                    foreach (var e in errors) EventLog.Add(o.Name, e, "ERROR");
                    await iface.StartAsync(TagSpace, ct);
                    _interfaces.Add((o, iface));
                    break;
                default:
                    throw new NotSupportedException($"unknown interface '{o.Interface}'");
            }
        }

        ConfigVersion++;
        _ = RunSystemTagsLoopAsync(ct);
        if (Config.Gateway.PersistLastValues)
            _ = RunPersistLoopAsync(ct);
    }

    public async Task StopAsync()
    {
        await _runCts.CancelAsync();
        foreach (var (_, iface) in _interfaces) await iface.StopAsync(default);
        foreach (var (driver, device) in _drivers) await driver.DisconnectAsync(device, default);
        _interfaces.Clear();
        _drivers.Clear();

        if (Config.Gateway.PersistLastValues)
            TagPersistence.Save(TagSpace, LastValuesPath);
    }

    /// <summary>
    /// §7.3/§14.3: reload without disrupting configuration correctness is guaranteed; reload without
    /// disrupting already-open Modbus TCP client sockets is a v0.1 simplification — see README.
    /// </summary>
    public async Task<List<string>> ReloadAsync()
    {
        var errors = LoadAndValidate();
        if (errors.Count > 0) return errors;
        await StopAsync();
        await StartAsync(_hostCt);
        return errors;
    }

    private Task<WriteResult> CoreWriteAsync(string tagId, object? value, CancellationToken ct) =>
        TagSpace.WriteAsync(tagId, value, TimeSpan.FromSeconds(5), ct);

    private string LastValuesPath => Path.Combine(_paths.DataDir, "last_values.json");

    private void RegisterSystemTags()
    {
        var owner = new SystemDriver();
        var dev = new DeviceHandle("_System", "_System");
        void Reg(string id, TagDataType type) =>
            TagSpace.Register(new TagDefinition(id, type, TagAccess.RO, dev, id), owner, new TagAddress(dev, id));

        Reg("_System.uptime_s", TagDataType.Int64);
        Reg("_System.heartbeat", TagDataType.Int64);
        Reg("_System.config_version", TagDataType.Int32);

        foreach (var s in Config.Sources)
        {
            Reg($"_System.{s.Name}.connected", TagDataType.Bool);
            Reg($"_System.{s.Name}.error_count", TagDataType.Int64);
            Reg($"_System.{s.Name}.last_error_code", TagDataType.String);
        }
        foreach (var o in Config.Outputs)
        {
            Reg($"_System.{o.Name}.client_count", TagDataType.Int32);
            Reg($"_System.{o.Name}.requests_total", TagDataType.Int64);
            Reg($"_System.{o.Name}.exceptions_total", TagDataType.Int64);
        }
    }

    private async Task RunSystemTagsLoopAsync(CancellationToken ct)
    {
        long heartbeat = 0;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            do
            {
                var now = DateTime.UtcNow;
                TagSpace.Publish("_System.uptime_s", (long)(now - StartedAtUtc).TotalSeconds, TagQuality.Good, now);
                TagSpace.Publish("_System.heartbeat", heartbeat++, TagQuality.Good, now);
                TagSpace.Publish("_System.config_version", ConfigVersion, TagQuality.Good, now);

                foreach (var (driver, device) in _drivers)
                {
                    var h = driver.GetHealth(device);
                    TagSpace.Publish($"_System.{device.Channel}.connected", h.Connected, TagQuality.Good, now);
                    TagSpace.Publish($"_System.{device.Channel}.error_count", h.ErrorCount, TagQuality.Good, now);
                    TagSpace.Publish($"_System.{device.Channel}.last_error_code", h.LastError ?? "", TagQuality.Good, now);
                }
                foreach (var (output, iface) in _interfaces)
                {
                    var h = iface.GetHealth();
                    TagSpace.Publish($"_System.{output.Name}.client_count", h.ClientCount, TagQuality.Good, now);
                    TagSpace.Publish($"_System.{output.Name}.requests_total", h.RequestsTotal, TagQuality.Good, now);
                    TagSpace.Publish($"_System.{output.Name}.exceptions_total", h.ExceptionsTotal, TagQuality.Good, now);
                }
            } while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) { }
    }

    private async Task RunPersistLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
                TagPersistence.Save(TagSpace, LastValuesPath);
        }
        catch (OperationCanceledException) { }
    }
}

/// <summary>Placeholder owner for synthetic `_System.*` tags (§8) — never connected, write always rejected.</summary>
internal sealed class SystemDriver : IProtocolDriver
{
    public string DriverTypeId => "_system";
    public DriverCapabilities Capabilities { get; } = new(false, false, false);
    public Task<DriverConnectResult> ConnectAsync(DeviceHandle device, CancellationToken ct) => Task.FromResult(DriverConnectResult.Success);
    public Task DisconnectAsync(DeviceHandle device, CancellationToken ct) => Task.CompletedTask;
    public Task StartPollingAsync(DeviceHandle device, ITagSink sink, CancellationToken ct) => Task.CompletedTask;
    public Task<WriteResult> WriteAsync(TagAddress address, TagValue value, CancellationToken ct) => Task.FromResult(WriteResult.NotSupported);
    public DriverHealth GetHealth(DeviceHandle device) => new(true, 0, null, 0);
}
