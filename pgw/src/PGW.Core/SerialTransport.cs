using System.IO.Ports;

namespace PGW.Core;

/// <summary>Priority lane for <see cref="SerialTransport.AcquireAsync"/> (§6.5): writes must be able to
/// jump ahead of any already-queued reads, since a stuck write behind a backlog of polling reads is worse
/// than a slightly stale read. Writes do not jump ahead of other already-queued writes (FIFO among
/// writers) — nothing in the ТЗ asks for priority among writers themselves.</summary>
public enum SerialBusPriority { Write, Read }

/// <summary>
/// §4.1.1: one physical serial port (COM/tty + baud/parity/stop bits — the thing a real
/// <see cref="SerialPort"/> opens), independent of any single <c>Device</c>. Several devices — potentially
/// speaking different protocols, e.g. Modbus RTU and Меркурий on the same RS-485 bus — reference the same
/// named transport via config's <c>serial_transport: bus_1</c> and share this one instance. The physical
/// port is opened exactly once per transport, and every request/response transaction across every device
/// on the bus is serialized through <see cref="AcquireAsync"/> so two devices can never write over each
/// other's frames.
/// </summary>
public sealed class SerialTransport : IDisposable
{
    public string Name { get; }
    public string PortName { get; }
    public int BaudRate { get; }
    public Parity Parity { get; }
    public StopBits StopBits { get; }
    public Handshake Handshake { get; }

    private SerialPort? _port;
    private Stream? _stream;
    private bool _open;
    private readonly object _openLock = new();

    private readonly object _queueLock = new();
    private readonly Queue<TaskCompletionSource> _writeWaiters = new();
    private readonly Queue<TaskCompletionSource> _readWaiters = new();
    private bool _held;

    public SerialTransport(string name, string portName, int baudRate, Parity parity, StopBits stopBits, Handshake handshake = Handshake.None)
    {
        Name = name;
        PortName = portName;
        BaudRate = baudRate;
        Parity = parity;
        StopBits = stopBits;
        Handshake = handshake;
    }

    public bool IsOpen => _open;

    /// <summary>The raw byte stream of the physical port. Only valid to touch while holding a lock from
    /// <see cref="AcquireAsync"/> — callers (the per-device driver code) are responsible for that, the
    /// same way they already serialize their own single-device I/O.</summary>
    public Stream BaseStream => _stream ?? throw new InvalidOperationException($"serial_transport '{Name}' ({PortName}) is not open");

    /// <summary>Opens the physical port if it isn't already — safe to call once per device that shares
    /// this transport; only the first caller actually opens anything.</summary>
    public void EnsureOpen()
    {
        if (_open) return;
        lock (_openLock)
        {
            if (_open) return;
            var port = new SerialPort(PortName, BaudRate, Parity, 8, StopBits) { Handshake = Handshake };
            port.Open();
            _port = port;
            _stream = port.BaseStream;
            _open = true;
        }
    }

    /// <summary>§13 test 15 seam: virtualizes the physical line as an arbitrary stream (e.g. a loopback
    /// socket standing in for two "device" emulators sharing one wire) instead of opening a real
    /// <see cref="SerialPort"/> — there's no RS-485 hardware in CI/this sandbox. Internal + only reachable
    /// via <c>InternalsVisibleTo</c> to PGW.Core.Tests; production code always goes through
    /// <see cref="EnsureOpen"/>.</summary>
    internal void AttachVirtualStreamForTesting(Stream stream)
    {
        lock (_openLock)
        {
            _stream = stream;
            _open = true;
        }
    }

    public void Close()
    {
        lock (_openLock)
        {
            try { _port?.Close(); } catch (Exception) { /* best-effort */ }
            _port?.Dispose();
            _port = null;
            _stream = null;
            _open = false;
        }
    }

    /// <summary>
    /// Acquires exclusive use of the physical line for one whole request/response transaction — hold it
    /// across the entire write-then-read exchange, not just a single byte read or write, or two devices'
    /// frames can interleave on the half-duplex wire. Dispose the returned handle to release.
    /// </summary>
    public async Task<IDisposable> AcquireAsync(SerialBusPriority priority, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_queueLock)
        {
            if (!_held)
            {
                _held = true;
                tcs.TrySetResult();
            }
            else if (priority == SerialBusPriority.Write)
                _writeWaiters.Enqueue(tcs);
            else
                _readWaiters.Enqueue(tcs);
        }

        using (ct.Register(() => tcs.TrySetCanceled(ct)))
        {
            await tcs.Task.ConfigureAwait(false);
        }
        return new Releaser(this);
    }

    private void Release()
    {
        lock (_queueLock)
        {
            // Keep handing ownership to the next queued waiter until one actually accepts it — a waiter
            // that was cancelled while queued still sits in the queue and must be skipped, not left to
            // strand the lock in a permanently-held state.
            while (true)
            {
                if (_writeWaiters.Count > 0)
                {
                    if (_writeWaiters.Dequeue().TrySetResult()) return;
                    continue;
                }
                if (_readWaiters.Count > 0)
                {
                    if (_readWaiters.Dequeue().TrySetResult()) return;
                    continue;
                }
                _held = false;
                return;
            }
        }
    }

    private sealed class Releaser(SerialTransport owner) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Release();
        }
    }

    public void Dispose() => Close();
}

/// <summary>
/// Looks up/creates shared <see cref="SerialTransport"/> instances by their <c>serial_transport</c> config
/// name (§4.1.1). Every source referencing the same name must agree on port/baud/parity/stop bits — that's
/// a real electrical constraint on a shared UART line, not just a PGW convention, so a mismatch is a
/// config validation error rather than something silently tolerated or overridden by whichever source
/// happened to build first.
/// </summary>
public sealed class SerialTransportRegistry : IDisposable
{
    private readonly Dictionary<string, SerialTransport> _transports = new();
    private readonly object _lock = new();

    public SerialTransport GetOrCreate(string name, string portName, int baudRate, Parity parity, StopBits stopBits, Handshake handshake = Handshake.None)
    {
        lock (_lock)
        {
            if (_transports.TryGetValue(name, out var existing))
            {
                if (existing.PortName != portName || existing.BaudRate != baudRate || existing.Parity != parity || existing.StopBits != stopBits)
                    throw new FormatException(
                        $"serial_transport '{name}': mismatched serial settings — already registered as " +
                        $"{existing.PortName} {existing.BaudRate}/{existing.Parity}/{existing.StopBits}, " +
                        $"but this source specifies {portName} {baudRate}/{parity}/{stopBits} (§4.1.1: every " +
                        "device on the same serial_transport must agree on port/baud/parity/stop_bits)");
                return existing;
            }

            var transport = new SerialTransport(name, portName, baudRate, parity, stopBits, handshake);
            _transports[name] = transport;
            return transport;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var t in _transports.Values) t.Dispose();
            _transports.Clear();
        }
    }
}
