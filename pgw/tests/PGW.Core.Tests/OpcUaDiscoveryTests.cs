using PGW.Drivers.OpcUa;
using PGW.Simulator;
using Xunit;

namespace PGW.Core.Tests;

/// <summary>§5.5 / §13: direct GetEndpoints discovery against a real, running OPC UA server (the
/// embedded simulator) — no mock, a real unsecured discovery round trip.</summary>
public class OpcUaDiscoveryTests
{
    [Fact]
    public async Task DiscoverAsync_Finds_A_Real_Servers_Endpoint_And_Security_Options()
    {
        var port = TestUtil.GetFreePort();
        var certsPath = Path.Combine(Path.GetTempPath(), "pgw-discovery-test-" + Guid.NewGuid());

        using var server = new SimulatedOpcUaServer();
        await server.StartAsync(port, Path.Combine(certsPath, "server"), default);

        var endpoints = await OpcUaDiscovery.DiscoverAsync(
            $"opc.tcp://127.0.0.1:{port}/pgw/simulator", Path.Combine(certsPath, "client"), autoAccept: true, default);

        var found = Assert.Single(endpoints);
        Assert.Equal($"opc.tcp://127.0.0.1:{port}/pgw/simulator", found.EndpointUrl);
        Assert.Contains("None", found.SecurityPolicies); // the simulator only exposes None/None
        Assert.Contains("None", found.SecurityModes);
    }

    [Fact]
    public async Task DiscoverAsync_Throws_A_Readable_Error_For_An_Unreachable_Host()
    {
        var port = TestUtil.GetFreePort(); // free = guaranteed nothing listening there
        var certsPath = Path.Combine(Path.GetTempPath(), "pgw-discovery-test-" + Guid.NewGuid());

        await Assert.ThrowsAnyAsync<Exception>(() =>
            OpcUaDiscovery.DiscoverAsync($"opc.tcp://127.0.0.1:{port}/nothing-here", certsPath, autoAccept: true, default));
    }
}
