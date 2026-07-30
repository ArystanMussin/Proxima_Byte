using System.Collections.Concurrent;
using System.Net;
using FluentModbus;
using PGW.Core;

namespace PGW.Drivers.Modbus;

public sealed record ScanTarget(string Host, int Port, byte UnitId, ModbusArea Area, ushort Start, int Length,
    TagDataType Type, WordOrder Order, int TimeoutMs = 1000);

public sealed record ScanRow(int Address, ushort[] Raw, object? Value, string Hex, string Binary);

public sealed record ScanResult(bool Ok, string? Error, double LatencyMs, IReadOnlyList<ScanRow> Rows)
{
    public static ScanResult Failure(string error, double latencyMs) => new(false, error, latencyMs, Array.Empty<ScanRow>());
}

/// <summary>
/// Ad-hoc Modbus TCP master for diagnostics — a built-in ModScan. Reads/writes any address on any
/// device independently of the configured sources, so an engineer can poke at hardware without
/// editing project.yaml first.
///
/// Connections are pooled per host:port: a 1 Hz poll from the dashboard must not open a fresh TCP
/// session per request, and real PLCs are stingy about concurrent connections. Idle entries are
/// dropped so a scan of a device that's since been unplugged doesn't hold a socket forever.
/// </summary>
public sealed class ModbusScanner : IDisposable
{
    private sealed class Conn
    {
        public readonly ModbusTcpClient Client = new();
        public readonly SemaphoreSlim Io = new(1, 1);
        public DateTime LastUsedUtc = DateTime.UtcNow;
    }

    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(2);
    private readonly ConcurrentDictionary<string, Conn> _pool = new();

    public const int MaxRegisters = 125;   // Modbus PDU limit for FC03/FC04
    public const int MaxBits = 2000;       // Modbus PDU limit for FC01/FC02

    public static bool IsBitArea(ModbusArea area) => area is ModbusArea.Coil or ModbusArea.DiscreteInput;

    public async Task<ScanResult> ReadAsync(ScanTarget t, CancellationToken ct)
    {
        SweepIdle();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var conn = _pool.GetOrAdd(Key(t.Host, t.Port), _ => new Conn());

        await conn.Io.WaitAsync(ct);
        try
        {
            await EnsureConnectedAsync(conn, t.Host, t.Port, t.TimeoutMs, ct);

            if (IsBitArea(t.Area))
            {
                var packed = t.Area == ModbusArea.Coil
                    ? (await conn.Client.ReadCoilsAsync(t.UnitId, t.Start, t.Length, ct)).ToArray()
                    : (await conn.Client.ReadDiscreteInputsAsync(t.UnitId, t.Start, t.Length, ct)).ToArray();
                return new ScanResult(true, null, sw.Elapsed.TotalMilliseconds, BuildBitRows(packed, t.Start, t.Length));
            }

            var regs = t.Area == ModbusArea.HoldingRegister
                ? (await conn.Client.ReadHoldingRegistersAsync<ushort>(t.UnitId, t.Start, t.Length, ct)).ToArray()
                : (await conn.Client.ReadInputRegistersAsync<ushort>(t.UnitId, t.Start, t.Length, ct)).ToArray();
            return new ScanResult(true, null, sw.Elapsed.TotalMilliseconds, BuildRegisterRows(regs, t.Start, t.Type, t.Order));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // A failed read usually means the socket is unusable (refused/reset/timeout); drop it so the
            // next poll reconnects instead of failing forever against a dead session.
            Close(conn);
            return ScanResult.Failure(ex.Message, sw.Elapsed.TotalMilliseconds);
        }
        finally
        {
            conn.LastUsedUtc = DateTime.UtcNow;
            conn.Io.Release();
        }
    }

    public async Task<ScanResult> WriteAsync(string host, int port, byte unitId, ModbusArea area, ushort address,
        TagDataType type, WordOrder order, object? value, int timeoutMs, CancellationToken ct)
    {
        SweepIdle();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var conn = _pool.GetOrAdd(Key(host, port), _ => new Conn());

        await conn.Io.WaitAsync(ct);
        try
        {
            await EnsureConnectedAsync(conn, host, port, timeoutMs, ct);

            if (area == ModbusArea.Coil)
            {
                await conn.Client.WriteSingleCoilAsync(unitId, address, Convert.ToBoolean(value), ct);
            }
            else if (area == ModbusArea.HoldingRegister)
            {
                var regs = ModbusCodec.Encode(value, type, order, ModbusCodec.RegisterCount(type));
                if (regs.Length == 1) await conn.Client.WriteSingleRegisterAsync(unitId, address, regs[0], ct);
                else await conn.Client.WriteMultipleRegistersAsync(unitId, address, regs, ct);
            }
            else
            {
                return ScanResult.Failure("area is read-only (only HR and CO are writable)", sw.Elapsed.TotalMilliseconds);
            }
            return new ScanResult(true, null, sw.Elapsed.TotalMilliseconds, Array.Empty<ScanRow>());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Close(conn);
            return ScanResult.Failure(ex.Message, sw.Elapsed.TotalMilliseconds);
        }
        finally
        {
            conn.LastUsedUtc = DateTime.UtcNow;
            conn.Io.Release();
        }
    }

    /// <summary>Explicit disconnect, so "Stop" in the UI actually frees the device's connection slot.</summary>
    public void Close(string host, int port)
    {
        if (_pool.TryRemove(Key(host, port), out var conn)) Close(conn);
    }

    private async Task EnsureConnectedAsync(Conn conn, string host, int port, int timeoutMs, CancellationToken ct)
    {
        conn.Client.ConnectTimeout = timeoutMs;
        conn.Client.ReadTimeout = timeoutMs;
        conn.Client.WriteTimeout = timeoutMs;
        if (conn.Client.IsConnected) return;

        var ip = IPAddress.TryParse(host, out var parsed) ? parsed : (await Dns.GetHostAddressesAsync(host, ct)).First();
        await Task.Run(() => conn.Client.Connect(new IPEndPoint(ip, port)), ct);
    }

    // Span-typed locals can't live in an async method (CS4012), so decoding stays in sync helpers.
    private static List<ScanRow> BuildRegisterRows(ushort[] regs, ushort start, TagDataType type, WordOrder order)
    {
        var perValue = ModbusCodec.RegisterCount(type);
        var rows = new List<ScanRow>(regs.Length / perValue);
        for (int i = 0; i + perValue <= regs.Length; i += perValue)
        {
            var slice = regs.AsSpan(i, perValue);
            rows.Add(new ScanRow(start + i, slice.ToArray(), ModbusCodec.Decode(slice, type, order), Hex(slice), Binary(slice)));
        }
        return rows;
    }

    private static List<ScanRow> BuildBitRows(byte[] packed, ushort start, int length)
    {
        var rows = new List<ScanRow>(length);
        for (int i = 0; i < length; i++)
        {
            var bit = ModbusCodec.GetPackedBit(packed, i);
            rows.Add(new ScanRow(start + i, Array.Empty<ushort>(), bit, bit ? "1" : "0", bit ? "1" : "0"));
        }
        return rows;
    }

    private static string Hex(ReadOnlySpan<ushort> regs)
    {
        var parts = new string[regs.Length];
        for (int i = 0; i < regs.Length; i++) parts[i] = regs[i].ToString("X4");
        return string.Join(" ", parts);
    }

    private static string Binary(ReadOnlySpan<ushort> regs)
    {
        var parts = new string[regs.Length];
        for (int i = 0; i < regs.Length; i++) parts[i] = Convert.ToString(regs[i], 2).PadLeft(16, '0');
        return string.Join(" ", parts);
    }

    private static string Key(string host, int port) => $"{host}:{port}";

    private static void Close(Conn conn)
    {
        try { if (conn.Client.IsConnected) conn.Client.Disconnect(); }
        catch (Exception) { /* best-effort teardown */ }
    }

    private void SweepIdle()
    {
        var cutoff = DateTime.UtcNow - IdleTimeout;
        foreach (var (key, conn) in _pool.ToArray())
        {
            if (conn.LastUsedUtc > cutoff) continue;
            if (!conn.Io.Wait(0)) continue;      // in use right now — leave it to its owner
            try
            {
                if (conn.LastUsedUtc <= cutoff && _pool.TryRemove(key, out _)) Close(conn);
            }
            finally { conn.Io.Release(); }
        }
    }

    public void Dispose()
    {
        foreach (var (key, conn) in _pool.ToArray())
        {
            _pool.TryRemove(key, out _);
            Close(conn);
        }
    }
}
