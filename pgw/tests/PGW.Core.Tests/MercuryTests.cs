using System.Net;
using System.Net.Sockets;
using PGW.Core;
using PGW.Drivers.Mercury;
using Xunit;

namespace PGW.Core.Tests;

/// <summary>Loopback TCP socket implementing <see cref="IMercuryTransport"/> — stands in for a real
/// RS-485 serial port, same trick as <c>ModbusRtuTests</c>' LoopbackSerialPort.</summary>
internal sealed class LoopbackMercuryTransport(Socket socket) : IMercuryTransport
{
    public bool IsOpen { get; private set; } = true;
    public Task OpenAsync(CancellationToken ct) { IsOpen = true; return Task.CompletedTask; }
    public void Close() { IsOpen = false; try { socket.Close(); } catch (Exception) { } }
    public Task WriteAsync(byte[] data, CancellationToken ct) => socket.SendAsync(data, SocketFlags.None, ct).AsTask();
    public async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        try { return await socket.ReceiveAsync(new ArraySegment<byte>(buffer, offset, count), SocketFlags.None, ct); }
        catch (OperationCanceledException) { return 0; }
        catch (SocketException) { return 0; }
    }
}

/// <summary>
/// A fake Mercury 230 meter: reads request frames per <see cref="MercuryCodec"/>'s understanding of the
/// wire format and answers with responses built the same way — this proves the client's request-building
/// and response-parsing are internally consistent (a real bug here would show up as a CRC failure or a
/// garbled decoded value), which is the strongest verification possible without real RS-485 hardware. It
/// does NOT prove the protocol understanding itself matches a genuine meter — see README "Меркурий".
/// </summary>
internal sealed class FakeMercuryMeter(Socket socket, byte address, uint activeEnergyMWh, double[] voltages)
{
    public async Task RunOnceEachAsync(CancellationToken ct)
    {
        await HandleAsync(ct); // CONNECT
        await HandleAsync(ct); // LIST (energy)
        await HandleAsync(ct); // READ_PARAMS (voltage)
        await HandleAsync(ct); // CLOSE
    }

    private async Task HandleAsync(CancellationToken ct)
    {
        var header = await ReadExactAsync(2, ct); // addr, command
        var command = header[1];
        var extra = command switch
        {
            0x01 => 7, // access level (1) + password (6), then CRC(2)
            0x02 => 0, // CLOSE: just CRC(2)
            0x05 => 2, // period + tariff, then CRC(2)
            0x08 => 2, // PARAM_ALL + param, then CRC(2)
            _ => throw new InvalidOperationException($"fake meter: unknown command 0x{command:X2}"),
        };
        var rest = await ReadExactAsync(extra + 2, ct);
        var request = header.Concat(rest).ToArray();
        Assert.True(MercuryCodec.VerifyCrc(request), "fake meter received a request with a bad CRC");

        byte[] response = command switch
        {
            0x01 => MercuryCodec.AppendCrc(new byte[] { address }),
            0x02 => MercuryCodec.AppendCrc(new byte[] { address }),
            0x05 => BuildEnergyResponse(),
            0x08 => BuildParamsResponse(rest[1]),
            _ => throw new InvalidOperationException("unreachable"),
        };
        await socket.SendAsync(response, SocketFlags.None, ct);
    }

    private byte[] BuildEnergyResponse()
    {
        var data = new byte[16];
        EncodeU32(activeEnergyMWh).CopyTo(data, 0);
        var frame = new byte[1 + 16];
        frame[0] = address;
        data.CopyTo(frame, 1);
        return MercuryCodec.AppendCrc(frame);
    }

    private byte[] BuildParamsResponse(byte param)
    {
        // Only voltage (0x11) is exercised by these tests.
        Assert.Equal(0x11, param);
        var frame = new byte[1 + voltages.Length * 3];
        frame[0] = address;
        for (var i = 0; i < voltages.Length; i++)
            EncodeU24((uint)(voltages[i] * 100)).CopyTo(frame, 1 + i * 3);
        return MercuryCodec.AppendCrc(frame);
    }

    private async Task<byte[]> ReadExactAsync(int count, CancellationToken ct)
    {
        var buf = new byte[count];
        var received = 0;
        while (received < count)
        {
            var n = await socket.ReceiveAsync(new ArraySegment<byte>(buf, received, count - received), SocketFlags.None, ct);
            if (n == 0) throw new IOException("fake meter: connection closed mid-frame");
            received += n;
        }
        return buf;
    }

    // Inverse of MercuryCodec.DecodeU32/DecodeU24 — test-only, production never needs to *build* a
    // meter's response, only parse one.
    private static byte[] EncodeU32(uint value) =>
        new[] { (byte)(value >> 16), (byte)(value >> 24), (byte)value, (byte)(value >> 8) };

    private static byte[] EncodeU24(uint value) =>
        new[] { (byte)(value >> 16), (byte)value, (byte)(value >> 8) };
}

public class MercuryTests
{
    [Fact]
    public void Crc16_Matches_A_Real_ModbusRtuClients_Wire_Bytes()
    {
        // Ground-truthed against a real, working FluentModbus.ModbusRtuClient rather than a
        // from-memory example: it puts request 01 03 00 00 00 0A on the wire followed by CRC bytes
        // C5 CD (transmitted low-byte-first) — confirmed by sniffing the actual bytes sent over a
        // loopback transport in a throwaway harness during development.
        var crc = MercuryCodec.Crc16(new byte[] { 0x01, 0x03, 0x00, 0x00, 0x00, 0x0A });
        Assert.Equal((byte)0xC5, (byte)(crc & 0xFF));
        Assert.Equal((byte)0xCD, (byte)(crc >> 8));
    }

    [Fact]
    public void AppendCrc_And_VerifyCrc_Round_Trip()
    {
        var frame = MercuryCodec.AppendCrc(new byte[] { 0x01, 0x08, 0x16, 0x11 });
        Assert.True(MercuryCodec.VerifyCrc(frame));
        frame[0] ^= 0xFF;
        Assert.False(MercuryCodec.VerifyCrc(frame));
    }

    [Theory]
    [InlineData(new byte[] { 0x34, 0x12, 0x78, 0x56 }, 0x12345678u)]
    [InlineData(new byte[] { 0x00, 0x00, 0x00, 0x00 }, 0u)]
    public void DecodeU32_Matches_The_Real_Firmwares_Word_Swapped_Layout(byte[] raw, uint expected) =>
        Assert.Equal(expected, MercuryCodec.DecodeU32(raw));

    [Fact]
    public async Task Driver_Connects_Reads_Energy_And_Voltage_Through_A_Fake_Meter()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var acceptTask = listener.AcceptSocketAsync();
        var clientSocket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await clientSocket.ConnectAsync(IPAddress.Loopback, port);
        var meterSocket = await acceptTask;
        listener.Stop();

        const byte address = 5;
        var meter = new FakeMercuryMeter(meterSocket, address, activeEnergyMWh: 123456, voltages: new[] { 229.8, 230.1, 228.9 });
        var meterTask = meter.RunOnceEachAsync(default);

        var log = new RingLog();
        var cfg = new MercuryDeviceSettings(Address: address, AccessLevel: 1, ResponseTimeoutMs: 2000, InterByteGapMs: 100);
        var bindings = new List<MercuryTagBinding>
        {
            new("meter.energy_total", MercuryParam.EnergyActiveTotal),
            new("meter.voltage_a", MercuryParam.VoltageA),
            new("meter.voltage_b", MercuryParam.VoltageB),
            new("meter.voltage_c", MercuryParam.VoltageC),
        };
        var transport = new LoopbackMercuryTransport(clientSocket);
        var driver = new MercuryClientDriver(transport, cfg, bindings, log);

        var device = new DeviceHandle("meter", "meter");
        var tagSpace = new TagSpace();
        foreach (var b in bindings)
            tagSpace.Register(new TagDefinition(b.TagId, TagDataType.Float64, TagAccess.RO, device, b.Param.ToString()), driver, new TagAddress(device, b.Param.ToString()));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var connect = await driver.ConnectAsync(device, cts.Token);
        Assert.True(connect.Ok, connect.Error);

        var pollTask = driver.StartPollingAsync(device, tagSpace, cts.Token);

        await TestUtil.WaitUntilAsync(() => tagSpace.Get("meter.voltage_c")?.Quality == TagQuality.Good, TimeSpan.FromSeconds(5));

        Assert.Equal(123.456, Convert.ToDouble(tagSpace.Get("meter.energy_total")!.Value), precision: 3);
        Assert.Equal(229.8, Convert.ToDouble(tagSpace.Get("meter.voltage_a")!.Value), precision: 2);
        Assert.Equal(230.1, Convert.ToDouble(tagSpace.Get("meter.voltage_b")!.Value), precision: 2);
        Assert.Equal(228.9, Convert.ToDouble(tagSpace.Get("meter.voltage_c")!.Value), precision: 2);

        cts.Cancel();
        await driver.DisconnectAsync(device, default);
        try { await meterTask; } catch (Exception) { /* client disconnect races the meter's own close */ }
    }
}
