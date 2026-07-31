using System.Net;
using System.Net.Sockets;
using FluentModbus;
using PGW.Core;
using PGW.Drivers.Modbus;
using Xunit;

namespace PGW.Core.Tests;

/// <summary>
/// A TCP loopback socket standing in for a real serial port, implementing FluentModbus's
/// <see cref="IModbusRtuSerialPort"/>. There's no real RS-485/COM port in CI or this sandbox, so this is
/// what makes an actually-real (not mocked) RTU round trip possible: real MBAP-less RTU framing, real
/// CRC16, real byte stream — only the physical transport underneath is virtualized, exactly the same
/// substitution <c>ModbusClientDriverFactory.CreateRtu</c> makes for production (it calls
/// <c>ModbusRtuClient.Connect(portName)</c> instead of <c>.Initialize(fakePort, ...)</c>); the shared
/// polling/encoding logic under test — <see cref="ModbusClientDriverBase"/> — is bit-for-bit what a real
/// deployment runs.
/// </summary>
internal sealed class LoopbackSerialPort(Socket socket, string name) : IModbusRtuSerialPort
{
    public string PortName => name;
    public bool IsOpen { get; private set; } = true;
    public void Open() => IsOpen = true;
    public void Close() { IsOpen = false; try { socket.Close(); } catch (Exception) { } }
    public int Read(byte[] buffer, int offset, int count) => socket.Receive(buffer, offset, count, SocketFlags.None);
    public async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        await socket.ReceiveAsync(new ArraySegment<byte>(buffer, offset, count), SocketFlags.None, ct);
    public void Write(byte[] buffer, int offset, int count) => socket.Send(buffer, offset, count, SocketFlags.None);
    public async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        await socket.SendAsync(new ArraySegment<byte>(buffer, offset, count), SocketFlags.None, ct);
}

public class ModbusRtuTests
{
    private static async Task<(LoopbackSerialPort ServerPort, LoopbackSerialPort ClientPort)> MakeLoopbackPairAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var acceptTask = listener.AcceptSocketAsync();
        var clientSocket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await clientSocket.ConnectAsync(IPAddress.Loopback, port);
        var serverSocket = await acceptTask;
        listener.Stop();
        return (new LoopbackSerialPort(serverSocket, "SERVER"), new LoopbackSerialPort(clientSocket, "CLIENT"));
    }

    [Fact]
    public async Task Rtu_Client_Reads_And_Writes_Through_A_Real_Rtu_Server()
    {
        var (serverPort, clientPort) = await MakeLoopbackPairAsync();

        using var server = new ModbusRtuServer(1);
        server.Start(serverPort);
        lock (server.Lock) { server.GetHoldingRegisters(1)[0] = 4321; }

        var log = new RingLog();
        var cfg = new ModbusPollSettings(UnitId: 1, TimeoutMs: 2000);
        var block = new ReadBlock(ModbusArea.HoldingRegister, 0, 1, new[] { ("meter.value", ModbusAddress.Parse("HR:0"), TagDataType.UInt16, 0) });
        var blocksByScanRate = new Dictionary<int, List<ReadBlock>> { [200] = new() { block } };
        var wireByAddr = new Dictionary<string, TagWireInfo> { ["HR:0"] = new(TagDataType.UInt16, WordOrder.ABCD) };

        var rtuClient = new ModbusRtuClient { ReadTimeout = 2000, WriteTimeout = 2000 };
        Task Connect(ModbusClient c, CancellationToken ct) { ((ModbusRtuClient)c).Initialize(clientPort, ModbusEndianness.LittleEndian); return Task.CompletedTask; }
        void Disconnect(ModbusClient c) => clientPort.Close();

        var driver = new ModbusClientDriverBase(ModbusRtuSourceFactory.TypeId, rtuClient, Connect, Disconnect, cfg,
            blocksByScanRate, wireByAddr, new List<string> { "meter.value" }, log);

        var device = new DeviceHandle("meter", "meter");
        var tagSpace = new TagSpace();
        tagSpace.Register(new TagDefinition("meter.value", TagDataType.UInt16, TagAccess.RW, device, "HR:0"), driver, new TagAddress(device, "HR:0"));

        using var cts = new CancellationTokenSource();
        var connect = await driver.ConnectAsync(device, cts.Token);
        Assert.True(connect.Ok, connect.Error);
        _ = driver.StartPollingAsync(device, tagSpace, cts.Token);

        await TestUtil.WaitUntilAsync(() => tagSpace.Get("meter.value")?.Quality == TagQuality.Good, TimeSpan.FromSeconds(5));
        Assert.Equal((ushort)4321, Convert.ToUInt16(tagSpace.Get("meter.value")!.Value));

        var writeResult = await tagSpace.WriteAsync("meter.value", (ushort)999, TimeSpan.FromSeconds(3), cts.Token);
        Assert.True(writeResult.Ok, writeResult.ErrorCode);

        await TestUtil.WaitUntilAsync(() => Convert.ToUInt16(tagSpace.Get("meter.value")!.Value) == 999, TimeSpan.FromSeconds(5));

        short onWire;
        lock (server.Lock) { onWire = server.GetHoldingRegisters(1)[0]; }
        Assert.Equal((short)999, onWire);

        cts.Cancel();
        await driver.DisconnectAsync(device, default);
    }

    [Fact]
    public async Task Rtu_Client_Reports_Bad_Quality_When_The_Transport_Drops()
    {
        // The reconnect/backoff loop itself is shared code with ModbusTcpClientDriver's already-covered
        // path (ModbusRoundTripTests, CoreIsolationTests) — what's RTU-specific and worth proving here is
        // that a dead serial transport is detected and surfaced as Bad quality rather than hanging or
        // throwing out of the poll loop.
        var (serverPort, clientPort) = await MakeLoopbackPairAsync();

        using var server = new ModbusRtuServer(1);
        server.Start(serverPort);
        lock (server.Lock) { server.GetHoldingRegisters(1)[0] = 111; }

        var log = new RingLog();
        var cfg = new ModbusPollSettings(UnitId: 1, TimeoutMs: 300, AutoDemoteAfter: 1, AutoDemoteSeconds: 1);
        var block = new ReadBlock(ModbusArea.HoldingRegister, 0, 1, new[] { ("meter.value", ModbusAddress.Parse("HR:0"), TagDataType.UInt16, 0) });
        var blocksByScanRate = new Dictionary<int, List<ReadBlock>> { [100] = new() { block } };
        var wireByAddr = new Dictionary<string, TagWireInfo> { ["HR:0"] = new(TagDataType.UInt16, WordOrder.ABCD) };

        var rtuClient = new ModbusRtuClient { ReadTimeout = 300, WriteTimeout = 300 };
        Task Connect(ModbusClient c, CancellationToken ct) { ((ModbusRtuClient)c).Initialize(clientPort, ModbusEndianness.LittleEndian); return Task.CompletedTask; }
        void Disconnect(ModbusClient c) => clientPort.Close();

        var driver = new ModbusClientDriverBase(ModbusRtuSourceFactory.TypeId, rtuClient, Connect, Disconnect, cfg,
            blocksByScanRate, wireByAddr, new List<string> { "meter.value" }, log);

        var device = new DeviceHandle("meter", "meter");
        var tagSpace = new TagSpace();
        tagSpace.Register(new TagDefinition("meter.value", TagDataType.UInt16, TagAccess.RO, device, "HR:0"), driver, new TagAddress(device, "HR:0"));

        using var cts = new CancellationTokenSource();
        Assert.True((await driver.ConnectAsync(device, cts.Token)).Ok);
        _ = driver.StartPollingAsync(device, tagSpace, cts.Token);
        await TestUtil.WaitUntilAsync(() => tagSpace.Get("meter.value")?.Quality == TagQuality.Good, TimeSpan.FromSeconds(3));

        // Sever the transport (simulates a cable pull) — the driver must mark the tag Bad instead of
        // hanging or crashing the poll loop.
        serverPort.Close();
        clientPort.Close();

        await TestUtil.WaitUntilAsync(() => tagSpace.Get("meter.value")?.Quality == TagQuality.Bad, TimeSpan.FromSeconds(5));

        cts.Cancel();
        await driver.DisconnectAsync(device, default);
    }

    [Fact]
    public void Build_Requires_A_Port()
    {
        var src = new SourceConfig
        {
            Name = "meter1",
            Driver = ModbusRtuSourceFactory.TypeId,
            Settings = new() { ["baud_rate"] = 9600 },
            Tags = new() { new() { ["name"] = "value", ["area"] = "HR", ["address"] = 0, ["type"] = "uint16" } },
        };

        var ex = Assert.Throws<FormatException>(() => ModbusRtuSourceFactory.Build(src, new RingLog()));
        Assert.Contains("port", ex.Message);
    }

    [Fact]
    public void Build_Parses_Serial_Settings_And_Tags_Without_Opening_A_Real_Port()
    {
        var src = new SourceConfig
        {
            Name = "meter1",
            Driver = ModbusRtuSourceFactory.TypeId,
            Settings = new()
            {
                ["serial_port"] = "/dev/ttyUSB0",
                ["baud_rate"] = 2400,
                ["parity"] = "none",
                ["stop_bits"] = "two",
                ["unit_id"] = 5,
            },
            Tags = new()
            {
                new() { ["name"] = "active_energy", ["area"] = "HR", ["address"] = 0, ["type"] = "float32", ["units"] = "kWh" },
            },
        };

        var (driver, device, tags) = ModbusRtuSourceFactory.Build(src, new RingLog());

        Assert.Equal(ModbusRtuSourceFactory.TypeId, driver.DriverTypeId);
        Assert.Equal("meter1", device.Channel);
        Assert.Single(tags);
        Assert.Equal("meter1.active_energy", tags[0].Def.Id);
        Assert.Equal(TagDataType.Float32, tags[0].Def.DataType);
        Assert.Equal("kWh", tags[0].Def.Units);
    }
}
