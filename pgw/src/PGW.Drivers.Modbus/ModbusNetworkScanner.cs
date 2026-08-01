using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace PGW.Drivers.Modbus;

/// <summary>
/// Modbus has no protocol-level discovery (no broadcast/Who-Is) — "finding devices on the network"
/// here means methodical enumeration, not real discovery, and is named `scan` rather than `discover`
/// throughout the API/UI to avoid promising something Modbus can't do (§4.5).
/// </summary>
public static class ModbusScanRange
{
    // Safety cap so a fat-fingered /8 doesn't spawn tens of millions of scan attempts — split larger
    // ranges into multiple scans instead. A /20 (4094 usable hosts) already covers a very large site.
    public const int MaxHosts = 4096;

    public static List<IPAddress> Parse(string spec)
    {
        spec = spec.Trim();
        if (spec.Contains('/')) return ParseCidr(spec);
        if (spec.Contains('-')) return ParseDashRange(spec);
        return new List<IPAddress> { ParseIPv4(spec, spec) };
    }

    private static List<IPAddress> ParseCidr(string spec)
    {
        var parts = spec.Split('/');
        if (parts.Length != 2) throw new FormatException($"'{spec}' is not a valid IPv4 CIDR (e.g. 192.168.1.0/24)");
        var baseIp = ParseIPv4(parts[0], spec);
        if (!int.TryParse(parts[1], out var prefixLen) || prefixLen is < 0 or > 32)
            throw new FormatException($"'{spec}': prefix length must be 0-32");

        var baseAddr = BinaryPrimitives.ReadUInt32BigEndian(baseIp.GetAddressBytes());
        var hostBits = 32 - prefixLen;
        var mask = hostBits == 0 ? 0xFFFFFFFFu : 0xFFFFFFFFu << hostBits;
        var network = baseAddr & mask;

        // /31 and /32 have no network/broadcast address to exclude (RFC 3021); everything else does.
        var includeAll = hostBits <= 1;
        var start = includeAll ? network : network + 1;
        var count = includeAll ? (1L << hostBits) : (1L << hostBits) - 2;
        if (count <= 0) count = 1;
        if (count > MaxHosts) throw new FormatException($"'{spec}': {count} addresses exceeds the {MaxHosts}-host scan limit — use a smaller range");

        var result = new List<IPAddress>((int)count);
        for (var i = 0L; i < count; i++) result.Add(ToIPv4((uint)(start + i)));
        return result;
    }

    private static List<IPAddress> ParseDashRange(string spec)
    {
        var parts = spec.Split('-', 2);
        if (parts.Length != 2) throw new FormatException($"'{spec}' is not a valid IP range (e.g. 192.168.1.1-192.168.1.50)");
        var start = ParseIPv4(parts[0], spec);
        var end = ParseIPv4(parts[1], spec);
        var s = BinaryPrimitives.ReadUInt32BigEndian(start.GetAddressBytes());
        var e = BinaryPrimitives.ReadUInt32BigEndian(end.GetAddressBytes());
        if (e < s) throw new FormatException($"'{spec}': end address is before start address");
        var count = (long)e - s + 1;
        if (count > MaxHosts) throw new FormatException($"'{spec}': {count} addresses exceeds the {MaxHosts}-host scan limit — use a smaller range");

        var result = new List<IPAddress>((int)count);
        for (var a = s; ; a++)
        {
            result.Add(ToIPv4(a));
            if (a == e) break;
        }
        return result;
    }

    private static IPAddress ParseIPv4(string s, string original)
    {
        s = s.Trim();
        if (!IPAddress.TryParse(s, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
            throw new FormatException($"'{original}': '{s}' is not a valid IPv4 address");
        return ip;
    }

    private static IPAddress ToIPv4(uint addr)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, addr);
        return new IPAddress(bytes);
    }
}

public enum ScanJobState { Running, Completed, Cancelled }

public sealed record ScanHostResult(string Ip, int Port, bool Responded, IReadOnlyList<byte> UnitIdsFound);

/// <summary>One in-flight or finished network scan (§4.5). Tracked by <see cref="ModbusNetworkScanner"/>
/// so the REST layer can poll status/results and cancel by id instead of blocking a request for
/// however long a /24 takes.</summary>
public sealed class ModbusNetworkScanJob
{
    public string Id { get; } = Guid.NewGuid().ToString("N")[..12];
    public ScanJobState State { get; internal set; } = ScanJobState.Running;
    public int Total { get; internal init; }
    public int Scanned;
    public readonly ConcurrentBag<ScanHostResult> Results = new();
    public DateTime StartedUtc { get; } = DateTime.UtcNow;
    internal readonly CancellationTokenSource Cts = new();
}

/// <summary>
/// Modbus TCP subnet scan (§4.5): for each address in range, TCP-connect (short timeout), and if that
/// succeeds, try a trivial holding-register read (unit id 1, then a short range 1-10 — an exception
/// 0x0B on some unit ids but not others typically means a TCP-to-RTU gateway routing to serial devices
/// behind it, per §4.5, so trying more than just unit id 1 is worth it). Concurrency is capped so a
/// full-range scan doesn't look like a port-scan/DoS to fragile field PLCs.
///
/// No Read Device Identification (FC43/MEI 0x0E) support — FluentModbus doesn't implement that function
/// code, so `device_id` from §4.5's result shape isn't populated; noted here rather than left silently
/// absent from the API response.
/// </summary>
public sealed class ModbusNetworkScanner : IDisposable
{
    private readonly ConcurrentDictionary<string, ModbusNetworkScanJob> _jobs = new();
    private static readonly byte[] DefaultUnitIds = Enumerable.Range(1, 10).Select(i => (byte)i).ToArray();

    public ModbusNetworkScanJob Start(string rangeSpec, int port, int timeoutMs, int maxConcurrency, IReadOnlyList<byte>? unitIds = null)
    {
        var ips = ModbusScanRange.Parse(rangeSpec);
        var job = new ModbusNetworkScanJob { Total = ips.Count };
        _jobs[job.Id] = job;
        _ = RunAsync(job, ips, port, Math.Max(50, timeoutMs), Math.Clamp(maxConcurrency, 1, 200), unitIds ?? DefaultUnitIds);
        return job;
    }

    public ModbusNetworkScanJob? Get(string id) => _jobs.TryGetValue(id, out var job) ? job : null;

    public bool Cancel(string id)
    {
        if (!_jobs.TryGetValue(id, out var job)) return false;
        job.Cts.Cancel();
        return true;
    }

    private async Task RunAsync(ModbusNetworkScanJob job, List<IPAddress> ips, int port, int timeoutMs, int maxConcurrency, IReadOnlyList<byte> unitIds)
    {
        using var sem = new SemaphoreSlim(maxConcurrency);
        var tasks = ips.Select(async ip =>
        {
            try { await sem.WaitAsync(job.Cts.Token); }
            catch (OperationCanceledException) { return; }
            try { await ScanOneAsync(job, ip, port, timeoutMs, unitIds); }
            finally
            {
                Interlocked.Increment(ref job.Scanned);
                sem.Release();
            }
        });

        try { await Task.WhenAll(tasks); }
        catch (Exception) { /* individual failures are recorded per-host, not surfaced here */ }
        job.State = job.Cts.IsCancellationRequested ? ScanJobState.Cancelled : ScanJobState.Completed;
    }

    private async Task ScanOneAsync(ModbusNetworkScanJob job, IPAddress ip, int port, int timeoutMs, IReadOnlyList<byte> unitIds)
    {
        if (job.Cts.IsCancellationRequested) return;

        using var tcp = new TcpClient();
        var responded = false;
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(job.Cts.Token);
            connectCts.CancelAfter(timeoutMs);
            await tcp.ConnectAsync(ip, port, connectCts.Token);
            responded = true;
        }
        catch (OperationCanceledException) when (job.Cts.IsCancellationRequested) { return; }
        catch (Exception) { /* refused/timeout/unreachable — just not there */ }

        if (!responded)
        {
            job.Results.Add(new ScanHostResult(ip.ToString(), port, false, Array.Empty<byte>()));
            return;
        }
        tcp.Close();

        var found = new List<byte>();
        var client = new FluentModbus.ModbusTcpClient { ConnectTimeout = timeoutMs, ReadTimeout = timeoutMs, WriteTimeout = timeoutMs };
        try
        {
            await Task.Run(() => client.Connect(new IPEndPoint(ip, port)), job.Cts.Token);
            foreach (var unitId in unitIds)
            {
                if (job.Cts.IsCancellationRequested) break;
                try
                {
                    await client.ReadHoldingRegistersAsync<ushort>(unitId, 0, 1, job.Cts.Token);
                    found.Add(unitId);
                }
                catch (OperationCanceledException) when (job.Cts.IsCancellationRequested) { break; }
                catch (Exception) { /* this unit id didn't answer — try the rest of the short range */ }
            }
        }
        catch (OperationCanceledException) when (job.Cts.IsCancellationRequested) { }
        catch (Exception) { /* TCP connected but Modbus framing failed — leave found empty, still "responded" */ }
        finally
        {
            try { if (client.IsConnected) client.Disconnect(); } catch (Exception) { }
        }

        job.Results.Add(new ScanHostResult(ip.ToString(), port, true, found));
    }

    public void Dispose()
    {
        foreach (var job in _jobs.Values) job.Cts.Cancel();
    }
}
