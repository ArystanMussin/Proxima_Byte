using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting.Systemd;
using Microsoft.Extensions.Hosting.WindowsServices;
using PGW.Core;
using PGW.Drivers.Modbus;
using PGW.Drivers.OpcUa;
using PGW.Host;
using Serilog;

var paths = new PathProvider();
Directory.CreateDirectory(paths.ConfigDir);
Directory.CreateDirectory(paths.DataDir);
Directory.CreateDirectory(paths.LogsDir);

var configPath = Environment.GetEnvironmentVariable("PGW_CONFIG") ?? Path.Combine(paths.ConfigDir, "project.yaml");
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

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Is(Enum.TryParse<Serilog.Events.LogEventLevel>(Environment.GetEnvironmentVariable("PGW_LOG_LEVEL") ?? "Information", true, out var lvl) ? lvl : Serilog.Events.LogEventLevel.Information)
    .WriteTo.Console()
    .WriteTo.File(Path.Combine(paths.LogsDir, "pgw-.log"), rollingInterval: RollingInterval.Day)
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog();
builder.Host.UseWindowsService();
builder.Host.UseSystemd();
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("PGW_API_URL") ?? "http://127.0.0.1:8080");

var gatewayEngine = new GatewayEngine(paths, configPath);
builder.Services.AddSingleton(gatewayEngine);
builder.Services.AddHostedService<GatewayHostedService>();

var app = builder.Build();

var apiToken = Environment.GetEnvironmentVariable("PGW_API_TOKEN");
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/config") || ctx.Request.Path.StartsWithSegments("/runtime/reload"))
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
app.MapGet("/config/outputs", () => gatewayEngine.Config.Outputs);
app.MapGet("/config/outputs/{name}/map", (string name) =>
{
    var output = gatewayEngine.Config.Outputs.FirstOrDefault(o => o.Name == name);
    if (output is null) return Results.NotFound();
    var wo = ModbusSourceFactory.ParseWordOrder(output.Settings.GetStr("word_order"), WordOrder.ABCD);
    var (map, _) = RegisterMapBuilder.Build(output, wo);
    return Results.Text(RegisterMapBuilder.ExportCsv(map), "text/csv");
});

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

app.Run();
return 0;

static object RedactSource(SourceConfig s) => new
{
    s.Name,
    s.Driver,
    Settings = s.Settings.Where(kv => kv.Key is not ("password" or "password_env")).ToDictionary(kv => kv.Key, kv => kv.Value),
    TagCount = s.Tags.Count,
};

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

/// <summary>Either browse an already-configured `opcua_client` source, or connect ad-hoc via `endpoint`.</summary>
public sealed record OpcUaBrowseRequest(
    string? Source,
    string? Endpoint,
    [property: JsonPropertyName("node_id")] string? NodeId,
    [property: JsonPropertyName("use_security")] bool? UseSecurity,
    bool? Autoaccept);
