using System.Net.Sockets;

namespace PGW.Drivers.Dlms;

/// <summary>
/// DLMS/COSEM client over the TCP wrapper transport (IEC 62056-47): connect, associate (AARQ/AARE,
/// optionally with Low Level Security), then issue GET.request-normal/GET.response-normal round trips.
/// One request in flight at a time — this client doesn't pipeline invoke IDs, which is fine for a
/// polling reader that reads one attribute at a time. See README "DLMS/COSEM" for scope/confidence.
/// </summary>
public sealed class DlmsClient : IAsyncDisposable
{
    public bool Connected { get; private set; }

    private readonly TcpClient _tcp = new();
    private NetworkStream? _stream;
    private ushort _clientWport;
    private ushort _serverWport;

    public async Task ConnectAsync(string host, int port, ushort clientAddress, ushort logicalDeviceAddress,
        DlmsAuthentication auth, byte[]? password, ushort maxPduSize, int connectTimeoutMs, int responseTimeoutMs, CancellationToken ct)
    {
        _clientWport = clientAddress;
        _serverWport = logicalDeviceAddress;

        using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectCts.CancelAfter(connectTimeoutMs);
            await _tcp.ConnectAsync(host, port, connectCts.Token);
        }
        _stream = _tcp.GetStream();

        var aarq = DlmsCodec.BuildAarq(auth, password, maxPduSize);
        var aare = await SendAndReceiveAsync(aarq, responseTimeoutMs, ct);
        DlmsCodec.ParseAareOrThrow(aare);

        Connected = true;
    }

    public async Task<object?> GetAsync(ushort classId, string obis, byte attributeId, int responseTimeoutMs, CancellationToken ct)
    {
        var request = DlmsCodec.BuildGetRequestNormal(classId, DlmsCodec.ParseObis(obis), attributeId);
        var response = await SendAndReceiveAsync(request, responseTimeoutMs, ct);
        return DlmsCodec.ParseGetResponseNormal(response);
    }

    private async Task<byte[]> SendAndReceiveAsync(byte[] apdu, int timeoutMs, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);
        var t = timeoutCts.Token;

        var header = DlmsCodec.BuildWrapperHeader(_clientWport, _serverWport, apdu.Length);
        await _stream!.WriteAsync(header, t);
        await _stream.WriteAsync(apdu, t);

        var respHeader = new byte[8];
        await ReadExactAsync(respHeader, t);
        var bodyLength = DlmsCodec.ReadWrapperBodyLength(respHeader);
        var body = new byte[bodyLength];
        await ReadExactAsync(body, t);
        return body;
    }

    private async Task ReadExactAsync(byte[] buf, CancellationToken ct)
    {
        var received = 0;
        while (received < buf.Length)
        {
            var n = await _stream!.ReadAsync(buf.AsMemory(received, buf.Length - received), ct);
            if (n == 0) throw new IOException("DLMS: connection closed mid-frame");
            received += n;
        }
    }

    public ValueTask DisposeAsync()
    {
        Connected = false;
        // No RLRQ/RLRE release procedure — for a read-only polling client, closing the TCP
        // connection is what every meter actually needs to see to free the association slot.
        _stream?.Dispose();
        _tcp.Dispose();
        return ValueTask.CompletedTask;
    }
}
