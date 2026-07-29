using System.Collections.Concurrent;

namespace PGW.Core;

public sealed record LogEntry(DateTime TimestampUtc, string Source, string Level, string Message);

/// <summary>Ring buffer backing both the communications event log and the REST API call log (§8, §14.2).</summary>
public sealed class RingLog(int capacity = 2000)
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    public void Add(string source, string message, string level = "INFO")
    {
        _entries.Enqueue(new LogEntry(DateTime.UtcNow, source, level, message));
        while (_entries.Count > capacity) _entries.TryDequeue(out _);
    }

    public IReadOnlyCollection<LogEntry> Snapshot(int? last = null)
    {
        var all = _entries.ToArray();
        if (last is null || last >= all.Length) return all;
        return all[^last.Value..];
    }
}
