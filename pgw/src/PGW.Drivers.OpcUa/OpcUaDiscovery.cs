using Opc.Ua;
using Opc.Ua.Client;

namespace PGW.Drivers.OpcUa;

public sealed record OpcUaDiscoveredEndpoint(string EndpointUrl, string? ServerName, IReadOnlyList<string> SecurityPolicies, IReadOnlyList<string> SecurityModes);

/// <summary>
/// §5.5 OPC UA server discovery: the mandatory direct-GetEndpoints fallback against a known/entered
/// host. `GetEndpoints` is an unsecured service — no session, no certificate trust decision needed —
/// which is exactly why the spec calls it the fallback that must always work, unlike mDNS/LDS-ME
/// (most embedded servers, the typical "one PLC" case, implement GetEndpoints as their own local
/// discovery rather than running a separate LDS, so a direct call here covers the common case anyway).
///
/// Deliberately NOT implemented: mDNS/LDS-ME multicast discovery (needs an extra multicast-DNS
/// dependency and only surfaces servers that opted into that extension — not guaranteed, especially on
/// budget PLCs) and an IP-range GetEndpoints scan analogous to the Modbus subnet scan (the optional
/// fallback-of-a-fallback per §5.5). Both are real gaps, documented rather than silently missing — see
/// README "OPC UA discovery".
/// </summary>
public static class OpcUaDiscovery
{
    public static async Task<List<OpcUaDiscoveredEndpoint>> DiscoverAsync(string hostUrl, string certsPath, bool autoAccept, CancellationToken ct)
    {
        var appConfig = await OpcUaAppConfig.BuildAsync("discover", certsPath, autoAccept, ct);
        var discoveryUrl = CoreClientUtils.GetDiscoveryUrl(hostUrl);
        using var client = await DiscoveryClient.CreateAsync(appConfig, discoveryUrl, DiagnosticsMasks.None, ct);
        var endpoints = await client.GetEndpointsAsync(null, ct);

        return endpoints
            .GroupBy(e => e.EndpointUrl)
            .Select(g => new OpcUaDiscoveredEndpoint(
                g.Key,
                g.First().Server?.ApplicationName?.Text,
                g.Select(e => ShortPolicyName(e.SecurityPolicyUri)).Distinct().ToList(),
                g.Select(e => e.SecurityMode.ToString()).Distinct().ToList()))
            .ToList();
    }

    private static string ShortPolicyName(string? policyUri) =>
        string.IsNullOrEmpty(policyUri) ? "None" : policyUri[(policyUri.LastIndexOf('#') + 1)..];
}
