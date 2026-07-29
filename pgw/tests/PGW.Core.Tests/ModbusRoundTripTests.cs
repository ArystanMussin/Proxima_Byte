using System.Net;
using FluentModbus;
using PGW.Core;
using PGW.Drivers.Modbus;
using Xunit;

namespace PGW.Core.Tests;

/// <summary>
/// End-to-end smoke test for §13 item 1 style flow: a simulated PLC (plain FluentModbus server) is polled
/// by our Modbus TCP Client driver into the Tag Space, then rendered back out by our Modbus TCP Server
/// interface and read by a plain FluentModbus client acting as a SCADA system. Also exercises the RW
/// write-back path end to end.
/// </summary>
public class ModbusRoundTripTests
{
    [Fact]
    public async Task Modbus_To_Modbus_Round_Trip_Reads_And_Writes()
    {
        var plcPort = TestUtil.GetFreePort();
        var scadaPort = TestUtil.GetFreePort();

        using var plc = new ModbusTcpServer();
        plc.Start(new IPEndPoint(IPAddress.Loopback, plcPort));
        plc.AddUnit(1);
        SetHoldingRegisters(plc, 1, 100, 1234); // T1_supply raw value

        var log = new RingLog();
        var src = new SourceConfig
        {
            Name = "ctp_12",
            Driver = "modbus_tcp_client",
            Settings = new() { ["host"] = "127.0.0.1", ["port"] = plcPort, ["unit_id"] = 1, ["scan_rate_ms"] = 100 },
            Tags = new()
            {
                new() { ["name"] = "T1_supply", ["area"] = "HR", ["address"] = 100, ["type"] = "uint16", ["access"] = "RW" },
            },
        };

        var (driver, device, defs) = ModbusSourceFactory.Build(src, log);
        var tagSpace = new TagSpace();
        foreach (var (def, addr) in defs) tagSpace.Register(def, driver, addr);

        using var cts = new CancellationTokenSource();
        var connect = await driver.ConnectAsync(device, cts.Token);
        Assert.True(connect.Ok);
        _ = driver.StartPollingAsync(device, tagSpace, cts.Token);

        await TestUtil.WaitUntilAsync(() => tagSpace.Get("ctp_12.T1_supply")?.Quality == TagQuality.Good, TimeSpan.FromSeconds(5));
        Assert.Equal((ushort)1234, Convert.ToUInt16(tagSpace.Get("ctp_12.T1_supply")!.Value));

        var output = new OutputConfig
        {
            Name = "scada_slave",
            Interface = "modbus_tcp_server",
            Settings = new() { ["bind"] = "127.0.0.1", ["port"] = scadaPort, ["read_only"] = false },
            Map = new()
            {
                new() { ["tag"] = "ctp_12.T1_supply", ["unit_id"] = 1, ["area"] = "HR", ["address"] = 0, ["type"] = "uint16", ["rw"] = true },
            },
        };

        Task<WriteResult> CoreWrite(string tagId, object? value, CancellationToken ct) =>
            tagSpace.WriteAsync(tagId, value, TimeSpan.FromSeconds(2), ct);

        var (iface, map, errors) = ModbusOutputFactory.Build(output, CoreWrite, log);
        Assert.Empty(errors);
        await iface.StartAsync(tagSpace, cts.Token);

        using var scadaClient = new ModbusTcpClient();
        scadaClient.Connect(new IPEndPoint(IPAddress.Loopback, scadaPort));

        var read = (await scadaClient.ReadHoldingRegistersAsync<ushort>(1, 0, 1, cts.Token)).ToArray();
        Assert.Equal((ushort)1234, read[0]);

        // SCADA writes a new value; it must propagate all the way back to the simulated PLC.
        await scadaClient.WriteSingleRegisterAsync(1, 0, (ushort)5555, cts.Token);

        await TestUtil.WaitUntilAsync(() => GetHoldingRegister(plc, 1, 100) == 5555, TimeSpan.FromSeconds(5));
        Assert.Equal(5555, GetHoldingRegister(plc, 1, 100));

        cts.Cancel();
        await iface.StopAsync(default);
        await driver.DisconnectAsync(device, default);
    }

    private static void SetHoldingRegisters(ModbusTcpServer server, byte unit, int address, short value)
    {
        lock (server.Lock) { server.GetHoldingRegisters(unit)[address] = value; }
    }

    private static short GetHoldingRegister(ModbusTcpServer server, byte unit, int address)
    {
        lock (server.Lock) { return server.GetHoldingRegisters(unit)[address]; }
    }

}
