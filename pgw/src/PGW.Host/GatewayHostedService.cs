namespace PGW.Host;

/// <summary>Bridges the generic host lifecycle to <see cref="GatewayEngine"/>; also the failure point if the config is invalid at startup.</summary>
public sealed class GatewayHostedService(GatewayEngine engine, ILogger<GatewayHostedService> logger, IHostApplicationLifetime lifetime) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        var errors = engine.LoadAndValidate();
        if (errors.Count > 0)
        {
            foreach (var e in errors) logger.LogError("config error: {Error}", e);
            lifetime.StopApplication();
            return;
        }

        await engine.StartAsync(lifetime.ApplicationStopping);
        logger.LogInformation("PGW started: {Sources} sources, {Outputs} outputs", engine.Config.Sources.Count, engine.Config.Outputs.Count);
    }

    public Task StopAsync(CancellationToken ct) => engine.StopAsync();
}
