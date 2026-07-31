using System.IO.Ports;

namespace PGW.Drivers.Mercury;

/// <summary>Byte-level transport a <see cref="MercuryProtocol"/> talks over — swappable so tests can
/// substitute a loopback socket for the real serial port (there's no physical RS-485 hardware available
/// in CI/this sandbox).</summary>
public interface IMercuryTransport
{
    bool IsOpen { get; }
    Task OpenAsync(CancellationToken ct);
    void Close();
    Task WriteAsync(byte[] data, CancellationToken ct);
    /// <summary>Reads whatever is available up to <paramref name="count"/> bytes; returns 0 if the read
    /// is cancelled/times out rather than throwing, so the frame reader can tell "nothing arrived in this
    /// window" from a real transport failure.</summary>
    Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct);
}

/// <summary>Production transport: a real RS-485 serial port.</summary>
public sealed class SerialMercuryTransport(string portName, int baudRate, Parity parity, StopBits stopBits) : IMercuryTransport
{
    private SerialPort? _port;

    public bool IsOpen => _port?.IsOpen ?? false;

    public Task OpenAsync(CancellationToken ct)
    {
        var port = new SerialPort(portName, baudRate, parity, 8, stopBits);
        port.Open();
        _port = port;
        return Task.CompletedTask;
    }

    public void Close()
    {
        try { _port?.Close(); } catch (Exception) { /* best-effort */ }
        _port?.Dispose();
        _port = null;
    }

    public Task WriteAsync(byte[] data, CancellationToken ct) => _port!.BaseStream.WriteAsync(data, ct).AsTask();

    public async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        try { return await _port!.BaseStream.ReadAsync(buffer.AsMemory(offset, count), ct); }
        catch (OperationCanceledException) { return 0; }
    }
}
