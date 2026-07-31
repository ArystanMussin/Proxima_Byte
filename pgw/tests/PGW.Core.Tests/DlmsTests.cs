using System.Net;
using System.Net.Sockets;
using PGW.Core;
using PGW.Drivers.Dlms;
using Xunit;

namespace PGW.Core.Tests;

/// <summary>
/// A fake DLMS/COSEM meter: speaks the TCP wrapper + AARQ/AARE + GET.request/response-normal framing
/// per <see cref="DlmsCodec"/>'s understanding of the wire format. Proves the client's framing/decoding
/// is internally consistent with the codec — see README "DLMS/COSEM" for how the codec's byte layouts
/// themselves were verified (against the u9n/dlms-cosem open-source reference implementation's source).
/// </summary>
internal sealed class FakeDlmsMeter(Socket socket)
{
    public async Task RunAssociationAndTwoGetsAsync(byte[] doubleLongUnsignedResponse, byte[] float64Response, CancellationToken ct)
    {
        await ReadFrameAsync(ct); // AARQ — this test doesn't need to inspect it
        await WriteFrameAsync(BuildAare(), ct);

        await ReadFrameAsync(ct); // GET energy_total
        await WriteFrameAsync(BuildGetResponse(doubleLongUnsignedResponse), ct);

        await ReadFrameAsync(ct); // GET voltage_a
        await WriteFrameAsync(BuildGetResponse(float64Response), ct);
    }

    private static byte[] BuildAare() =>
        // AARE, tag 0x61, containing only [2] result = accepted(0) — the only field
        // DlmsCodec.ParseAareOrThrow actually reads.
        DlmsCodec.BerEncode(0x61, DlmsCodec.BerEncode(0xA2, DlmsCodec.BerEncode(0x02, [0x00])));

    private static byte[] BuildGetResponse(byte[] dataBytes)
    {
        var apdu = new byte[4 + dataBytes.Length];
        apdu[0] = 0xC4; // get-response tag
        apdu[1] = 0x01; // get-response-normal
        apdu[2] = 0xC1; // invoke-id-and-priority (unchecked by the client)
        apdu[3] = 0x00; // data-access-result choice: 0 = data
        dataBytes.CopyTo(apdu, 4);
        return apdu;
    }

    private async Task WriteFrameAsync(byte[] apdu, CancellationToken ct)
    {
        var header = DlmsCodec.BuildWrapperHeader(sourceWport: 1, destinationWport: 1, apdu.Length);
        await socket.SendAsync(header, SocketFlags.None, ct);
        await socket.SendAsync(apdu, SocketFlags.None, ct);
    }

    private async Task<byte[]> ReadFrameAsync(CancellationToken ct)
    {
        var header = await ReadExactAsync(8, ct);
        var body = await ReadExactAsync(DlmsCodec.ReadWrapperBodyLength(header), ct);
        return body;
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
}

public class DlmsTests
{
    [Fact]
    public void ParseObis_Accepts_Five_And_Six_Part_Forms()
    {
        Assert.Equal(new byte[] { 1, 0, 1, 8, 0, 255 }, DlmsCodec.ParseObis("1.0.1.8.0"));
        Assert.Equal(new byte[] { 1, 0, 1, 8, 0, 255 }, DlmsCodec.ParseObis("1.0.1.8.0.255"));
        Assert.Equal(new byte[] { 1, 0, 32, 7, 0, 255 }, DlmsCodec.ParseObis("1.0.32.7.0.255"));
        Assert.Throws<FormatException>(() => DlmsCodec.ParseObis("1.0.1.8"));
    }

    [Fact]
    public void WrapperHeader_Round_Trips_Body_Length()
    {
        var header = DlmsCodec.BuildWrapperHeader(1, 1, 42);
        Assert.Equal(42, DlmsCodec.ReadWrapperBodyLength(header));
    }

    [Fact]
    public void BuildAarq_Produces_A_Well_Formed_Application_0_APDU()
    {
        var aarq = DlmsCodec.BuildAarq(DlmsAuthentication.None, null, 1024);
        Assert.Equal(0x60, aarq[0]);
        Assert.Equal(aarq.Length - 2, aarq[1]); // outer BER length matches remaining bytes
    }

    [Fact]
    public void BuildAarq_With_Lls_Includes_Authentication_Fields()
    {
        var aarq = DlmsCodec.BuildAarq(DlmsAuthentication.Lls, "12345678"u8.ToArray(), 1024);
        // sender-acse-requirements (0x8A), mechanism-name (0x8B) and calling-authentication-value
        // (0xAC) should all be present when LLS is requested.
        Assert.Contains((byte)0x8A, aarq);
        Assert.Contains((byte)0x8B, aarq);
        Assert.Contains((byte)0xAC, aarq);
    }

    [Fact]
    public void ParseAareOrThrow_Accepts_Result_Zero_And_Rejects_Others()
    {
        var accepted = DlmsCodec.BerEncode(0x61, DlmsCodec.BerEncode(0xA2, DlmsCodec.BerEncode(0x02, [0x00])));
        DlmsCodec.ParseAareOrThrow(accepted); // should not throw

        var rejected = DlmsCodec.BerEncode(0x61, DlmsCodec.BerEncode(0xA2, DlmsCodec.BerEncode(0x02, [0x01])));
        var ex = Assert.Throws<DlmsAssociationException>(() => DlmsCodec.ParseAareOrThrow(rejected));
        Assert.Equal(DlmsAssociationResult.RejectedPermanent, ex.Result);
    }

    [Theory]
    [InlineData(new byte[] { 6, 0x00, 0x01, 0xE2, 0x40 }, 123456L)] // double-long-unsigned
    [InlineData(new byte[] { 17, 42 }, 42L)] // unsigned (8-bit)
    [InlineData(new byte[] { 18, 0x01, 0x00 }, 256L)] // long-unsigned (16-bit)
    public void DecodeData_Reads_Common_Integer_Types(byte[] wire, long expected)
    {
        var offset = 0;
        Assert.Equal(expected, DlmsCodec.DecodeData(wire, ref offset));
        Assert.Equal(wire.Length, offset);
    }

    [Fact]
    public void DecodeData_Reads_Float64()
    {
        var wire = new byte[9];
        wire[0] = 24;
        System.Buffers.Binary.BinaryPrimitives.WriteDoubleBigEndian(wire.AsSpan(1), 229.8);
        var offset = 0;
        Assert.Equal(229.8, (double)DlmsCodec.DecodeData(wire, ref offset)!, precision: 6);
    }

    [Fact]
    public async Task Driver_Associates_And_Reads_Two_Registers_Through_A_Fake_Meter()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var acceptTask = listener.AcceptSocketAsync();

        var meterTask = Task.Run(async () =>
        {
            var socket = await acceptTask;
            listener.Stop();
            var meter = new FakeDlmsMeter(socket);
            // energy_total = 123456 (double-long-unsigned), voltage_a = 229.8 (float64)
            var energyBytes = new byte[] { 6, 0x00, 0x01, 0xE2, 0x40 };
            var voltageBytes = new byte[9];
            voltageBytes[0] = 24;
            System.Buffers.Binary.BinaryPrimitives.WriteDoubleBigEndian(voltageBytes.AsSpan(1), 229.8);
            await meter.RunAssociationAndTwoGetsAsync(energyBytes, voltageBytes, CancellationToken.None);
        });

        var log = new RingLog();
        var cfg = new DlmsDeviceSettings(Host: "127.0.0.1", Port: port, ConnectTimeoutMs: 5000, ResponseTimeoutMs: 5000, ScanRateMs: 60000);
        var bindings = new List<DlmsTagBinding>
        {
            new("meter.energy_total", 3, "1.0.1.8.0.255", 2),
            new("meter.voltage_a", 3, "1.0.32.7.0.255", 2),
        };
        var driver = new DlmsClientDriver(cfg, bindings, log);

        var device = new DeviceHandle("meter", "meter");
        var tagSpace = new TagSpace();
        foreach (var b in bindings)
            tagSpace.Register(new TagDefinition(b.TagId, TagDataType.Float64, TagAccess.RO, device, b.Obis), driver, new TagAddress(device, b.Obis));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var connect = await driver.ConnectAsync(device, cts.Token);
        Assert.True(connect.Ok, connect.Error);

        var pollTask = driver.StartPollingAsync(device, tagSpace, cts.Token);

        await TestUtil.WaitUntilAsync(() => tagSpace.Get("meter.voltage_a")?.Quality == TagQuality.Good, TimeSpan.FromSeconds(5));

        Assert.Equal(123456.0, Convert.ToDouble(tagSpace.Get("meter.energy_total")!.Value), precision: 3);
        Assert.Equal(229.8, Convert.ToDouble(tagSpace.Get("meter.voltage_a")!.Value), precision: 3);

        cts.Cancel();
        await driver.DisconnectAsync(device, default);
        try { await meterTask; } catch (Exception) { /* client disconnect races the meter's own loop exit */ }
    }
}
