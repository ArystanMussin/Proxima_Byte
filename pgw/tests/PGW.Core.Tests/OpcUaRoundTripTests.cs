using PGW.Core;
using PGW.Drivers.OpcUa;
using PGW.Simulator;
using Xunit;

namespace PGW.Core.Tests;

/// <summary>
/// End-to-end OPC UA smoke test, same spirit as <see cref="ModbusRoundTripTests"/>: a real OPC UA
/// server (<see cref="SimulatedOpcUaServer"/>) is subscribed to by the real <see cref="OpcUaClientDriver"/>,
/// and a write is routed back through the Tag Space and confirmed on the server side.
/// </summary>
public class OpcUaRoundTripTests
{
    [Fact]
    public async Task OpcUa_Client_Subscribes_Reads_And_Writes_Through_Real_Server()
    {
        var port = TestUtil.GetFreePort();
        var certsPath = Path.Combine(Path.GetTempPath(), "pgw-test-certs-" + Guid.NewGuid());

        using var server = new SimulatedOpcUaServer();
        await server.StartAsync(port, Path.Combine(certsPath, "server"), default);

        var log = new RingLog();
        var tags = new Dictionary<string, OpcUaTagInfo>
        {
            ["sim.T1_supply"] = new OpcUaTagInfo("ns=1;s=T1_supply", TagDataType.Float64),
            ["sim.setpoint"] = new OpcUaTagInfo("ns=1;s=setpoint", TagDataType.Float64),
        };
        var cfg = new OpcUaDeviceSettings(
            EndpointUrl: $"opc.tcp://127.0.0.1:{port}/pgw/simulator",
            SecurityMode: "None",
            PublishingIntervalMs: 200,
            SamplingIntervalMs: 200,
            AutoAcceptUntrustedCertificates: true,
            CertsPath: Path.Combine(certsPath, "client"));

        var driver = new OpcUaClientDriver(cfg, tags, log);
        var device = new DeviceHandle("sim", "sim");
        var tagSpace = new TagSpace();
        tagSpace.Register(new TagDefinition("sim.T1_supply", TagDataType.Float64, TagAccess.RO, device, "ns=1;s=T1_supply"), driver, new TagAddress(device, "ns=1;s=T1_supply"));
        tagSpace.Register(new TagDefinition("sim.setpoint", TagDataType.Float64, TagAccess.RW, device, "ns=1;s=setpoint"), driver, new TagAddress(device, "ns=1;s=setpoint"));

        using var cts = new CancellationTokenSource();
        var connect = await driver.ConnectAsync(device, cts.Token);
        Assert.True(connect.Ok, connect.Error);
        _ = driver.StartPollingAsync(device, tagSpace, cts.Token);

        await TestUtil.WaitUntilAsync(() => tagSpace.Get("sim.T1_supply")?.Quality == TagQuality.Good, TimeSpan.FromSeconds(5));
        Assert.Equal(TagQuality.Good, tagSpace.Get("sim.setpoint")!.Quality);
        Assert.Equal(70.0, Convert.ToDouble(tagSpace.Get("sim.setpoint")!.Value));

        var writeResult = await tagSpace.WriteAsync("sim.setpoint", 42.5, TimeSpan.FromSeconds(3), cts.Token);
        Assert.True(writeResult.Ok, writeResult.ErrorCode);

        await TestUtil.WaitUntilAsync(() => Convert.ToDouble(tagSpace.Get("sim.setpoint")!.Value) == 42.5, TimeSpan.FromSeconds(5));

        cts.Cancel();
        await driver.DisconnectAsync(device, default);
    }

    [Fact]
    public async Task OpcUa_Client_Recovers_When_Server_Starts_Late()
    {
        var port = TestUtil.GetFreePort();
        var certsPath = Path.Combine(Path.GetTempPath(), "pgw-test-certs-" + Guid.NewGuid());

        var log = new RingLog();
        var tags = new Dictionary<string, OpcUaTagInfo>
        {
            ["sim.T1_supply"] = new OpcUaTagInfo("ns=1;s=T1_supply", TagDataType.Float64),
        };
        var cfg = new OpcUaDeviceSettings(
            EndpointUrl: $"opc.tcp://127.0.0.1:{port}/pgw/simulator",
            SecurityMode: "None",
            PublishingIntervalMs: 200,
            SamplingIntervalMs: 200,
            AutoAcceptUntrustedCertificates: true,
            CertsPath: Path.Combine(certsPath, "client"),
            ReconnectMinMs: 200,
            ReconnectMaxMs: 1000);

        var driver = new OpcUaClientDriver(cfg, tags, log);
        var device = new DeviceHandle("sim", "sim");
        var tagSpace = new TagSpace();
        tagSpace.Register(new TagDefinition("sim.T1_supply", TagDataType.Float64, TagAccess.RO, device, "ns=1;s=T1_supply"), driver, new TagAddress(device, "ns=1;s=T1_supply"));

        using var cts = new CancellationTokenSource();

        // Nothing is listening yet — this is the case that used to kill polling permanently.
        var connect = await driver.ConnectAsync(device, cts.Token);
        Assert.False(connect.Ok);
        _ = driver.StartPollingAsync(device, tagSpace, cts.Token);

        using var server = new SimulatedOpcUaServer();
        await server.StartAsync(port, Path.Combine(certsPath, "server"), default);

        await TestUtil.WaitUntilAsync(() => tagSpace.Get("sim.T1_supply")?.Quality == TagQuality.Good, TimeSpan.FromSeconds(20));
        Assert.Equal(TagQuality.Good, tagSpace.Get("sim.T1_supply")!.Quality);

        cts.Cancel();
        await driver.DisconnectAsync(device, default);
    }
}
