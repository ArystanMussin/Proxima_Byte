using Opc.Ua;
using Opc.Ua.Client;
using PGW.Core;
using PGW.Drivers.OpcUa;
using PGW.Simulator;
using Xunit;

namespace PGW.Core.Tests;

/// <summary>
/// Precise OPC UA security-policy selection (§5.1): previously any non-default `security.policy` was
/// silently ignored — `CoreClientUtils.SelectEndpoint` only ever picked "the server's best guess" for a
/// plain secure/not-secure toggle. <see cref="OpcUaEndpointSelector"/> discovers the server's actual
/// endpoint list and matches the exact mode/policy the config asked for.
/// </summary>
public class OpcUaEndpointSelectorTests
{
    private static EndpointDescription Endpoint(MessageSecurityMode mode, string policyUri, byte level) =>
        new() { SecurityMode = mode, SecurityPolicyUri = policyUri, SecurityLevel = level };

    [Fact]
    public void ChooseBest_Picks_Exact_Mode_And_Policy_Match()
    {
        var endpoints = new[]
        {
            Endpoint(MessageSecurityMode.None, SecurityPolicies.None, 0),
            Endpoint(MessageSecurityMode.Sign, SecurityPolicies.Basic256Sha256, 1),
            Endpoint(MessageSecurityMode.SignAndEncrypt, SecurityPolicies.Basic256Sha256, 2),
            Endpoint(MessageSecurityMode.SignAndEncrypt, SecurityPolicies.Aes256_Sha256_RsaPss, 3),
        };

        var chosen = OpcUaEndpointSelector.ChooseBest(endpoints, MessageSecurityMode.SignAndEncrypt, SecurityPolicies.Basic256Sha256);

        Assert.NotNull(chosen);
        Assert.Equal(MessageSecurityMode.SignAndEncrypt, chosen!.SecurityMode);
        Assert.Equal(SecurityPolicies.Basic256Sha256, chosen.SecurityPolicyUri);
    }

    [Fact]
    public void ChooseBest_Prefers_Higher_SecurityLevel_When_Policy_Unspecified()
    {
        var endpoints = new[]
        {
            Endpoint(MessageSecurityMode.Sign, SecurityPolicies.Basic256Sha256, 5),
            Endpoint(MessageSecurityMode.Sign, SecurityPolicies.Aes256_Sha256_RsaPss, 9),
        };

        var chosen = OpcUaEndpointSelector.ChooseBest(endpoints, MessageSecurityMode.Sign, wantPolicyUri: null);

        Assert.NotNull(chosen);
        Assert.Equal(SecurityPolicies.Aes256_Sha256_RsaPss, chosen!.SecurityPolicyUri);
    }

    [Fact]
    public void ChooseBest_Returns_Null_When_Server_Does_Not_Offer_The_Requested_Combination()
    {
        var endpoints = new[] { Endpoint(MessageSecurityMode.None, SecurityPolicies.None, 0) };

        var chosen = OpcUaEndpointSelector.ChooseBest(endpoints, MessageSecurityMode.SignAndEncrypt, SecurityPolicies.Basic256Sha256);

        Assert.Null(chosen);
    }

    [Theory]
    [InlineData("None", MessageSecurityMode.None)]
    [InlineData(null, MessageSecurityMode.None)]
    [InlineData("Sign", MessageSecurityMode.Sign)]
    [InlineData("SignAndEncrypt", MessageSecurityMode.SignAndEncrypt)]
    [InlineData("sign_and_encrypt", MessageSecurityMode.SignAndEncrypt)]
    public void ParseMode_Accepts_Config_Style_Strings(string? input, MessageSecurityMode expected) =>
        Assert.Equal(expected, OpcUaEndpointSelector.ParseMode(input));

    [Fact]
    public void ResolvePolicyUri_Maps_Short_Name_To_Full_Uri() =>
        Assert.Equal(SecurityPolicies.Basic256Sha256, OpcUaEndpointSelector.ResolvePolicyUri("Basic256Sha256"));

    [Fact]
    public void ResolvePolicyUri_Passes_Through_A_Full_Uri_Unchanged() =>
        Assert.Equal(SecurityPolicies.Basic256Sha256, OpcUaEndpointSelector.ResolvePolicyUri(SecurityPolicies.Basic256Sha256));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("None")]
    public void ResolvePolicyUri_Treats_Empty_Or_None_As_Any(string? input) =>
        Assert.Null(OpcUaEndpointSelector.ResolvePolicyUri(input));

    [Fact]
    public void ResolvePolicyUri_Rejects_Unknown_Policy_Name() =>
        Assert.Throws<ArgumentException>(() => OpcUaEndpointSelector.ResolvePolicyUri("NotARealPolicy"));

    [Fact]
    public async Task Driver_Connects_Through_Discovery_Path_And_Falls_Back_When_Server_Lacks_The_Requested_Policy()
    {
        // The simulator only exposes None/None (AddUnsecurePolicyNone). Asking for a policy it doesn't
        // offer must not break connectivity — the selector should discover, fail to find a match, log
        // why, and fall back to the best-effort endpoint instead of erroring out. Driven through the real
        // OpcUaClientDriver (not the internal AppConfig builder directly) so this exercises exactly the
        // path a real `sources:` entry with `security.policy: Basic256Sha256` would take.
        var port = TestUtil.GetFreePort();
        var certsPath = Path.Combine(Path.GetTempPath(), "pgw-test-certs-" + Guid.NewGuid());

        using var server = new SimulatedOpcUaServer();
        await server.StartAsync(port, Path.Combine(certsPath, "server"), default);

        var log = new RingLog();
        var cfg = new OpcUaDeviceSettings(
            EndpointUrl: $"opc.tcp://127.0.0.1:{port}/pgw/simulator",
            SecurityPolicy: "Basic256Sha256",
            SecurityMode: "None",
            AutoAcceptUntrustedCertificates: true,
            CertsPath: Path.Combine(certsPath, "client"));
        var driver = new OpcUaClientDriver(cfg, new Dictionary<string, OpcUaTagInfo>(), log);
        var device = new DeviceHandle("selector-test", "selector-test");

        var result = await driver.ConnectAsync(device, default);

        Assert.True(result.Ok, result.Error);
        Assert.Contains(log.Snapshot(null), e => e.Level == "WARN" && e.Message.Contains("does not offer"));

        await driver.DisconnectAsync(device, default);
    }
}
