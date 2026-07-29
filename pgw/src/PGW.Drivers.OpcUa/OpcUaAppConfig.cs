using Opc.Ua;
using Opc.Ua.Configuration;

namespace PGW.Drivers.OpcUa;

/// <summary>
/// Shared client `ApplicationConfiguration` builder, used by both the polling driver and the browser
/// (§5.2) so the file-based certificate trust model (§5.1: certs/own, certs/trusted, certs/rejected)
/// has a single implementation.
/// </summary>
internal static class OpcUaAppConfig
{
    public static async Task<ApplicationConfiguration> BuildAsync(string name, string certsPath, bool autoAccept, CancellationToken ct)
    {
        var app = new ApplicationInstance
        {
            ApplicationName = $"PGW-{name}",
            ApplicationType = ApplicationType.Client,
        };

        var config = await app.Build($"urn:{Environment.MachineName}:pgw:{name}", "uri:pgw:gateway")
            .AsClient()
            .AddSecurityConfiguration($"CN=PGW-{name}, O=PGW", certsPath)
            .SetAutoAcceptUntrustedCertificates(autoAccept)
            .SetRejectSHA1SignedCertificates(false)
            .SetMinimumCertificateKeySize(1024)
            .CreateAsync(ct);

        app.ApplicationConfiguration = config;
        await app.CheckApplicationInstanceCertificatesAsync(false, null, ct);
        return config;
    }
}
