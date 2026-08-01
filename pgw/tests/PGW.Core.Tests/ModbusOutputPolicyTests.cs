using System.Net;
using FluentModbus;
using PGW.Core;
using PGW.Drivers.Modbus;
using Xunit;

namespace PGW.Core.Tests;

/// <summary>
/// Real-socket tests for the two Modbus TCP Server output policies that were config-only before:
/// max_connections (now enforced via FluentModbus's own <c>ModbusTcpServer.MaxConnections</c>) and
/// on_bad: freeze_and_flag (now sets/clears a dedicated Coil/DI status bit, not just documented as
/// "behaves like hold").
/// </summary>
public class ModbusOutputPolicyTests
{
    private static (TagSpace TagSpace, DeviceHandle Device) RegisterTag(string tagId, TagDataType type, TagAccess access = TagAccess.RO)
    {
        var tagSpace = new TagSpace();
        var device = new DeviceHandle("src", "src");
        var def = new TagDefinition(tagId, type, access, device, "n/a");
        tagSpace.Register(def, new FakeProtocolDriver(), new TagAddress(device, "n/a"));
        return (tagSpace, device);
    }

    [Fact]
    public async Task MaxConnections_Rejects_Clients_Beyond_The_Limit()
    {
        var (tagSpace, _) = RegisterTag("src.value", TagDataType.UInt16);
        tagSpace.Publish("src.value", (ushort)7, TagQuality.Good, DateTime.UtcNow);

        var port = TestUtil.GetFreePort();
        var output = new OutputConfig
        {
            Name = "out",
            Interface = "modbus_tcp_server",
            Settings = new() { ["bind"] = "127.0.0.1", ["port"] = port, ["max_connections"] = 2 },
            Map = new() { new() { ["tag"] = "src.value", ["unit_id"] = 1, ["area"] = "HR", ["address"] = 0, ["type"] = "uint16" } },
        };
        var (iface, _, errors) = ModbusOutputFactory.Build(output, (_, _, _) => Task.FromResult(WriteResult.Success()), new RingLog());
        Assert.Empty(errors);

        using var cts = new CancellationTokenSource();
        await iface.StartAsync(tagSpace, cts.Token);

        var clients = new List<ModbusTcpClient>();
        for (int i = 0; i < 4; i++)
        {
            var c = new ModbusTcpClient { ConnectTimeout = 500, ReadTimeout = 500 };
            c.Connect(new IPEndPoint(IPAddress.Loopback, port));
            clients.Add(c);
            await Task.Delay(100); // let the server's accept loop catch up before the next connect
        }

        var results = new bool[4];
        for (int i = 0; i < 4; i++)
        {
            try { await clients[i].ReadHoldingRegistersAsync<ushort>(1, 0, 1, cts.Token); results[i] = true; }
            catch { results[i] = false; }
        }

        Assert.Equal(2, results.Count(ok => ok));
        Assert.True(results[0] && results[1], "the first two connections (within the limit) must succeed");
        Assert.False(results[2] || results[3], "connections beyond max_connections must not be serviced");

        await iface.StopAsync(default);
    }

    /// <summary>§13 test 4 / §9.2: 8 simultaneous Modbus clients against one output, all get correct
    /// answers, none blocks another. §9.2 flagged this as fully reproducible in code (unlike the hardware-
    /// dependent tests in that table) — this proves it directly rather than relying on the adjacent
    /// max_connections test (which only proves the limit is enforced at N=2, not that N=8 within a much
    /// higher limit runs concurrently without serialization).</summary>
    [Fact]
    public async Task Eight_Concurrent_Modbus_Clients_Get_Correct_Responses_Without_Blocking()
    {
        var (tagSpace, _) = RegisterTag("src.value", TagDataType.UInt16);
        tagSpace.Publish("src.value", (ushort)4242, TagQuality.Good, DateTime.UtcNow);

        var port = TestUtil.GetFreePort();
        var output = new OutputConfig
        {
            Name = "out",
            Interface = "modbus_tcp_server",
            Settings = new() { ["bind"] = "127.0.0.1", ["port"] = port, ["max_connections"] = 16 },
            Map = new() { new() { ["tag"] = "src.value", ["unit_id"] = 1, ["area"] = "HR", ["address"] = 0, ["type"] = "uint16" } },
        };
        var (iface, _, errors) = ModbusOutputFactory.Build(output, (_, _, _) => Task.FromResult(WriteResult.Success()), new RingLog());
        Assert.Empty(errors);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await iface.StartAsync(tagSpace, cts.Token);

        const int clientCount = 8;
        var clients = new ModbusTcpClient[clientCount];
        for (var i = 0; i < clientCount; i++)
        {
            var c = new ModbusTcpClient { ConnectTimeout = 2000, ReadTimeout = 2000 };
            c.Connect(new IPEndPoint(IPAddress.Loopback, port));
            clients[i] = c;
        }

        // Every client reads several times concurrently with every other client — if the server (or
        // anything behind it) ever serialized clients instead of servicing them independently, this
        // would either time out or take roughly clientCount times as long as a single round trip.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var round = 0; round < 5; round++)
        {
            var reads = clients.Select(c => c.ReadHoldingRegistersAsync<ushort>(1, 0, 1, cts.Token)).ToArray();
            var results = await Task.WhenAll(reads);
            foreach (var r in results) Assert.Equal((ushort)4242, r.ToArray()[0]);
        }
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"8 concurrent clients over 5 rounds took {sw.Elapsed} — looks serialized/blocked");

        foreach (var c in clients) c.Disconnect();
        await iface.StopAsync(default);
    }

    [Fact]
    public async Task FreezeAndFlag_Freezes_Value_And_Sets_Flag_Bit_While_Bad_Then_Clears_It()
    {
        var (tagSpace, _) = RegisterTag("src.value", TagDataType.UInt16, TagAccess.RO);
        tagSpace.Publish("src.value", (ushort)123, TagQuality.Good, DateTime.UtcNow);

        var port = TestUtil.GetFreePort();
        var output = new OutputConfig
        {
            Name = "out",
            Interface = "modbus_tcp_server",
            Settings = new() { ["bind"] = "127.0.0.1", ["port"] = port },
            Map = new()
            {
                new()
                {
                    ["tag"] = "src.value", ["unit_id"] = 1, ["area"] = "HR", ["address"] = 0, ["type"] = "uint16",
                    ["on_bad"] = "freeze_and_flag", ["flag_area"] = "DI", ["flag_address"] = 5,
                },
            },
        };
        var (iface, _, errors) = ModbusOutputFactory.Build(output, (_, _, _) => Task.FromResult(WriteResult.Success()), new RingLog());
        Assert.Empty(errors);

        using var cts = new CancellationTokenSource();
        await iface.StartAsync(tagSpace, cts.Token);

        using var client = new ModbusTcpClient { ConnectTimeout = 1000, ReadTimeout = 1000 };
        client.Connect(new IPEndPoint(IPAddress.Loopback, port));

        var value = (await client.ReadHoldingRegistersAsync<ushort>(1, 0, 1, cts.Token)).ToArray();
        Assert.Equal((ushort)123, value[0]);
        var flag = (await client.ReadDiscreteInputsAsync(1, 5, 1, cts.Token)).ToArray();
        Assert.False(ModbusCodec.GetPackedBit(flag, 0), "flag must be clear while quality is Good");

        // Source goes Bad: value must freeze at 123 (not zero, not reset) and the flag bit must set.
        tagSpace.Publish("src.value", null, TagQuality.Bad, DateTime.UtcNow, QualitySubCode.CommFailure);
        await TestUtil.WaitUntilAsync(() =>
        {
            var f = client.ReadDiscreteInputsAsync(1, 5, 1, cts.Token).GetAwaiter().GetResult().ToArray();
            return ModbusCodec.GetPackedBit(f, 0);
        }, TimeSpan.FromSeconds(3));

        value = (await client.ReadHoldingRegistersAsync<ushort>(1, 0, 1, cts.Token)).ToArray();
        Assert.Equal((ushort)123, value[0]); // frozen, not zeroed

        // Source recovers: flag must clear, and the fresh value must come through again.
        tagSpace.Publish("src.value", (ushort)456, TagQuality.Good, DateTime.UtcNow);
        await TestUtil.WaitUntilAsync(() =>
        {
            var f = client.ReadDiscreteInputsAsync(1, 5, 1, cts.Token).GetAwaiter().GetResult().ToArray();
            return !ModbusCodec.GetPackedBit(f, 0);
        }, TimeSpan.FromSeconds(3));

        value = (await client.ReadHoldingRegistersAsync<ushort>(1, 0, 1, cts.Token)).ToArray();
        Assert.Equal((ushort)456, value[0]);

        await iface.StopAsync(default);
    }

    [Fact]
    public void FreezeAndFlag_Without_FlagAddress_Is_Reported_As_A_Config_Error()
    {
        var output = new OutputConfig
        {
            Name = "out",
            Interface = "modbus_tcp_server",
            Settings = new() { ["bind"] = "127.0.0.1", ["port"] = TestUtil.GetFreePort() },
            Map = new() { new() { ["tag"] = "src.value", ["unit_id"] = 1, ["area"] = "HR", ["address"] = 0, ["type"] = "uint16", ["on_bad"] = "freeze_and_flag" } },
        };
        var (_, _, errors) = ModbusOutputFactory.Build(output, (_, _, _) => Task.FromResult(WriteResult.Success()), new RingLog());
        Assert.Contains(errors, e => e.Contains("flag_address"));
    }
}
