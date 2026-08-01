using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting.Systemd;
using Microsoft.Extensions.Hosting.WindowsServices;
using PGW.Core;
using PGW.Drivers.Modbus;
using PGW.Drivers.OpcUa;
using PGW.Host;
using PGW.Simulator;
using Serilog;

var paths = new PathProvider();
Directory.CreateDirectory(paths.ConfigDir);
Directory.CreateDirectory(paths.DataDir);
Directory.CreateDirectory(paths.LogsDir);

// `pgw simulate` needs no project.yaml at all — it just stands up fake devices for a `sources:` entry
// to point at, so the whole read/write path can be exercised without real hardware (§13-style setup,
// but for connectivity testing rather than an automated test).
if (args.Length > 0 && args[0] == "simulate")
{
    var modbusPort = int.TryParse(Environment.GetEnvironmentVariable("PGW_SIM_MODBUS_PORT"), out var mp) ? mp : 15020;
    // 4841, not the canonical 4840 — 4840 is the OPC UA default and commonly already taken by other
    // OPC UA software (UaExpert, another server, etc.) on the same machine.
    var opcuaPort = int.TryParse(Environment.GetEnvironmentVariable("PGW_SIM_OPCUA_PORT"), out var op) ? op : 4841;

    using var modbusSim = new SimulatedModbusServer();
    modbusSim.Start("127.0.0.1", modbusPort);
    Console.WriteLine($"Simulated Modbus TCP device: 127.0.0.1:{modbusPort}, unit 1 (T1_supply=HR:100, P1_supply=HR:102, setpoint=HR:104 rw, pump1_run=DI:0)");

    using var opcuaSim = new SimulatedOpcUaServer();
    await opcuaSim.StartAsync(opcuaPort, Path.Combine(paths.DataDir, "certs", "_simulator"), CancellationToken.None);
    Console.WriteLine($"Simulated OPC UA server: opc.tcp://127.0.0.1:{opcuaPort}/pgw/simulator (ns=1;s=T1_supply / P1_supply / setpoint rw / pump1_run)");

    Console.WriteLine("Point a source at either (see demo/project.yaml) and press Ctrl+C to stop.");
    var stop = new TaskCompletionSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.TrySetResult(); };
    await stop.Task;
    return 0;
}

var configPath = Environment.GetEnvironmentVariable("PGW_CONFIG") ?? Path.Combine(paths.ConfigDir, "project.yaml");

// Double-click UX (§ README "Один exe, один двойной клик"): a fresh install has no project.yaml
// anywhere yet. Rather than exit with an error the user has to go figure out, bootstrap the bundled
// demo config on first run so the app has something to show immediately — same content as
// `demo/project.yaml`, just copied into the real config location so future runs (and the Config
// editor tab) treat it as a normal, editable config rather than a special read-only demo mode.
var isDemoBootstrap = false;
if (!File.Exists(configPath) && args.Length == 0)
{
    var bundledDemo = Path.Combine(AppContext.BaseDirectory, "demo", "project.yaml");
    if (File.Exists(bundledDemo))
    {
        File.Copy(bundledDemo, configPath);
        isDemoBootstrap = true;
    }
}

if (!File.Exists(configPath))
{
    Console.Error.WriteLine($"config file not found: {configPath}");
    Console.Error.WriteLine("set PGW_CONFIG or create it (see project.sample.yaml)");
    return 1;
}

// CLI: `pgw validate` / `pgw export-map` check the config and exit; anything else runs the service.
if (args.Length > 0 && args[0] is "validate" or "export-map")
{
    var cliEngine = new GatewayEngine(paths, configPath);
    var cliErrors = cliEngine.LoadAndValidate();
    foreach (var e in cliErrors) Console.Error.WriteLine($"error: {e}");
    if (cliErrors.Count > 0) return 1;

    if (args[0] == "export-map")
    {
        foreach (var o in cliEngine.Config.Outputs.Where(o => o.Interface == ModbusOutputFactory.TypeId))
        {
            var wo = ModbusSourceFactory.ParseWordOrder(o.Settings.GetStr("word_order"), WordOrder.ABCD);
            var (map, _) = RegisterMapBuilder.Build(o, wo);
            var path = $"{o.Name}.map.csv";
            File.WriteAllText(path, RegisterMapBuilder.ExportCsv(map));
            Console.WriteLine($"wrote {path}");
        }
    }
    else
    {
        Console.WriteLine("config OK");
    }
    return 0;
}

// §7.1/§7.3: `pgw import-csv <source> <file.csv>` / `pgw export-csv <source> <file.csv>` — bulk
// tag list editing (typical case per the spec: "3000 tags from a spreadsheet"), operating directly on
// the config file rather than requiring the service to be running.
if (args.Length > 0 && args[0] is "import-csv" or "export-csv")
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine($"usage: pgw {args[0]} <source-name> <file.csv>");
        return 1;
    }
    var (sourceName, csvPath) = (args[1], args[2]);
    var cliEngine = new GatewayEngine(paths, configPath);
    var cliErrors = cliEngine.LoadAndValidate();
    foreach (var e in cliErrors) Console.Error.WriteLine($"error: {e}");
    if (cliErrors.Count > 0) return 1;

    var src = cliEngine.Config.Sources.FirstOrDefault(s => s.Name == sourceName);
    if (src is null)
    {
        Console.Error.WriteLine($"error: unknown source '{sourceName}' (add it first — import-csv only adds/updates its tags)");
        return 1;
    }

    if (args[0] == "export-csv")
    {
        File.WriteAllText(csvPath, TagCsv.ExportCsv(src.Tags));
        Console.WriteLine($"wrote {csvPath} ({src.Tags.Count} tags)");
        return 0;
    }

    if (!File.Exists(csvPath))
    {
        Console.Error.WriteLine($"error: file not found: {csvPath}");
        return 1;
    }
    var rows = TagCsv.ParseCsv(File.ReadAllText(csvPath));
    var imported = 0;
    foreach (var row in rows)
    {
        var (applied, rowErrors) = cliEngine.UpsertTag(sourceName, row);
        if (!applied) { Console.Error.WriteLine($"error on row '{row.GetStr("name", "?")}': {string.Join("; ", rowErrors)}"); continue; }
        foreach (var w in rowErrors) Console.Error.WriteLine($"warning: {w}");
        imported++;
    }
    Console.WriteLine($"imported {imported}/{rows.Count} tags into source '{sourceName}'");
    return imported == rows.Count ? 0 : 1;
}

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Is(Enum.TryParse<Serilog.Events.LogEventLevel>(Environment.GetEnvironmentVariable("PGW_LOG_LEVEL") ?? "Information", true, out var lvl) ? lvl : Serilog.Events.LogEventLevel.Information)
    .WriteTo.Console()
    .WriteTo.File(Path.Combine(paths.LogsDir, "pgw-.log"), rollingInterval: RollingInterval.Day)
    .CreateLogger();

// The bootstrapped demo config points its sources at the same simulator `pgw simulate` stands up —
// start it in-process here so double-clicking the exe with nothing configured yet shows live data
// immediately, with no second window/process for the user to know about or keep open.
SimulatedModbusServer? demoModbusSim = null;
SimulatedOpcUaServer? demoOpcUaSim = null;
if (isDemoBootstrap)
{
    var modbusPort = int.TryParse(Environment.GetEnvironmentVariable("PGW_SIM_MODBUS_PORT"), out var mp) ? mp : 15020;
    var opcuaPort = int.TryParse(Environment.GetEnvironmentVariable("PGW_SIM_OPCUA_PORT"), out var op) ? op : 4841;
    demoModbusSim = new SimulatedModbusServer();
    demoModbusSim.Start("127.0.0.1", modbusPort);
    demoOpcUaSim = new SimulatedOpcUaServer();
    await demoOpcUaSim.StartAsync(opcuaPort, Path.Combine(paths.DataDir, "certs", "_simulator"), CancellationToken.None);
    Log.Information("First run: bootstrapped demo config and started the embedded simulator (Modbus 127.0.0.1:{ModbusPort}, OPC UA 127.0.0.1:{OpcUaPort})", modbusPort, opcuaPort);
}

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog();
builder.Host.UseWindowsService();
builder.Host.UseSystemd();
// 8420, not the very common dev-tool default 8080 — 8080 collides constantly with other
// local software (proxies, other web servers, etc.) on Windows machines.
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("PGW_API_URL") ?? "http://127.0.0.1:8420");

var gatewayEngine = new GatewayEngine(paths, configPath);
builder.Services.AddSingleton(gatewayEngine);
builder.Services.AddHostedService<GatewayHostedService>();

var app = builder.Build();

// Static dashboard (wwwroot/) — same Kestrel instance, same REST API, no separate app or build step.
app.UseDefaultFiles();
app.UseStaticFiles();

var apiToken = Environment.GetEnvironmentVariable("PGW_API_TOKEN");
app.Use(async (ctx, next) =>
{
    // /tools/* can read and write arbitrary devices, so it sits behind the same token as /config/*.
    if (ctx.Request.Path.StartsWithSegments("/config") || ctx.Request.Path.StartsWithSegments("/tools")
        || ctx.Request.Path.StartsWithSegments("/runtime/reload"))
    {
        gatewayEngine.ApiLog.Add(ctx.Connection.RemoteIpAddress?.ToString() ?? "?", $"{ctx.Request.Method} {ctx.Request.Path}");
        if (!string.IsNullOrEmpty(apiToken) && ctx.Request.Headers.Authorization != $"Bearer {apiToken}")
        {
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsync("unauthorized");
            return;
        }
    }
    await next();
});

// §14.2 resource schema. /config/* is the static, authenticated, reload-requiring side;
// /runtime/* is live read-only state, safe to leave open inside a trusted network segment.
app.MapGet("/config/channels", () => gatewayEngine.Config.Sources.Select(RedactSource));
app.MapGet("/config/channels/{name}", (string name) =>
{
    var s = gatewayEngine.Config.Sources.FirstOrDefault(x => x.Name == name);
    return s is null ? Results.NotFound() : Results.Ok(RedactSource(s));
});
app.MapPost("/config/channels", (Dictionary<string, object?> body) => ApplyResult(gatewayEngine.UpsertSource(body)));
app.MapPut("/config/channels/{name}", (string name, Dictionary<string, object?> body) =>
{
    body["name"] = name;
    return ApplyResult(gatewayEngine.UpsertSource(body));
});
app.MapDelete("/config/channels/{name}", (string name) => ApplyResult(gatewayEngine.RemoveSource(name)));

app.MapPost("/config/channels/{name}/tags", (string name, Dictionary<string, object?> body) => ApplyResult(gatewayEngine.UpsertTag(name, body)));
app.MapPut("/config/channels/{name}/tags/{tagName}", (string name, string tagName, Dictionary<string, object?> body) =>
{
    body["name"] = tagName;
    return ApplyResult(gatewayEngine.UpsertTag(name, body));
});
app.MapDelete("/config/channels/{name}/tags/{tagName}", (string name, string tagName) => ApplyResult(gatewayEngine.RemoveTag(name, tagName)));

// §7.1: bulk tag list editing via CSV — same underlying UpsertTag as the one-at-a-time endpoints
// above, so a row is exactly as valid/invalid as a single POST would be, just without curl-in-a-loop.
app.MapGet("/config/channels/{name}/tags/csv", (string name) =>
{
    var src = gatewayEngine.Config.Sources.FirstOrDefault(s => s.Name == name);
    return src is null ? Results.NotFound(new { error = $"unknown source '{name}'" }) : Results.Text(TagCsv.ExportCsv(src.Tags), "text/csv");
});
app.MapPost("/config/channels/{name}/tags/import-csv", async (string name, HttpRequest req) =>
{
    if (gatewayEngine.Config.Sources.All(s => s.Name != name)) return Results.NotFound(new { error = $"unknown source '{name}'" });
    using var reader = new StreamReader(req.Body);
    var rows = TagCsv.ParseCsv(await reader.ReadToEndAsync());
    var errors = new List<string>();
    var imported = 0;
    foreach (var row in rows)
    {
        var (applied, rowErrors) = gatewayEngine.UpsertTag(name, row);
        if (!applied) { errors.AddRange(rowErrors.Select(e => $"'{row.GetStr("name", "?")}': {e}")); continue; }
        imported++;
    }
    return Results.Ok(new { ok = errors.Count == 0, imported, total = rows.Count, errors });
});

app.MapGet("/config/outputs", () => gatewayEngine.Config.Outputs);
app.MapPost("/config/outputs", (Dictionary<string, object?> body) => ApplyResult(gatewayEngine.UpsertOutput(body)));
app.MapPut("/config/outputs/{name}", (string name, Dictionary<string, object?> body) =>
{
    body["name"] = name;
    return ApplyResult(gatewayEngine.UpsertOutput(body));
});
app.MapDelete("/config/outputs/{name}", (string name) => ApplyResult(gatewayEngine.RemoveOutput(name)));

app.MapGet("/config/outputs/{name}/map", (string name) =>
{
    var output = gatewayEngine.Config.Outputs.FirstOrDefault(o => o.Name == name);
    if (output is null) return Results.NotFound();
    var wo = ModbusSourceFactory.ParseWordOrder(output.Settings.GetStr("word_order"), WordOrder.ABCD);
    var (map, _) = RegisterMapBuilder.Build(output, wo);
    return Results.Text(RegisterMapBuilder.ExportCsv(map), "text/csv");
});
app.MapPost("/config/outputs/{name}/map", (string name, Dictionary<string, object?> body) => ApplyResult(gatewayEngine.UpsertMapEntry(name, body)));
app.MapPut("/config/outputs/{name}/map/{tag}", (string name, string tag, Dictionary<string, object?> body) =>
{
    body["tag"] = tag;
    return ApplyResult(gatewayEngine.UpsertMapEntry(name, body));
});
app.MapDelete("/config/outputs/{name}/map/{tag}", (string name, string tag) => ApplyResult(gatewayEngine.RemoveMapEntry(name, tag)));

app.MapPost("/config/opcua/browse", async (OpcUaBrowseRequest req) =>
{
    string endpointUrl, certsPath;
    bool useSecurity, autoAccept;

    if (!string.IsNullOrEmpty(req.Source))
    {
        var src = gatewayEngine.Config.Sources.FirstOrDefault(s => s.Name == req.Source && s.Driver == OpcUaSourceFactory.TypeId);
        if (src is null) return Results.NotFound(new { error = $"unknown opcua_client source '{req.Source}'" });
        (endpointUrl, useSecurity, certsPath, autoAccept) = OpcUaSourceFactory.ResolveBrowseParams(src, paths);
    }
    else if (!string.IsNullOrEmpty(req.Endpoint))
    {
        endpointUrl = req.Endpoint;
        useSecurity = req.UseSecurity ?? false;
        certsPath = Path.Combine(paths.DataDir, "certs", "_browse");
        autoAccept = req.Autoaccept ?? false;
    }
    else
    {
        return Results.BadRequest(new { error = "provide either 'source' (an existing opcua_client source name) or 'endpoint'" });
    }

    try
    {
        var nodes = await OpcUaBrowser.BrowseAsync(endpointUrl, useSecurity, req.NodeId, certsPath, autoAccept, CancellationToken.None);
        return Results.Ok(nodes);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// ---- OPC UA server discovery (§5.5): direct GetEndpoints against a known/entered host — the
// mandatory fallback method, no session/cert-trust decision needed since GetEndpoints is unsecured ----
app.MapPost("/tools/opcua/discover", async (OpcUaDiscoverRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.Host)) return Results.BadRequest(new { error = "'host' is required, e.g. opc.tcp://192.168.1.50:4840" });
    var certsPath = Path.Combine(paths.DataDir, "certs", "_discover");
    try
    {
        var endpoints = await OpcUaDiscovery.DiscoverAsync(req.Host, certsPath, req.Autoaccept ?? true, CancellationToken.None);
        return Results.Ok(endpoints);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// ---- Built-in Modbus scanner (§ diagnostics): poll/write any device, independent of the config ----
var scanner = new ModbusScanner();
app.Lifetime.ApplicationStopping.Register(scanner.Dispose);

app.MapPost("/tools/modbus/read", async (ModbusScanRequest req, CancellationToken ct) =>
{
    ScanTarget target;
    try { target = req.ToTarget(); }
    catch (Exception ex) { return Results.BadRequest(new { ok = false, error = ex.Message }); }

    var result = await scanner.ReadAsync(target, ct);
    return Results.Ok(new
    {
        ok = result.Ok,
        error = result.Error,
        latency_ms = Math.Round(result.LatencyMs, 2),
        rows = result.Rows.Select(r => new { address = r.Address, raw = r.Raw, value = r.Value, hex = r.Hex, binary = r.Binary }),
    });
});

app.MapPost("/tools/modbus/write", async (ModbusWriteRequest req, CancellationToken ct) =>
{
    ModbusArea area;
    TagDataType type;
    WordOrder order;
    try
    {
        area = ModbusScanRequest.ParseArea(req.Area);
        type = ModbusScanRequest.ParseType(req.Type);
        order = ModbusSourceFactory.ParseWordOrder(req.WordOrder, WordOrder.ABCD);
    }
    catch (Exception ex) { return Results.BadRequest(new { ok = false, error = ex.Message }); }

    // Value arrives as a boxed JsonElement from minimal-API model binding, which Convert.To*() can't
    // read directly (it doesn't implement IConvertible) — unbox it to a plain CLR type first.
    var value = JsonUnbox.ToPlain(req.Value);
    var result = await scanner.WriteAsync(req.Host, req.Port ?? 502, (byte)(req.UnitId ?? 1), area, (ushort)req.Address,
        type, order, value, req.TimeoutMs ?? 1000, ct);
    return Results.Ok(new { ok = result.Ok, error = result.Error, latency_ms = Math.Round(result.LatencyMs, 2) });
});

app.MapPost("/tools/modbus/close", (ModbusCloseRequest req) =>
{
    scanner.Close(req.Host, req.Port ?? 502);
    return Results.Ok(new { ok = true });
});

// ---- Modbus TCP network scan (§4.5) — named "scan", not "discover": Modbus has no protocol-level
// discovery mechanism, this is methodical enumeration, and honesty about that belongs in the API name
// too, not just the docs. Async job (scan_id) so a /24 doesn't block a request for however long it takes.
var networkScanner = new ModbusNetworkScanner();
app.Lifetime.ApplicationStopping.Register(networkScanner.Dispose);

app.MapPost("/tools/modbus/scan", (ModbusNetworkScanRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.Range)) return Results.BadRequest(new { ok = false, error = "'range' is required (CIDR, e.g. 192.168.1.0/24, or a dash range, e.g. 192.168.1.1-192.168.1.50)" });
    try
    {
        var job = networkScanner.Start(req.Range, req.Port ?? 502, req.TimeoutMs ?? 200, req.MaxConcurrency ?? 32);
        return Results.Ok(new { ok = true, scan_id = job.Id, total = job.Total });
    }
    catch (Exception ex) { return Results.BadRequest(new { ok = false, error = ex.Message }); }
});

app.MapGet("/tools/modbus/scan/{id}", (string id) =>
{
    var job = networkScanner.Get(id);
    if (job is null) return Results.NotFound(new { error = $"unknown scan_id '{id}'" });
    return Results.Ok(ScanJobToApi(job));
});

app.MapPost("/tools/modbus/scan/{id}/cancel", (string id) =>
    networkScanner.Cancel(id) ? Results.Ok(new { ok = true }) : Results.NotFound(new { error = $"unknown scan_id '{id}'" }));

app.MapGet("/tools/modbus/scan/{id}/csv", (string id) =>
{
    var job = networkScanner.Get(id);
    if (job is null) return Results.NotFound(new { error = $"unknown scan_id '{id}'" });
    var csv = new System.Text.StringBuilder("ip,port,responded,unit_ids_found\r\n");
    foreach (var r in job.Results.OrderBy(r => r.Ip, StringComparer.Ordinal))
        csv.Append($"{r.Ip},{r.Port},{r.Responded},\"{string.Join(';', r.UnitIdsFound)}\"\r\n");
    return Results.Text(csv.ToString(), "text/csv");
});

app.MapGet("/tools/modbus/serial-ports", () => Results.Ok(System.IO.Ports.SerialPort.GetPortNames()));

app.MapGet("/runtime/tags", (string? prefix) =>
{
    var all = gatewayEngine.TagSpace.GetAll();
    var filtered = string.IsNullOrEmpty(prefix) ? all : all.Where(t => t.Definition.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    return filtered.Select(ToApiTag);
});
app.MapGet("/runtime/tags/{id}", (string id) =>
{
    var snap = gatewayEngine.TagSpace.Get(id);
    return snap is null ? Results.NotFound() : Results.Ok(ToApiTag(snap));
});
app.MapGet("/runtime/status", () => new
{
    gatewayEngine.Config.Gateway.Name,
    version = gatewayEngine.ConfigVersion,
    uptime_s = (long)(DateTime.UtcNow - gatewayEngine.StartedAtUtc).TotalSeconds,
    started_at = gatewayEngine.StartedAtUtc,
    sources = gatewayEngine.Config.Sources.Count,
    outputs = gatewayEngine.Config.Outputs.Count,
    tags = gatewayEngine.TagSpace.GetAll().Count,
});
app.MapPost("/runtime/reload", async () =>
{
    var errors = await gatewayEngine.ReloadAsync();
    return errors.Count == 0 ? Results.Ok(new { ok = true, version = gatewayEngine.ConfigVersion }) : Results.BadRequest(new { ok = false, errors });
});
app.MapGet("/runtime/event-log", (int? last) => gatewayEngine.EventLog.Snapshot(last));
app.MapGet("/runtime/api-log", (int? last) => gatewayEngine.ApiLog.Snapshot(last));
app.MapGet("/metrics", () => Results.Text(PrometheusExporter.Export(gatewayEngine), "text/plain"));

// Double-click UX: open the dashboard automatically once the server is actually listening, but only
// when someone plausibly just double-clicked the exe or ran it from a terminal — never when hosted
// as a real Windows Service/systemd unit, where there's no desktop session to open a browser on.
var isInteractive = args.Length == 0 && !WindowsServiceHelpers.IsWindowsService() && !SystemdHelpers.IsSystemdService();
var dashboardUrl = Environment.GetEnvironmentVariable("PGW_API_URL") ?? "http://127.0.0.1:8420";
if (isInteractive)
{
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        try { Process.Start(new ProcessStartInfo(dashboardUrl) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warning(ex, "could not auto-open the dashboard in a browser"); }
    });
}

try
{
    app.Run();
}
catch (Exception ex) when (IsAddressInUse(ex))
{
    // Most likely cause: the exe was double-clicked while a previous instance is already running —
    // either Kestrel's own dashboard port or one of the gateway's own listening sockets (e.g. the
    // demo config's Modbus TCP Server output on 502) can be the one that collides; either way it
    // means "an instance is already up." Rather than crash with a stack trace, just bring up the
    // existing instance's dashboard — that's what a second double-click should feel like.
    Log.Information("PGW is already running (port in use) — opening its dashboard instead of starting a second instance");
    if (isInteractive)
    {
        try { Process.Start(new ProcessStartInfo(dashboardUrl) { UseShellExecute = true }); }
        catch (Exception openEx) { Log.Warning(openEx, "could not auto-open the dashboard in a browser"); }
    }
}
finally
{
    demoModbusSim?.Dispose();
    demoOpcUaSim?.Dispose();
}

return 0;

static bool IsAddressInUse(Exception? ex) =>
    ex switch
    {
        null => false,
        SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse } => true,
        _ => IsAddressInUse(ex.InnerException),
    };

static object RedactSource(SourceConfig s) => new
{
    s.Name,
    s.Driver,
    Settings = s.Settings.Where(kv => kv.Key is not ("password" or "password_env")).ToDictionary(kv => kv.Key, kv => kv.Value),
    s.Tags,
};

static IResult ApplyResult((bool Applied, List<string> Errors) r) =>
    r.Applied ? Results.Ok(new { ok = true, warnings = r.Errors }) : Results.BadRequest(new { ok = false, errors = r.Errors });

static object ToApiTag(TagSnapshot t) => new
{
    id = t.Definition.Id,
    value = t.Value,
    quality = t.Quality.ToString(),
    sub_code = t.SubCode.ToString(),
    timestamp = t.SourceTimestampUtc,
    server_timestamp = t.ServerTimestampUtc,
    units = t.Definition.Units,
    access = t.Definition.Access.ToString(),
};

static object ScanJobToApi(ModbusNetworkScanJob job) => new
{
    scan_id = job.Id,
    state = job.State.ToString(),
    total = job.Total,
    scanned = job.Scanned,
    started_at = job.StartedUtc,
    results = job.Results.OrderBy(r => r.Ip, StringComparer.Ordinal)
        .Select(r => new { ip = r.Ip, port = r.Port, responded = r.Responded, unit_ids_found = r.UnitIdsFound }),
};

/// <summary>One poll of the built-in Modbus scanner. Everything but `host` and `address` has a default.</summary>
public sealed record ModbusScanRequest(
    string Host,
    int? Port,
    [property: JsonPropertyName("unit_id")] int? UnitId,
    string? Area,
    int? Address,
    int? Length,
    string? Type,
    [property: JsonPropertyName("word_order")] string? WordOrder,
    [property: JsonPropertyName("timeout_ms")] int? TimeoutMs)
{
    public ScanTarget ToTarget()
    {
        if (string.IsNullOrWhiteSpace(Host)) throw new ArgumentException("host is required");
        var area = ParseArea(Area);
        var type = ParseType(Type);
        var length = Length ?? 1;

        var max = ModbusScanner.IsBitArea(area) ? ModbusScanner.MaxBits : ModbusScanner.MaxRegisters;
        if (length < 1 || length > max)
            throw new ArgumentException($"length must be 1..{max} for area {area}");

        var address = Address ?? 0;
        if (address is < 0 or > 65535) throw new ArgumentException("address must be 0..65535");

        return new ScanTarget(Host, Port ?? 502, (byte)(UnitId ?? 1), area, (ushort)address, length, type,
            ModbusSourceFactory.ParseWordOrder(WordOrder, PGW.Drivers.Modbus.WordOrder.ABCD), TimeoutMs ?? 1000);
    }

    public static ModbusArea ParseArea(string? area) => (area ?? "HR").ToUpperInvariant() switch
    {
        "HR" or "HOLDING" => ModbusArea.HoldingRegister,
        "IR" or "INPUT" => ModbusArea.InputRegister,
        "CO" or "COIL" or "COILS" => ModbusArea.Coil,
        "DI" or "DISCRETE" => ModbusArea.DiscreteInput,
        var s => throw new ArgumentException($"unknown area '{s}' (use HR, IR, CO or DI)"),
    };

    public static TagDataType ParseType(string? type) =>
        Enum.TryParse<TagDataType>(type ?? "uint16", ignoreCase: true, out var t)
            ? t
            : throw new ArgumentException($"unknown type '{type}'");
}

public sealed record ModbusWriteRequest(
    string Host,
    int? Port,
    [property: JsonPropertyName("unit_id")] int? UnitId,
    string? Area,
    int Address,
    string? Type,
    [property: JsonPropertyName("word_order")] string? WordOrder,
    object? Value,
    [property: JsonPropertyName("timeout_ms")] int? TimeoutMs);

public sealed record ModbusCloseRequest(string Host, int? Port);

public sealed record ModbusNetworkScanRequest(
    string Range,
    int? Port,
    [property: JsonPropertyName("timeout_ms")] int? TimeoutMs,
    [property: JsonPropertyName("max_concurrency")] int? MaxConcurrency);

/// <summary>Either browse an already-configured `opcua_client` source, or connect ad-hoc via `endpoint`.</summary>
public sealed record OpcUaBrowseRequest(
    string? Source,
    string? Endpoint,
    [property: JsonPropertyName("node_id")] string? NodeId,
    [property: JsonPropertyName("use_security")] bool? UseSecurity,
    bool? Autoaccept);

public sealed record OpcUaDiscoverRequest(string Host, bool? Autoaccept);
