using System.Net.Sockets;

namespace PGW.Drivers.Iec104;

/// <summary>
/// IEC 60870-5-104 master-side connection: APCI framing (I/S/U format), STARTDT handshake, sequence
/// number bookkeeping, and a background receive loop that decodes incoming ASDUs. Deliberately doesn't
/// implement full send-window/retransmission recovery (§5 of the standard's link-layer procedures) — this
/// driver only ever sends small control ASDUs (general interrogation, clock sync), never a sustained
/// stream that would need flow control, so the simplification is safe for a client role.
/// </summary>
public sealed class Iec104Client : IAsyncDisposable
{
    public event Action<Iec104Asdu>? AsduReceived;
    public bool Connected { get; private set; }

    private readonly TcpClient _tcp = new();
    private NetworkStream? _stream;
    private int _sendSeq;
    private int _recvSeq;
    private int _unackedReceived;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private TaskCompletionSource? _startDtConfirmed;
    private CancellationTokenSource? _receiveLoopCts;
    private Task? _receiveLoopTask;

    public async Task ConnectAsync(string host, int port, int connectTimeoutMs, CancellationToken ct)
    {
        using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectCts.CancelAfter(connectTimeoutMs);
            await _tcp.ConnectAsync(host, port, connectCts.Token);
        }
        _stream = _tcp.GetStream();
        _sendSeq = 0; _recvSeq = 0; _unackedReceived = 0;

        _startDtConfirmed = new TaskCompletionSource();
        _receiveLoopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _receiveLoopTask = Task.Run(() => ReceiveLoopAsync(_receiveLoopCts.Token));

        await WriteRawAsync(Iec104Codec.StartDtAct, ct);

        using var startDtCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        startDtCts.CancelAfter(connectTimeoutMs);
        await using (startDtCts.Token.Register(() => _startDtConfirmed.TrySetCanceled()))
        {
            try { await _startDtConfirmed.Task; }
            catch (TaskCanceledException) { throw new TimeoutException("IEC 104: no STARTDT confirmation within timeout"); }
        }

        Connected = true;
    }

    public Task SendGeneralInterrogationAsync(int commonAddress, byte originatorAddress, CancellationToken ct) =>
        SendIFrameAsync(Iec104Codec.BuildGeneralInterrogation(commonAddress, originatorAddress), ct);

    public Task SendTestFrAsync(CancellationToken ct) => WriteRawAsync(Iec104Codec.TestFrAct, ct);

    public async ValueTask DisposeAsync()
    {
        Connected = false;
        try { if (_stream is not null) await WriteRawAsync(Iec104Codec.StopDtAct, CancellationToken.None); }
        catch (Exception) { /* best-effort */ }

        if (_receiveLoopCts is not null) await _receiveLoopCts.CancelAsync();
        if (_receiveLoopTask is not null)
        {
            try { await _receiveLoopTask; } catch (Exception) { /* loop observed its own cancellation */ }
        }

        _stream?.Dispose();
        _tcp.Dispose();
    }

    private async Task SendIFrameAsync(byte[] asdu, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var frame = Iec104Codec.BuildIFrame(_sendSeq, _recvSeq, asdu);
            _sendSeq = (_sendSeq + 1) & 0x7FFF;
            _unackedReceived = 0; // this frame piggybacks our N(R), so nothing more owed right now
            await _stream!.WriteAsync(frame, ct);
        }
        finally { _writeLock.Release(); }
    }

    private async Task SendSFrameAsync(CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try { await _stream!.WriteAsync(Iec104Codec.BuildSFrame(_recvSeq), ct); }
        finally { _writeLock.Release(); }
    }

    private async Task WriteRawAsync(byte[] frame, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try { await _stream!.WriteAsync(frame, ct); }
        finally { _writeLock.Release(); }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = await ReadFrameAsync(ct);
                if (frame is null) break; // peer closed the connection
                await ProcessFrameAsync(frame, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* surfaced to callers via Connected flipping false / the next I/O failing */ }
        finally { Connected = false; }
    }

    private async Task<byte[]?> ReadFrameAsync(CancellationToken ct)
    {
        var header = new byte[2];
        if (!await ReadExactAsync(header, ct)) return null;
        if (header[0] != Iec104Codec.Start) throw new IOException($"IEC 104: bad start byte 0x{header[0]:X2}");
        var len = header[1];
        var rest = new byte[len];
        if (!await ReadExactAsync(rest, ct)) return null;
        var full = new byte[2 + len];
        header.CopyTo(full, 0);
        rest.CopyTo(full, 2);
        return full;
    }

    private async Task<bool> ReadExactAsync(byte[] buf, CancellationToken ct)
    {
        var received = 0;
        while (received < buf.Length)
        {
            var n = await _stream!.ReadAsync(buf.AsMemory(received, buf.Length - received), ct);
            if (n == 0) return false;
            received += n;
        }
        return true;
    }

    private async Task ProcessFrameAsync(byte[] frame, CancellationToken ct)
    {
        // Span<byte> locals can't live across an `await` inside an async method (CS4012), so control
        // bytes are read via plain array indexing here instead of frame.AsSpan(2, 4).
        var c0 = frame[2];
        var c1 = frame[3];
        switch (Iec104Codec.ClassifyControl(c0))
        {
            case Iec104Codec.FrameKind.I:
                _recvSeq = (Iec104Codec.DecodeSeq(c0, c1) + 1) & 0x7FFF;
                _unackedReceived++;
                try { AsduReceived?.Invoke(DecodeAsduBody(frame)); }
                catch (NotSupportedException) { /* an unsupported/uninteresting ASDU type — skip, keep the connection alive */ }
                if (_unackedReceived >= 8) { await SendSFrameAsync(ct); _unackedReceived = 0; }
                break;

            case Iec104Codec.FrameKind.S:
                break; // no send-window to reconcile against — see class remarks

            case Iec104Codec.FrameKind.U:
                if (c0 == 0x0B) _startDtConfirmed?.TrySetResult();       // STARTDT_CON
                else if (c0 == 0x43) await WriteRawAsync(Iec104Codec.TestFrCon, ct); // peer's TESTFR_ACT -> reply CON
                break;
        }
    }

    // Isolated as its own (synchronous) method because a Span<byte> local can't be held across an
    // `await` inside an async method (CS4012) — this keeps the span entirely off the async state machine.
    private static Iec104Asdu DecodeAsduBody(byte[] frame) => Iec104Codec.DecodeAsdu(frame.AsSpan(6));
}
