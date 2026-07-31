using System.Net;
using System.Net.Sockets;
using PGW.Core;
using PGW.Drivers.Iec104;
using Xunit;

namespace PGW.Core.Tests;

/// <summary>
/// A fake IEC 60870-5-104 substation: speaks the APCI/ASDU framing per <see cref="Iec104Codec"/>'s
/// understanding of the wire format (STARTDT handshake, TESTFR keepalive, replies to General
/// Interrogation with data ASDUs). This proves the client's framing/decoding is internally consistent
/// with the codec — the strongest verification possible without a real substation/RTU. See README
/// "IEC 60870-5-104" for how the codec constants themselves were verified (against lib60870).
/// </summary>
internal sealed class FakeIec104Substation(Socket socket)
{
    private int _sendSeq;
    private int _recvSeq;

    public async Task RunAsync(int commonAddress, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var frame = await ReadFrameAsync(ct);
            if (frame is null) return;

            var c0 = frame[2];
            switch (Iec104Codec.ClassifyControl(c0))
            {
                case Iec104Codec.FrameKind.U:
                    if (c0 == 0x07) await SendAsync(Iec104Codec.StartDtCon, ct);       // STARTDT_ACT -> CON
                    else if (c0 == 0x43) await SendAsync(Iec104Codec.TestFrCon, ct);   // TESTFR_ACT -> CON
                    break;

                case Iec104Codec.FrameKind.I:
                    _recvSeq = (Iec104Codec.DecodeSeq(c0, frame[3]) + 1) & 0x7FFF;
                    // C_IC_NA_1 (general interrogation) isn't a monitoring type Iec104Codec.DecodeAsdu
                    // knows how to decode an information element for (it's the driver's outbound
                    // command, never an inbound value) — read the type ID byte directly instead of
                    // calling the full decoder, which would throw NotSupportedException.
                    if ((Iec104TypeId)frame[6] == Iec104TypeId.C_IC_NA_1)
                        await SendDataAsync(commonAddress, ct);
                    break;

                case Iec104Codec.FrameKind.S:
                    break;
            }
        }
    }

    private async Task SendDataAsync(int commonAddress, CancellationToken ct)
    {
        // M_SP_NA_1 (single point, IOA 100, value=on/1, good quality)
        await SendAsduAsync(BuildSpAsdu(commonAddress, ioa: 100, value: true), ct);
        // M_ME_NC_1 (measured value short float, IOA 200, value=42.5, good quality)
        await SendAsduAsync(BuildFloatAsdu(commonAddress, ioa: 200, value: 42.5f), ct);
    }

    private async Task SendAsduAsync(byte[] asdu, CancellationToken ct)
    {
        var frame = Iec104Codec.BuildIFrame(_sendSeq, _recvSeq, asdu);
        _sendSeq = (_sendSeq + 1) & 0x7FFF;
        await SendAsync(frame, ct);
    }

    private static byte[] BuildSpAsdu(int commonAddress, int ioa, bool value)
    {
        var asdu = new byte[6 + 3 + 1];
        WriteHeader(asdu, Iec104TypeId.M_SP_NA_1, commonAddress, ioa);
        asdu[9] = (byte)(value ? 0x01 : 0x00); // SIQ: bit0=value, quality bits=0 (good)
        return asdu;
    }

    private static byte[] BuildFloatAsdu(int commonAddress, int ioa, float value)
    {
        var asdu = new byte[6 + 3 + 5];
        WriteHeader(asdu, Iec104TypeId.M_ME_NC_1, commonAddress, ioa);
        BitConverter.GetBytes(value).CopyTo(asdu, 9); // little-endian on all supported targets
        asdu[13] = 0x00; // quality: good
        return asdu;
    }

    private static void WriteHeader(byte[] asdu, Iec104TypeId typeId, int commonAddress, int ioa)
    {
        asdu[0] = (byte)typeId;
        asdu[1] = 0x01; // VSQ: SQ=0, 1 object
        asdu[2] = (byte)Iec104Cot.InterrogatedByStation;
        asdu[3] = 0; // originator
        asdu[4] = (byte)commonAddress;
        asdu[5] = (byte)(commonAddress >> 8);
        asdu[6] = (byte)ioa;
        asdu[7] = (byte)(ioa >> 8);
        asdu[8] = (byte)(ioa >> 16);
    }

    private Task SendAsync(byte[] frame, CancellationToken ct) => socket.SendAsync(frame, SocketFlags.None, ct).AsTask();

    private async Task<byte[]?> ReadFrameAsync(CancellationToken ct)
    {
        var header = await ReadExactAsync(2, ct);
        if (header is null) return null;
        if (header[0] != Iec104Codec.Start) throw new IOException("fake substation: bad start byte");
        var rest = await ReadExactAsync(header[1], ct);
        if (rest is null) return null;
        var full = new byte[2 + header[1]];
        header.CopyTo(full, 0);
        rest.CopyTo(full, 2);
        return full;
    }

    private async Task<byte[]?> ReadExactAsync(int count, CancellationToken ct)
    {
        var buf = new byte[count];
        var received = 0;
        while (received < count)
        {
            int n;
            try { n = await socket.ReceiveAsync(new ArraySegment<byte>(buf, received, count - received), SocketFlags.None, ct); }
            catch (SocketException) { return null; }
            catch (OperationCanceledException) { return null; }
            if (n == 0) return null;
            received += n;
        }
        return buf;
    }
}

public class Iec104Tests
{
    [Fact]
    public void DecodeCp56Time2a_Matches_A_Known_Timestamp()
    {
        // 2024-03-15 14:27:36.500 UTC: ms=36500 (0x8E94, LE), minute=27, hour=14, day=15, month=3, year=24.
        var bytes = new byte[] { 0x94, 0x8E, 0x1B, 0x0E, 0x0F, 0x03, 0x18 };
        var dt = Iec104Codec.DecodeCp56Time2a(bytes);
        Assert.Equal(new DateTime(2024, 3, 15, 14, 27, 36, 500, DateTimeKind.Utc), dt);
    }

    [Fact]
    public void ClassifyControl_Distinguishes_I_S_U_Frames()
    {
        Assert.Equal(Iec104Codec.FrameKind.I, Iec104Codec.ClassifyControl(0x00));
        Assert.Equal(Iec104Codec.FrameKind.I, Iec104Codec.ClassifyControl(0x02));
        Assert.Equal(Iec104Codec.FrameKind.S, Iec104Codec.ClassifyControl(0x01));
        Assert.Equal(Iec104Codec.FrameKind.U, Iec104Codec.ClassifyControl(0x07));
        Assert.Equal(Iec104Codec.FrameKind.U, Iec104Codec.ClassifyControl(0x43));
    }

    [Fact]
    public void DecodeAsdu_Reads_A_Single_Point_And_A_Short_Float()
    {
        var spAsdu = new byte[] { (byte)Iec104TypeId.M_SP_NA_1, 0x01, (byte)Iec104Cot.Spontaneous, 0, 1, 0, 100, 0, 0, 0x01 };
        var decoded = Iec104Codec.DecodeAsdu(spAsdu);
        Assert.Equal(Iec104TypeId.M_SP_NA_1, decoded.TypeId);
        Assert.Equal(Iec104Cot.Spontaneous, decoded.Cot);
        Assert.Equal(1, decoded.CommonAddress);
        var point = Assert.Single(decoded.Points);
        Assert.Equal(100, point.Ioa);
        Assert.Equal(true, point.Value);
        Assert.Equal(Iec104Quality.Good, point.Quality);
    }

    [Fact]
    public async Task Driver_Connects_And_Publishes_Points_Through_A_Fake_Substation()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var acceptTask = listener.AcceptSocketAsync();
        // The driver owns its own TcpClient internally and connects to the listener above;
        // this task only drives the substation side of the conversation.
        var connectTask = Task.Run(async () =>
        {
            var socket = await acceptTask;
            listener.Stop();
            var substation = new FakeIec104Substation(socket);
            await substation.RunAsync(commonAddress: 1, CancellationToken.None);
        });

        var log = new RingLog();
        var cfg = new Iec104DeviceSettings(Host: "127.0.0.1", Port: port, CommonAddress: 1, ConnectTimeoutMs: 5000, InterrogationIntervalMs: 60000);
        var tagIdByIoa = new Dictionary<int, string> { [100] = "rtu.status", [200] = "rtu.value" };
        var driver = new Iec104ClientDriver(cfg, tagIdByIoa, log);

        var device = new DeviceHandle("rtu", "rtu");
        var tagSpace = new TagSpace();
        tagSpace.Register(new TagDefinition("rtu.status", TagDataType.Bool, TagAccess.RO, device, "IOA:100"), driver, new TagAddress(device, "IOA:100"));
        tagSpace.Register(new TagDefinition("rtu.value", TagDataType.Float64, TagAccess.RO, device, "IOA:200"), driver, new TagAddress(device, "IOA:200"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var connect = await driver.ConnectAsync(device, cts.Token);
        Assert.True(connect.Ok, connect.Error);

        var pollTask = driver.StartPollingAsync(device, tagSpace, cts.Token);

        await TestUtil.WaitUntilAsync(() => tagSpace.Get("rtu.value")?.Quality == TagQuality.Good, TimeSpan.FromSeconds(5));

        Assert.Equal(true, tagSpace.Get("rtu.status")!.Value);
        Assert.Equal(42.5, Convert.ToDouble(tagSpace.Get("rtu.value")!.Value), precision: 3);

        cts.Cancel();
        await driver.DisconnectAsync(device, default);
        try { await connectTask; } catch (Exception) { /* client disconnect races the substation's own loop exit */ }
    }
}
