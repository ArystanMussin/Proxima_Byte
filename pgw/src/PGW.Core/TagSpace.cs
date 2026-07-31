using System.Collections.Concurrent;
using System.Diagnostics;

namespace PGW.Core;

public sealed record TagDefinition(
    string Id,
    TagDataType DataType,
    TagAccess Access,
    DeviceHandle OwnerDevice,
    string NativeAddress,
    int ScanRateMs = 1000,
    double Deadband = 0,
    ScalingConfig? Scaling = null,
    string? Description = null,
    string? Units = null,
    OnBadPolicy OnBad = OnBadPolicy.Hold,
    double? WriteMin = null,
    double? WriteMax = null);

public enum OnBadPolicy { Hold, Zero, Substitute, FreezeAndFlag }

public sealed record TagSnapshot(TagDefinition Definition, object? Value, TagQuality Quality, QualitySubCode SubCode, DateTime SourceTimestampUtc, DateTime? ServerTimestampUtc);

public interface ITagSource
{
    TagSnapshot? Get(string tagId);
    IReadOnlyCollection<TagSnapshot> GetAll();
    event Action<TagSnapshot>? Changed;
}

/// <summary>
/// The Tag Space: single in-memory, thread-safe store that decouples N source drivers from M delivery
/// interfaces. Drivers publish through <see cref="ITagSink"/>; interfaces read/subscribe through <see cref="ITagSource"/>.
/// </summary>
public sealed class TagSpace : ITagSink, ITagSource
{
    private sealed class Entry
    {
        public required TagDefinition Def;
        public TagSnapshot? Snapshot;
        public IProtocolDriver? Owner;
        public TagAddress Address;
    }

    private readonly ConcurrentDictionary<string, Entry> _tags = new();

    public event Action<TagSnapshot>? Changed;

    public void Register(TagDefinition def, IProtocolDriver owner, TagAddress address)
    {
        var entry = new Entry { Def = def, Owner = owner, Address = address };
        entry.Snapshot = new TagSnapshot(def, TagTypeConversion.DefaultValue(def.DataType), TagQuality.Bad, QualitySubCode.NotInitialized, DateTime.UtcNow, null);
        _tags[def.Id] = entry;
    }

    /// <summary>Drops a tag entirely (§7.3 hot reload: a source that's gone or changed shouldn't leave stale entries behind).</summary>
    public void Unregister(string tagId) => _tags.TryRemove(tagId, out _);

    public IEnumerable<TagDefinition> Definitions => _tags.Values.Select(e => e.Def);

    public void Publish(string tagId, object? value, TagQuality quality, DateTime sourceTimestampUtc, QualitySubCode subCode = QualitySubCode.None, DateTime? serverTimestampUtc = null)
    {
        if (!_tags.TryGetValue(tagId, out var entry)) return;

        var scaled = quality == TagQuality.Good ? Scaling.Apply(entry.Def.Scaling, value) : entry.Snapshot?.Value;
        scaled = TagTypeConversion.Coerce(scaled, entry.Def.DataType);

        if (entry.Def.Deadband > 0 && entry.Snapshot is { Quality: TagQuality.Good } prev && quality == TagQuality.Good
            && IsWithinDeadband(prev.Value, scaled, entry.Def.Deadband))
        {
            return;
        }

        var snap = new TagSnapshot(entry.Def, scaled, quality, subCode, sourceTimestampUtc, serverTimestampUtc);
        entry.Snapshot = snap;
        Changed?.Invoke(snap);
    }

    /// <summary>Seeds a tag with a value restored from disk; marked Uncertain until the driver confirms it live.</summary>
    public void RestoreLastValue(string tagId, object? value, DateTime timestampUtc)
    {
        if (!_tags.TryGetValue(tagId, out var entry)) return;
        entry.Snapshot = new TagSnapshot(entry.Def, TagTypeConversion.Coerce(value, entry.Def.DataType), TagQuality.Uncertain, QualitySubCode.None, timestampUtc, null);
    }

    public TagSnapshot? Get(string tagId) => _tags.TryGetValue(tagId, out var e) ? e.Snapshot : null;

    public IReadOnlyCollection<TagSnapshot> GetAll() => _tags.Values.Select(e => e.Snapshot!).ToArray();

    /// <summary>
    /// Routes a write to the tag's owning driver (§3.2 p.6). Returns synchronously with a timeout;
    /// the driver call itself may be async under the hood.
    /// </summary>
    public async Task<WriteResult> WriteAsync(string tagId, object? engineeringValue, TimeSpan timeout, CancellationToken ct)
    {
        if (!_tags.TryGetValue(tagId, out var entry) || entry.Owner is null)
            return WriteResult.Failure("unknown_tag");
        if (entry.Def.Access != TagAccess.RW)
            return WriteResult.Failure("read_only");

        if (entry.Def.WriteMin is not null && entry.Def.WriteMax is not null && engineeringValue is not null)
        {
            var d = Convert.ToDouble(engineeringValue);
            if (d < entry.Def.WriteMin || d > entry.Def.WriteMax)
                return WriteResult.Failure("out_of_range");
        }

        var raw = TagTypeConversion.Coerce(Scaling.Reverse(entry.Def.Scaling, engineeringValue), entry.Def.DataType);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await entry.Owner.WriteAsync(entry.Address, new TagValue(raw, TagQuality.Good, QualitySubCode.None, DateTime.UtcNow), cts.Token);
            return result with { LatencyMs = sw.Elapsed.TotalMilliseconds };
        }
        catch (OperationCanceledException)
        {
            return WriteResult.Failure("timeout", sw.Elapsed.TotalMilliseconds);
        }
    }

    private static bool IsWithinDeadband(object? oldValue, object? newValue, double deadband)
    {
        if (oldValue is null || newValue is null) return false;
        try
        {
            var diff = Math.Abs(Convert.ToDouble(newValue) - Convert.ToDouble(oldValue));
            return diff < deadband;
        }
        catch (InvalidCastException) { return false; }
        catch (FormatException) { return false; }
    }
}
