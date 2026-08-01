using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using PGW.Core;
using PGW.Drivers.Mercury;
using PGW.Drivers.Modbus;
using PGW.Host;
using Xunit;

namespace PGW.Core.Tests;

/// <summary>
/// A hand-rolled "device" sitting on the server side of a shared physical bus (a loopback socket standing
/// in for RS-485, per <see cref="SerialTransport"/>'s virtualization seam), speaking for TWO different
/// protocols at two different addresses on the same wire — exactly §13 test 15's scenario. Framing is
/// address-first for both protocols, so this reads one address byte, then continues framing per whichever
/// protocol owns that address; the real correctness check is that both protocols' own CRCs verify and
/// nothing gets routed to the wrong handler, which would only happen if two transactions' bytes actually
/// interleaved on the wire.
/// </summary>
internal sealed class SharedBusResponder(Socket busSocket, byte modbusUnitId, ushort holdingRegisterValue, byte mercuryAddress, uint mercuryEnergyMWh)
{
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            byte[] addr;
            try { addr = await ReadExactAsync(1, ct); }
            catch (Exception) { return; } // socket closed / cancelled — test is winding down

            if (addr[0] == modbusUnitId) await HandleModbusAsync(ct);
            else if (addr[0] == mercuryAddress) await HandleMercuryAsync(ct);
            else throw new InvalidOperationException($"shared bus responder: frame addressed to unexpected id 0x{addr[0]:X2} — the two devices' bytes may have interleaved");
        }
    }

    private async Task HandleModbusAsync(CancellationToken ct)
    {
        // function(1) + start(2) + quantity(2) + crc(2) — read-holding-registers request, the only
        // function ModbusClientDriverBase issues for a HoldingRegister ReadBlock.
        var rest = await ReadExactAsync(7, ct);
        var request = new byte[] { modbusUnitId }.Concat(rest).ToArray();
        if (!MercuryCodec.VerifyCrc(request))
            throw new InvalidOperationException("shared bus responder: bad CRC on a modbus-addressed frame — frames may have corrupted each other");
        if (rest[0] != 0x03) throw new InvalidOperationException($"shared bus responder: unsupported modbus function 0x{rest[0]:X2}");

        var quantity = (rest[3] << 8) | rest[4];
        var data = new byte[quantity * 2];
        for (var i = 0; i < quantity; i++)
        {
            // ModbusRtuClient.Initialize(..., ModbusEndianness.LittleEndian) is what CreateRtu uses for a
            // shared transport (matching Connect(string)'s own default) — register bytes go on the wire
            // low byte first to match, not the "network order" a real Modbus server usually uses.
            data[i * 2] = (byte)holdingRegisterValue;
            data[i * 2 + 1] = (byte)(holdingRegisterValue >> 8);
        }
        var response = MercuryCodec.AppendCrc(new byte[] { modbusUnitId, 0x03, (byte)(quantity * 2) }.Concat(data).ToArray());
        await busSocket.SendAsync(response, SocketFlags.None, ct);
    }

    private async Task HandleMercuryAsync(CancellationToken ct)
    {
        var command = (await ReadExactAsync(1, ct))[0];
        var extra = command switch
        {
            0x01 => 7, // CONNECT: access level(1) + password(6), then CRC(2)
            0x02 => 0, // CLOSE: just CRC(2)
            0x05 => 2, // LIST: period + tariff, then CRC(2)
            _ => throw new InvalidOperationException($"shared bus responder: unknown mercury command 0x{command:X2}"),
        };
        var rest = await ReadExactAsync(extra + 2, ct);
        var request = new byte[] { mercuryAddress, command }.Concat(rest).ToArray();
        if (!MercuryCodec.VerifyCrc(request))
            throw new InvalidOperationException("shared bus responder: bad CRC on a mercury-addressed frame — frames may have corrupted each other");

        byte[] response = command switch
        {
            0x01 or 0x02 => MercuryCodec.AppendCrc(new byte[] { mercuryAddress }),
            0x05 => BuildEnergyResponse(),
            _ => throw new InvalidOperationException("unreachable"),
        };
        await busSocket.SendAsync(response, SocketFlags.None, ct);
    }

    private byte[] BuildEnergyResponse()
    {
        var data = new byte[16];
        EncodeU32(mercuryEnergyMWh).CopyTo(data, 0);
        return MercuryCodec.AppendCrc(new byte[] { mercuryAddress }.Concat(data).ToArray());
    }

    private static byte[] EncodeU32(uint value) =>
        new[] { (byte)(value >> 16), (byte)(value >> 24), (byte)value, (byte)(value >> 8) };

    private async Task<byte[]> ReadExactAsync(int count, CancellationToken ct)
    {
        var buf = new byte[count];
        var received = 0;
        while (received < count)
        {
            var n = await busSocket.ReceiveAsync(new ArraySegment<byte>(buf, received, count - received), SocketFlags.None, ct);
            if (n == 0) throw new IOException("shared bus responder: connection closed mid-frame");
            received += n;
        }
        return buf;
    }
}

file sealed class SerialTransportTempPathProvider : IPathProvider
{
    public string ConfigDir { get; }
    public string DataDir { get; }
    public string LogsDir { get; }

    public SerialTransportTempPathProvider()
    {
        var root = Path.Combine(Path.GetTempPath(), "pgw-serial-transport-test-" + Guid.NewGuid());
        ConfigDir = Path.Combine(root, "config");
        DataDir = Path.Combine(root, "data");
        LogsDir = Path.Combine(root, "logs");
        Directory.CreateDirectory(ConfigDir);
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LogsDir);
    }
}

/// <summary>§4.1.1 / §13 test 15: a Modbus RTU device and a Mercury device sharing one physical
/// serial_transport must not corrupt each other's frames, and mismatched port settings across sources
/// sharing a transport name must be rejected as a config error.</summary>
public class SerialTransportTests
{
    private static async Task<(Socket Bus, Socket Client)> MakeBusPairAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var acceptTask = listener.AcceptSocketAsync();
        var clientSocket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await clientSocket.ConnectAsync(IPAddress.Loopback, port);
        var busSocket = await acceptTask;
        listener.Stop();
        return (busSocket, clientSocket);
    }

    [Fact]
    public async Task Modbus_Rtu_And_Mercury_Devices_Share_One_SerialTransport_Without_Frame_Corruption()
    {
        var (busSocket, clientSocket) = await MakeBusPairAsync();

        const byte modbusUnitId = 1;
        const ushort holdingRegisterValue = 4321;
        const byte mercuryAddress = 2;
        const uint mercuryEnergyMWh = 123456;

        var responder = new SharedBusResponder(busSocket, modbusUnitId, holdingRegisterValue, mercuryAddress, mercuryEnergyMWh);
        using var responderCts = new CancellationTokenSource();
        var responderTask = responder.RunAsync(responderCts.Token);

        // The one physical bus both devices below will share — a real deployment opens exactly one
        // System.IO.Ports.SerialPort here; the test virtualizes that with a loopback socket instead.
        var transport = new SerialTransport("bus_1", "TESTBUS", 9600, Parity.Even, StopBits.One);
        transport.AttachVirtualStreamForTesting(new NetworkStream(clientSocket, ownsSocket: true));

        var log = new RingLog();
        var tagSpace = new TagSpace();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        // Device 1: Modbus RTU, unit_id 1, polling every 150ms.
        var rtuCfg = new ModbusRtuDeviceSettings(PortName: "TESTBUS", UnitId: modbusUnitId, TimeoutMs: 2000);
        var block = new ReadBlock(ModbusArea.HoldingRegister, 0, 1, new[] { ("modbus_dev.value", ModbusAddress.Parse("HR:0"), TagDataType.UInt16, 0) });
        var blocksByScanRate = new Dictionary<int, List<ReadBlock>> { [150] = new() { block } };
        var wireByAddr = new Dictionary<string, TagWireInfo> { ["HR:0"] = new(TagDataType.UInt16, WordOrder.ABCD) };
        var modbusDriver = ModbusClientDriverFactory.CreateRtu(rtuCfg, blocksByScanRate, wireByAddr, new List<string> { "modbus_dev.value" }, log, transport);
        var modbusDevice = new DeviceHandle("modbus_dev", "modbus_dev");
        tagSpace.Register(new TagDefinition("modbus_dev.value", TagDataType.UInt16, TagAccess.RO, modbusDevice, "HR:0"), modbusDriver, new TagAddress(modbusDevice, "HR:0"));

        // Device 2: Mercury, address 2, polling every 300ms — a different protocol, same physical wire.
        var mercuryCfg = new MercuryDeviceSettings(Address: mercuryAddress, AccessLevel: 1, ScanRateMs: 300, ResponseTimeoutMs: 2000, InterByteGapMs: 150);
        var mercuryTransport = new SharedSerialMercuryTransport(transport);
        var bindings = new List<MercuryTagBinding> { new("mercury_dev.energy", MercuryParam.EnergyActiveTotal) };
        var mercuryDriver = new MercuryClientDriver(mercuryTransport, mercuryCfg, bindings, log);
        var mercuryDevice = new DeviceHandle("mercury_dev", "mercury_dev");
        tagSpace.Register(new TagDefinition("mercury_dev.energy", TagDataType.Float64, TagAccess.RO, mercuryDevice, "EnergyActiveTotal"), mercuryDriver, new TagAddress(mercuryDevice, "EnergyActiveTotal"));

        var connect1 = await modbusDriver.ConnectAsync(modbusDevice, cts.Token);
        Assert.True(connect1.Ok, connect1.Error);
        var connect2 = await mercuryDriver.ConnectAsync(mercuryDevice, cts.Token);
        Assert.True(connect2.Ok, connect2.Error);

        _ = modbusDriver.StartPollingAsync(modbusDevice, tagSpace, cts.Token);
        _ = mercuryDriver.StartPollingAsync(mercuryDevice, tagSpace, cts.Token);

        await TestUtil.WaitUntilAsync(() => tagSpace.Get("modbus_dev.value")?.Quality == TagQuality.Good, TimeSpan.FromSeconds(5));
        await TestUtil.WaitUntilAsync(() => tagSpace.Get("mercury_dev.energy")?.Quality == TagQuality.Good, TimeSpan.FromSeconds(5));
        Assert.Equal(holdingRegisterValue, Convert.ToUInt16(tagSpace.Get("modbus_dev.value")!.Value));
        Assert.Equal(mercuryEnergyMWh / 1000.0, Convert.ToDouble(tagSpace.Get("mercury_dev.energy")!.Value));

        // Keep both polling concurrently for a stretch — this is where interleaved writes on the shared
        // wire would show up (as a CRC failure inside SharedBusResponder, surfacing as Bad quality or the
        // responder task faulting), if AcquireAsync's mutual exclusion weren't actually protecting the bus.
        await Task.Delay(1500, CancellationToken.None);
        Assert.Equal(TagQuality.Good, tagSpace.Get("modbus_dev.value")!.Quality);
        Assert.Equal(TagQuality.Good, tagSpace.Get("mercury_dev.energy")!.Quality);
        Assert.Equal(holdingRegisterValue, Convert.ToUInt16(tagSpace.Get("modbus_dev.value")!.Value));
        Assert.Equal(mercuryEnergyMWh / 1000.0, Convert.ToDouble(tagSpace.Get("mercury_dev.energy")!.Value));
        Assert.False(responderTask.IsFaulted, responderTask.Exception?.ToString());

        await responderCts.CancelAsync();
        await modbusDriver.DisconnectAsync(modbusDevice, default);
        await mercuryDriver.DisconnectAsync(mercuryDevice, default);
        transport.Dispose();
    }

    [Fact]
    public void SerialTransportRegistry_Rejects_Mismatched_Baud_For_The_Same_Transport_Name()
    {
        using var registry = new SerialTransportRegistry();
        registry.GetOrCreate("bus_1", "/dev/ttyUSB0", 9600, Parity.Even, StopBits.One);

        var ex = Assert.Throws<FormatException>(() =>
            registry.GetOrCreate("bus_1", "/dev/ttyUSB0", 19200, Parity.Even, StopBits.One));
        Assert.Contains("mismatched", ex.Message);
    }

    [Fact]
    public void SerialTransportRegistry_Rejects_Mismatched_Parity_For_The_Same_Transport_Name()
    {
        using var registry = new SerialTransportRegistry();
        registry.GetOrCreate("bus_1", "/dev/ttyUSB0", 9600, Parity.Even, StopBits.One);

        Assert.Throws<FormatException>(() =>
            registry.GetOrCreate("bus_1", "/dev/ttyUSB0", 9600, Parity.None, StopBits.One));
    }

    [Fact]
    public void SerialTransportRegistry_Allows_Two_Devices_With_Matching_Settings_To_Share_A_Transport()
    {
        using var registry = new SerialTransportRegistry();
        var t1 = registry.GetOrCreate("bus_1", "/dev/ttyUSB0", 9600, Parity.Even, StopBits.One);
        var t2 = registry.GetOrCreate("bus_1", "/dev/ttyUSB0", 9600, Parity.Even, StopBits.One);
        Assert.Same(t1, t2);
    }

    [Fact]
    public void GatewayEngine_Config_Validation_Rejects_Mismatched_Serial_Transport_Settings_Across_Sources()
    {
        var yaml = """
            gateway:
              name: serial-transport-test
            sources:
              - name: modbus_dev
                driver: modbus_rtu_client
                serial_port: /dev/ttyUSB0
                serial_transport: bus_1
                baud_rate: 9600
                unit_id: 1
                tags:
                  - name: value
                    area: HR
                    address: 0
                    type: uint16
              - name: mercury_dev
                driver: mercury_client
                serial_port: /dev/ttyUSB0
                serial_transport: bus_1
                baud_rate: 19200
                unit_id: 2
                tags:
                  - name: energy
                    param: energy_active_total
            """;

        var paths = new SerialTransportTempPathProvider();
        var configPath = Path.Combine(paths.ConfigDir, "project.yaml");
        File.WriteAllText(configPath, yaml);

        var engine = new GatewayEngine(paths, configPath);
        var errors = engine.LoadAndValidate();

        Assert.Contains(errors, e => e.Contains("mismatched") && e.Contains("bus_1"));
    }

    [Fact]
    public void GatewayEngine_Config_Validation_Accepts_Matching_Serial_Transport_Settings_Across_Sources()
    {
        var yaml = """
            gateway:
              name: serial-transport-test
            sources:
              - name: modbus_dev
                driver: modbus_rtu_client
                serial_port: /dev/ttyUSB0
                serial_transport: bus_1
                baud_rate: 9600
                parity: even
                unit_id: 1
                tags:
                  - name: value
                    area: HR
                    address: 0
                    type: uint16
              - name: mercury_dev
                driver: mercury_client
                serial_port: /dev/ttyUSB0
                serial_transport: bus_1
                baud_rate: 9600
                parity: even
                unit_id: 2
                tags:
                  - name: energy
                    param: energy_active_total
            """;

        var paths = new SerialTransportTempPathProvider();
        var configPath = Path.Combine(paths.ConfigDir, "project.yaml");
        File.WriteAllText(configPath, yaml);

        var engine = new GatewayEngine(paths, configPath);
        var errors = engine.LoadAndValidate();

        Assert.Empty(errors);
    }
}
