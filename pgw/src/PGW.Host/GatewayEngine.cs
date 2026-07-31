using PGW.Core;
using PGW.Drivers.Mercury;
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

    private static readonly string[] KnownDrivers = { ModbusSourceFactory.TypeId, ModbusRtuSourceFactory.TypeId, OpcUaSourceFactory.TypeId, MercurySourceFactory.TypeId };
    private static readonly string[] KnownInterfaces = { ModbusOutputFactory.TypeId };

    private sealed record DriverEntry(SourceConfig Config, IProtocolDriver Driver, DeviceHandle Device, List<string> TagIds);

    private readonly IPathProvider _paths;
    private readonly string _configPath;
    private readonly Dictionary<string, DriverEntry> _driverEntries = new();
    private readonly Dictionary<string, (OutputConfig Config, IProtocolInterface Interface)> _interfaceEntries = new();
    private readonly object _configLock = new();
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
        var errors = ValidateFull(cfg);
        if (errors.Count == 0) Config = cfg;
        return errors;
    }

    private static List<string> ValidateFull(GatewayProjectConfig cfg)
    {
        var errors = ConfigValidator.Validate(cfg);
        foreach (var o in cfg.Outputs.Where(o => o.Interface == ModbusOutputFactory.TypeId))
        {
            var wo = ModbusSourceFactory.ParseWordOrder(o.Settings.GetStr("word_order"), WordOrder.ABCD);
            errors.AddRange(RegisterMapBuilder.Build(o, wo).Errors);
        }
        return errors;
    }

    public async Task StartAsync(CancellationToken hostCt)
    {
        _hostCt = hostCt;
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(hostCt);
        var ct = _runCts.Token;

        RegisterSystemTags();
        await ReconcileAsync(previous: null, ct);

        ConfigVersion++;
        _ = RunSystemTagsLoopAsync(ct);
        if (Config.Gateway.PersistLastValues)
            _ = RunPersistLoopAsync(ct);
    }

    public async Task StopAsync()
    {
        await _runCts.CancelAsync();
        foreach (var (_, iface) in _interfaceEntries.Values) await iface.StopAsync(default);
        foreach (var entry in _driverEntries.Values) await entry.Driver.DisconnectAsync(entry.Device, default);
        _interfaceEntries.Clear();
        _driverEntries.Clear();

        if (Config.Gateway.PersistLastValues)
            TagPersistence.Save(TagSpace, LastValuesPath);
    }

    /// <summary>
    /// §7.3/§14.3: reconciles against the running state instead of a full stop/start — a source or
    /// output whose config content is byte-for-byte unchanged keeps its live driver connection /
    /// already-open Modbus TCP Server socket untouched, so editing one tag doesn't drop every SCADA
    /// client currently polling an unrelated output. Only entities that actually changed (or were
    /// added/removed) get stopped and rebuilt.
    /// </summary>
    public async Task<List<string>> ReloadAsync()
    {
        var previous = Config;
        var errors = LoadAndValidate();
        if (errors.Count > 0) return errors;

        await ReconcileAsync(previous, _hostCt);
        ConfigVersion++;
        return errors;
    }

    private async Task ReconcileAsync(GatewayProjectConfig? previous, CancellationToken ct)
    {
        var newSources = Config.Sources.ToDictionary(s => s.Name);
        var newOutputs = Config.Outputs.ToDictionary(o => o.Name);

        // Stop/unregister sources that vanished or changed — before building anything new, so a
        // renamed-then-reused address or a changed port on the same name can't collide with what's
        // still running.
        foreach (var (name, entry) in _driverEntries.ToList())
        {
            if (newSources.TryGetValue(name, out var stillWanted) && ConfigDiff.ContentEquals(entry.Config, stillWanted))
                continue; // unchanged — leave the live connection alone

            await entry.Driver.DisconnectAsync(entry.Device, CancellationToken.None);
            foreach (var tagId in entry.TagIds) TagSpace.Unregister(tagId);
            _driverEntries.Remove(name);
            EventLog.Add(entry.Device.ToString(), "reload: stopped (source removed or changed)", "INFO");
        }

        // Build + register tags for sources that are new or changed; don't connect yet — persisted
        // last-values (cold start only) must land before the first live poll can overwrite them.
        var toConnect = new List<DriverEntry>();
        foreach (var src in Config.Sources)
        {
            if (_driverEntries.ContainsKey(src.Name)) continue;

            var (driver, device, tags) = src.Driver switch
            {
                ModbusSourceFactory.TypeId => ModbusSourceFactory.Build(src, EventLog),
                ModbusRtuSourceFactory.TypeId => ModbusRtuSourceFactory.Build(src, EventLog),
                MercurySourceFactory.TypeId => MercurySourceFactory.Build(src, EventLog),
                OpcUaSourceFactory.TypeId => OpcUaSourceFactory.Build(src, EventLog, _paths),
                _ => throw new NotSupportedException($"unknown driver '{src.Driver}'"),
            };
            var tagIds = new List<string>(tags.Count);
            foreach (var (def, addr) in tags)
            {
                TagSpace.Register(def, driver, addr);
                tagIds.Add(def.Id);
            }
            var entry = new DriverEntry(src, driver, device, tagIds);
            _driverEntries[src.Name] = entry;
            toConnect.Add(entry);
            RegisterSourceSystemTags(src);
        }

        if (previous is null && Config.Gateway.PersistLastValues)
            TagPersistence.Restore(TagSpace, LastValuesPath);

        foreach (var entry in toConnect)
        {
            var result = await entry.Driver.ConnectAsync(entry.Device, ct);
            EventLog.Add(entry.Device.ToString(), result.Ok ? "connected" : $"connect failed: {result.Error}", result.Ok ? "INFO" : "ERROR");
            var device = entry.Device;
            _ = entry.Driver.StartPollingAsync(device, TagSpace, ct).ContinueWith(t =>
            {
                if (t.IsFaulted && !ct.IsCancellationRequested)
                    EventLog.Add(device.ToString(), $"polling stopped: {t.Exception?.GetBaseException().Message}", "ERROR");
            }, TaskScheduler.Default);
        }

        // Same two-phase pattern for outputs: stop/rebuild only what changed, leaving unaffected Modbus
        // TCP Server sockets (and every client currently connected to them) running the whole time.
        foreach (var (name, entry) in _interfaceEntries.ToList())
        {
            if (newOutputs.TryGetValue(name, out var stillWanted) && ConfigDiff.ContentEquals(entry.Config, stillWanted))
                continue;

            await entry.Interface.StopAsync(CancellationToken.None);
            _interfaceEntries.Remove(name);
            EventLog.Add(name, "reload: stopped (output removed or changed)", "INFO");
        }

        foreach (var o in Config.Outputs)
        {
            if (_interfaceEntries.ContainsKey(o.Name)) continue;

            switch (o.Interface)
            {
                case ModbusOutputFactory.TypeId:
                    var (iface, _, errors) = ModbusOutputFactory.Build(o, CoreWriteAsync, EventLog);
                    foreach (var e in errors) EventLog.Add(o.Name, e, "ERROR");
                    await iface.StartAsync(TagSpace, ct);
                    _interfaceEntries[o.Name] = (o, iface);
                    RegisterOutputSystemTags(o);
                    break;
                default:
                    throw new NotSupportedException($"unknown interface '{o.Interface}'");
            }
        }
    }

    // ---- Config editing (§14.2 write side) ----------------------------------------------------
    // Each call mutates Config in place and persists to disk immediately; it does NOT restart drivers
    // or interfaces on its own — the caller (dashboard) batches edits and calls ReloadAsync explicitly,
    // so ten tag edits don't mean ten reconnects.

    public (bool Applied, List<string> Errors) UpsertSource(Dictionary<string, object?> raw)
    {
        lock (_configLock)
        {
            var src = ConfigLoader.ParseSource(JsonUnbox.ToPlainDict(raw));
            if (string.IsNullOrWhiteSpace(src.Name)) return (false, new() { "source requires 'name'" });
            if (!KnownDrivers.Contains(src.Driver))
                return (false, new() { $"unknown driver '{src.Driver}' (expected: {string.Join(", ", KnownDrivers)})" });

            var idx = Config.Sources.FindIndex(s => s.Name == src.Name);
            if (idx >= 0) Config.Sources[idx] = src; else Config.Sources.Add(src);
            return Persist();
        }
    }

    public (bool Applied, List<string> Errors) RemoveSource(string name)
    {
        lock (_configLock)
        {
            if (Config.Sources.RemoveAll(s => s.Name == name) == 0) return (false, new() { $"unknown source '{name}'" });
            return Persist();
        }
    }

    public (bool Applied, List<string> Errors) UpsertTag(string sourceName, Dictionary<string, object?> raw)
    {
        lock (_configLock)
        {
            var src = Config.Sources.FirstOrDefault(s => s.Name == sourceName);
            if (src is null) return (false, new() { $"unknown source '{sourceName}'" });

            var tag = JsonUnbox.ToPlainDict(raw);
            var tagName = tag.GetStr("name");
            if (string.IsNullOrWhiteSpace(tagName)) return (false, new() { "tag requires 'name'" });

            var idx = src.Tags.FindIndex(t => t.GetStr("name") == tagName);
            if (idx >= 0) src.Tags[idx] = tag; else src.Tags.Add(tag);
            return Persist();
        }
    }

    public (bool Applied, List<string> Errors) RemoveTag(string sourceName, string tagName)
    {
        lock (_configLock)
        {
            var src = Config.Sources.FirstOrDefault(s => s.Name == sourceName);
            if (src is null) return (false, new() { $"unknown source '{sourceName}'" });
            if (src.Tags.RemoveAll(t => t.GetStr("name") == tagName) == 0) return (false, new() { $"unknown tag '{tagName}'" });
            return Persist();
        }
    }

    public (bool Applied, List<string> Errors) UpsertOutput(Dictionary<string, object?> raw)
    {
        lock (_configLock)
        {
            var output = ConfigLoader.ParseOutput(JsonUnbox.ToPlainDict(raw));
            if (string.IsNullOrWhiteSpace(output.Name)) return (false, new() { "output requires 'name'" });
            if (!KnownInterfaces.Contains(output.Interface))
                return (false, new() { $"unknown interface '{output.Interface}' (expected: {string.Join(", ", KnownInterfaces)})" });

            var idx = Config.Outputs.FindIndex(o => o.Name == output.Name);
            if (idx >= 0) Config.Outputs[idx] = output; else Config.Outputs.Add(output);
            return Persist();
        }
    }

    public (bool Applied, List<string> Errors) RemoveOutput(string name)
    {
        lock (_configLock)
        {
            if (Config.Outputs.RemoveAll(o => o.Name == name) == 0) return (false, new() { $"unknown output '{name}'" });
            return Persist();
        }
    }

    public (bool Applied, List<string> Errors) UpsertMapEntry(string outputName, Dictionary<string, object?> raw)
    {
        lock (_configLock)
        {
            var output = Config.Outputs.FirstOrDefault(o => o.Name == outputName);
            if (output is null) return (false, new() { $"unknown output '{outputName}'" });

            var entry = JsonUnbox.ToPlainDict(raw);
            var tag = entry.GetStr("tag");
            if (string.IsNullOrWhiteSpace(tag)) return (false, new() { "map entry requires 'tag'" });

            var idx = output.Map.FindIndex(m => m.GetStr("tag") == tag);
            if (idx >= 0) output.Map[idx] = entry; else output.Map.Add(entry);
            return Persist();
        }
    }

    public (bool Applied, List<string> Errors) RemoveMapEntry(string outputName, string tag)
    {
        lock (_configLock)
        {
            var output = Config.Outputs.FirstOrDefault(o => o.Name == outputName);
            if (output is null) return (false, new() { $"unknown output '{outputName}'" });
            if (output.Map.RemoveAll(m => m.GetStr("tag") == tag) == 0) return (false, new() { $"unknown map entry '{tag}'" });
            return Persist();
        }
    }

    /// <summary>Writes the in-memory config to disk and runs full validation; caller decides whether/when to reload.</summary>
    private (bool Applied, List<string> Errors) Persist()
    {
        ConfigWriter.Save(Config, _configPath);
        return (true, ValidateFull(Config));
    }

    private Task<WriteResult> CoreWriteAsync(string tagId, object? value, CancellationToken ct) =>
        TagSpace.WriteAsync(tagId, value, TimeSpan.FromSeconds(5), ct);

    private string LastValuesPath => Path.Combine(_paths.DataDir, "last_values.json");

    private static readonly SystemDriver SystemTagOwner = new();
    private static readonly DeviceHandle SystemDevice = new("_System", "_System");

    private static void RegSystemTag(TagSpace tagSpace, string id, TagDataType type) =>
        tagSpace.Register(new TagDefinition(id, type, TagAccess.RO, SystemDevice, id), SystemTagOwner, new TagAddress(SystemDevice, id));

    private void RegisterSystemTags()
    {
        RegSystemTag(TagSpace, "_System.uptime_s", TagDataType.Int64);
        RegSystemTag(TagSpace, "_System.heartbeat", TagDataType.Int64);
        RegSystemTag(TagSpace, "_System.config_version", TagDataType.Int32);
    }

    /// <summary>Called once per source, both at cold start and whenever reload adds/recreates one (§7.3)
    /// — without this, a source added via a later reload would never get its health tags registered.</summary>
    private void RegisterSourceSystemTags(SourceConfig s)
    {
        RegSystemTag(TagSpace, $"_System.{s.Name}.connected", TagDataType.Bool);
        RegSystemTag(TagSpace, $"_System.{s.Name}.error_count", TagDataType.Int64);
        RegSystemTag(TagSpace, $"_System.{s.Name}.last_error_code", TagDataType.String);
    }

    private void RegisterOutputSystemTags(OutputConfig o)
    {
        RegSystemTag(TagSpace, $"_System.{o.Name}.client_count", TagDataType.Int32);
        RegSystemTag(TagSpace, $"_System.{o.Name}.requests_total", TagDataType.Int64);
        RegSystemTag(TagSpace, $"_System.{o.Name}.exceptions_total", TagDataType.Int64);
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

                foreach (var entry in _driverEntries.Values)
                {
                    var h = entry.Driver.GetHealth(entry.Device);
                    TagSpace.Publish($"_System.{entry.Device.Channel}.connected", h.Connected, TagQuality.Good, now);
                    TagSpace.Publish($"_System.{entry.Device.Channel}.error_count", h.ErrorCount, TagQuality.Good, now);
                    TagSpace.Publish($"_System.{entry.Device.Channel}.last_error_code", h.LastError ?? "", TagQuality.Good, now);
                }
                foreach (var (output, iface) in _interfaceEntries.Values)
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
