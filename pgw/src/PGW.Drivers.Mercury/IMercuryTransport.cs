using System.IO.Ports;
using PGW.Core;

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

    /// <summary>§4.1.1/§6.5: acquires exclusive use of the physical line for one whole request/response
    /// transaction. A no-op for a dedicated port (nothing else can be on the wire), but on a
    /// <see cref="SerialTransport"/> shared with other devices this serializes against their transactions
    /// too, so two devices' frames can never interleave. Dispose the result to release.</summary>
    Task<IDisposable> AcquireAsync(CancellationToken ct);
}

/// <summary>Production transport: a real, dedicated RS-485 serial port.</summary>
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

    // Nothing else can be using a dedicated port, so there's nothing to serialize against.
    public Task<IDisposable> AcquireAsync(CancellationToken ct) => Task.FromResult<IDisposable>(NoopLock.Instance);

    private sealed class NoopLock : IDisposable
    {
        public static readonly NoopLock Instance = new();
        public void Dispose() { }
    }
}

/// <summary>§4.1.1: routes a Mercury device's byte-level I/O through a <see cref="SerialTransport"/> shared
/// with other devices — Modbus RTU and/or other Mercury meters — on the same physical RS-485 bus, instead
/// of owning a dedicated <see cref="SerialPort"/>.</summary>
public sealed class SharedSerialMercuryTransport(SerialTransport transport) : IMercuryTransport
{
    public bool IsOpen => transport.IsOpen;

    public Task OpenAsync(CancellationToken ct)
    {
        transport.EnsureOpen();
        return Task.CompletedTask;
    }

    // Never close a bus shared with other devices just because this one disconnected/reconnected.
    public void Close() { }

    public Task WriteAsync(byte[] data, CancellationToken ct) => transport.BaseStream.WriteAsync(data, ct).AsTask();

    public async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        try { return await transport.BaseStream.ReadAsync(buffer.AsMemory(offset, count), ct); }
        catch (OperationCanceledException) { return 0; }
    }

    // Mercury is read-only (no WriteAsync support in MercuryClientDriver), so it only ever needs read
    // priority on the shared bus — Modbus RTU writes on the same bus still jump ahead of it.
    public async Task<IDisposable> AcquireAsync(CancellationToken ct) => await transport.AcquireAsync(SerialBusPriority.Read, ct);
}
