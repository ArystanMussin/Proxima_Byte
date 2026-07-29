using System.Net;
using FluentModbus;
using PGW.Core;
using PGW.Drivers.Modbus;
using Xunit;

namespace PGW.Core.Tests;

/// <summary>§13 acceptance test 12: a fake, non-Modbus, non-OPC-UA driver flows through the unmodified
/// Modbus TCP Server without a single protocol-specific line in the core or the interface.</summary>
public class CoreIsolationTests
{
    [Fact]
    public async Task Fake_Driver_Values_Reach_A_Real_Modbus_Client_Through_Unmodified_Core_And_Interface()
    {
        var log = new RingLog();
        var tagSpace = new TagSpace();
        var driver = new FakeProtocolDriver();
        var device = new DeviceHandle("fake_src", "fake_src");
        var def = new TagDefinition("fake_src.value", TagDataType.Int32, TagAccess.RO, device, "n/a");
        tagSpace.Register(def, driver, new TagAddress(device, "n/a"));

        using var cts = new CancellationTokenSource();
        await driver.ConnectAsync(device, cts.Token);
        _ = driver.StartPollingAsync(device, tagSpace, cts.Token);

        await TestUtil.WaitUntilAsync(() => tagSpace.Get("fake_src.value")?.Quality == TagQuality.Good, TimeSpan.FromSeconds(2));
        Assert.Equal(4242, tagSpace.Get("fake_src.value")!.Value);

        var port = TestUtil.GetFreePort();
        var output = new OutputConfig
        {
            Name = "out",
            Interface = "modbus_tcp_server",
            Settings = new() { ["bind"] = "127.0.0.1", ["port"] = port },
            Map = new() { new() { ["tag"] = "fake_src.value", ["unit_id"] = 1, ["area"] = "HR", ["address"] = 0, ["type"] = "int32" } },
        };
        Task<WriteResult> CoreWrite(string tagId, object? value, CancellationToken ct) =>
            tagSpace.WriteAsync(tagId, value, TimeSpan.FromSeconds(1), ct);

        var (iface, _, errors) = ModbusOutputFactory.Build(output, CoreWrite, log);
        Assert.Empty(errors);
        await iface.StartAsync(tagSpace, cts.Token);

        using var client = new ModbusTcpClient();
        client.Connect(new IPEndPoint(IPAddress.Loopback, port));

        ushort[] regs = [];
        await TestUtil.WaitUntilAsync(() =>
        {
            regs = client.ReadHoldingRegistersAsync<ushort>(1, 0, 2, default).GetAwaiter().GetResult().ToArray();
            return ModbusCodec.Decode(regs, TagDataType.Int32, WordOrder.ABCD) is 4242;
        }, TimeSpan.FromSeconds(3));

        Assert.Equal(4242, ModbusCodec.Decode(regs, TagDataType.Int32, WordOrder.ABCD));

        cts.Cancel();
        await iface.StopAsync(default);
        await driver.DisconnectAsync(device, default);
    }
}
