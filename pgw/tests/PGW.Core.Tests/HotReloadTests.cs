using System.Net;
using FluentModbus;
using PGW.Core;
using PGW.Host;
using Xunit;

namespace PGW.Core.Tests;

file sealed class TempPathProvider : IPathProvider
{
    public string ConfigDir { get; }
    public string DataDir { get; }
    public string LogsDir { get; }

    public TempPathProvider()
    {
        var root = Path.Combine(Path.GetTempPath(), "pgw-reload-test-" + Guid.NewGuid());
        ConfigDir = Path.Combine(root, "config");
        DataDir = Path.Combine(root, "data");
        LogsDir = Path.Combine(root, "logs");
        Directory.CreateDirectory(ConfigDir);
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LogsDir);
    }
}

/// <summary>
/// §7.3: reload must not disrupt configuration correctness, and — since this session's rework — must
/// not disrupt already-open Modbus TCP Server sockets for outputs whose config didn't change either.
/// Proven here against a real GatewayEngine wired to real FluentModbus servers/clients over loopback
/// sockets, not mocks: an unrelated config edit must leave an already-connected SCADA client's TCP
/// connection alive and serviceable, while an edit that actually touches an entity must still restart
/// exactly that entity.
/// </summary>
public class HotReloadTests
{
    private static string WriteConfig(string path, int plcPort, int scadaPort, int scanRateMs)
    {
        var yaml = $"""
            gateway:
              name: reload-test
            sources:
              - name: ctp_12
                driver: modbus_tcp_client
                host: 127.0.0.1
                port: {plcPort}
                unit_id: 1
                scan_rate_ms: {scanRateMs}
                tags:
                  - name: T1_supply
                    area: HR
                    address: 0
                    type: uint16
            outputs:
              - name: scada_slave
                interface: modbus_tcp_server
                bind: 127.0.0.1
                port: {scadaPort}
                read_only: true
                map:
                  - tag: ctp_12.T1_supply
                    unit_id: 1
                    area: HR
                    address: 0
                    type: uint16
            """;
        File.WriteAllText(path, yaml);
        return path;
    }

    private static void SeedPlc(ModbusTcpServer plc, short value)
    {
        lock (plc.Lock) { plc.GetHoldingRegisters(1)[0] = value; }
    }

    [Fact]
    public async Task Reload_Keeps_An_Unchanged_Outputs_Live_Client_Connected_When_Only_A_Source_Changes()
    {
        var plcPort = TestUtil.GetFreePort();
        var scadaPort = TestUtil.GetFreePort();

        using var plc = new ModbusTcpServer();
        plc.Start(new IPEndPoint(IPAddress.Loopback, plcPort));
        plc.AddUnit(1);
        SeedPlc(plc, 1234);

        var paths = new TempPathProvider();
        var configPath = WriteConfig(Path.Combine(paths.ConfigDir, "project.yaml"), plcPort, scadaPort, scanRateMs: 200);

        var engine = new GatewayEngine(paths, configPath);
        Assert.Empty(engine.LoadAndValidate());
        using var cts = new CancellationTokenSource();
        await engine.StartAsync(cts.Token);

        // A SCADA client connects and stays connected across the reload below — this is the socket
        // that must survive. StartAsync returns once drivers are connected, but the first poll tick
        // (and thus the output's register buffer) runs concurrently in the background, so wait for it
        // rather than racing it.
        using var scada = new ModbusTcpClient { ReadTimeout = 2000 };
        scada.Connect(new IPEndPoint(IPAddress.Loopback, scadaPort));
        await TestUtil.WaitUntilAsync(() =>
        {
            var v = scada.ReadHoldingRegistersAsync<ushort>(1, 0, 1, cts.Token).GetAwaiter().GetResult().ToArray();
            return v[0] == 1234;
        }, TimeSpan.FromSeconds(3));

        // Change only the source's scan rate — the output's config is byte-for-byte identical.
        WriteConfig(configPath, plcPort, scadaPort, scanRateMs: 250);
        var errors = await engine.ReloadAsync();
        Assert.Empty(errors);

        // The already-open connection must still work — no reconnect performed here.
        var after = (await scada.ReadHoldingRegistersAsync<ushort>(1, 0, 1, cts.Token)).ToArray();
        Assert.Equal((ushort)1234, after[0]);

        // And the source itself must still be alive and polling under the new scan rate.
        SeedPlc(plc, 5678);
        await TestUtil.WaitUntilAsync(() =>
        {
            var v = scada.ReadHoldingRegistersAsync<ushort>(1, 0, 1, cts.Token).GetAwaiter().GetResult().ToArray();
            return v[0] == 5678;
        }, TimeSpan.FromSeconds(3));

        await engine.StopAsync();
    }

    [Fact]
    public async Task Reload_Restarts_An_Output_Whose_Own_Config_Actually_Changed()
    {
        var plcPort = TestUtil.GetFreePort();
        var scadaPort = TestUtil.GetFreePort();
        var newScadaPort = TestUtil.GetFreePort();

        using var plc = new ModbusTcpServer();
        plc.Start(new IPEndPoint(IPAddress.Loopback, plcPort));
        plc.AddUnit(1);
        SeedPlc(plc, 42);

        var paths = new TempPathProvider();
        var configPath = WriteConfig(Path.Combine(paths.ConfigDir, "project.yaml"), plcPort, scadaPort, scanRateMs: 200);

        var engine = new GatewayEngine(paths, configPath);
        Assert.Empty(engine.LoadAndValidate());
        using var cts = new CancellationTokenSource();
        await engine.StartAsync(cts.Token);

        using var oldScada = new ModbusTcpClient { ReadTimeout = 2000 };
        oldScada.Connect(new IPEndPoint(IPAddress.Loopback, scadaPort));
        await TestUtil.WaitUntilAsync(() =>
        {
            var v = oldScada.ReadHoldingRegistersAsync<ushort>(1, 0, 1, cts.Token).GetAwaiter().GetResult().ToArray();
            return v[0] == 42;
        }, TimeSpan.FromSeconds(3));

        // Move the output to a different port — this output's config genuinely changed.
        WriteConfig(configPath, plcPort, newScadaPort, scanRateMs: 200);
        var errors = await engine.ReloadAsync();
        Assert.Empty(errors);

        // The old socket must actually be gone (the listener behind it was torn down and rebuilt).
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await oldScada.ReadHoldingRegistersAsync<ushort>(1, 0, 1, cts.Token));

        // The new port must be serving current data.
        using var newScada = new ModbusTcpClient();
        newScada.Connect(new IPEndPoint(IPAddress.Loopback, newScadaPort));
        Assert.Equal((ushort)42, (await newScada.ReadHoldingRegistersAsync<ushort>(1, 0, 1, cts.Token)).ToArray()[0]);

        await engine.StopAsync();
    }
}
