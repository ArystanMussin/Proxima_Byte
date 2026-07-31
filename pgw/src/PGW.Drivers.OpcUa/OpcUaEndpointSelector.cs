using Opc.Ua;
using Opc.Ua.Client;
using PGW.Core;

namespace PGW.Drivers.OpcUa;

/// <summary>
/// Resolves the exact endpoint to connect to (§5.1). `CoreClientUtils.SelectEndpoint(url, useSecurity)`
/// only picks the server's own idea of "best" endpoint for a plain secure/not-secure toggle — it has no
/// way to honor a specific policy like `Basic256Sha256` when a server offers several. When the config
/// asks for a specific mode/policy, this discovers the server's real endpoint list and picks an exact
/// match instead, falling back to the old best-effort behavior only if nothing matches (and says so in
/// the log, rather than silently connecting on the wrong policy).
/// </summary>
public static class OpcUaEndpointSelector
{
    public static async Task<EndpointDescription> SelectAsync(ApplicationConfiguration appConfig, string endpointUrl,
        string securityMode, string? securityPolicy, RingLog log, string logSource, CancellationToken ct)
    {
        var wantMode = ParseMode(securityMode);
        var wantPolicyUri = ResolvePolicyUri(securityPolicy);

        // Fast path: nothing specific requested — skip the extra discovery round-trip entirely, same
        // behavior as before this feature existed.
        if (wantMode == MessageSecurityMode.None && wantPolicyUri is null)
            return await CoreClientUtils.SelectEndpointAsync(appConfig, endpointUrl, false, 15000, ct);

        EndpointDescriptionCollection endpoints;
        try
        {
            var discoveryUrl = CoreClientUtils.GetDiscoveryUrl(endpointUrl);
            using var discoveryClient = await DiscoveryClient.CreateAsync(appConfig, discoveryUrl, DiagnosticsMasks.None, ct);
            endpoints = await discoveryClient.GetEndpointsAsync(null, ct);
        }
        catch (Exception ex)
        {
            log.Add(logSource, $"endpoint discovery failed ({ex.Message}); falling back to best-effort endpoint selection", "WARN");
            return await CoreClientUtils.SelectEndpointAsync(appConfig, endpointUrl, wantMode != MessageSecurityMode.None, 15000, ct);
        }

        var chosen = ChooseBest(endpoints, wantMode, wantPolicyUri);
        if (chosen is not null) return chosen;

        log.Add(logSource, $"server does not offer security mode={wantMode} policy={securityPolicy ?? "(any)"} " +
                            $"— configured endpoints were: {string.Join(", ", endpoints.Select(e => $"{e.SecurityMode}/{ShortPolicyName(e.SecurityPolicyUri)}"))}; " +
                            "falling back to best-effort selection", "WARN");
        return await CoreClientUtils.SelectEndpointAsync(appConfig, endpointUrl, wantMode != MessageSecurityMode.None, 15000, ct);
    }

    /// <summary>Pure selection logic, kept separate from the network call above so it's testable without a live server.</summary>
    public static EndpointDescription? ChooseBest(IEnumerable<EndpointDescription> endpoints, MessageSecurityMode wantMode, string? wantPolicyUri)
    {
        var candidates = endpoints.Where(e => e.SecurityMode == wantMode);
        if (wantPolicyUri is not null) candidates = candidates.Where(e => e.SecurityPolicyUri == wantPolicyUri);
        return candidates.OrderByDescending(e => e.SecurityLevel).FirstOrDefault();
    }

    public static MessageSecurityMode ParseMode(string? mode) => (mode ?? "None").Replace("_", "").ToLowerInvariant() switch
    {
        "sign" => MessageSecurityMode.Sign,
        "signandencrypt" => MessageSecurityMode.SignAndEncrypt,
        _ => MessageSecurityMode.None,
    };

    /// <summary>Accepts either a short name (`Basic256Sha256`) or the full policy URI; null/empty/"None" means "any policy".</summary>
    public static string? ResolvePolicyUri(string? policy)
    {
        if (string.IsNullOrWhiteSpace(policy) || policy.Equals("None", StringComparison.OrdinalIgnoreCase)) return null;
        if (policy.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return policy;
        var uri = SecurityPolicies.GetUri(policy);
        return string.IsNullOrEmpty(uri) ? throw new ArgumentException($"unknown OPC UA security policy '{policy}'") : uri;
    }

    private static string ShortPolicyName(string? policyUri) =>
        string.IsNullOrEmpty(policyUri) ? "None" : policyUri[(policyUri.LastIndexOf('#') + 1)..];
}
