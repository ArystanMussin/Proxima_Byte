namespace PGW.Core;

public enum TagQuality { Good, Uncertain, Bad }

public enum QualitySubCode { None, NotInitialized, CommFailure, Timeout, ProtocolError, DeviceException }

public enum TagDataType { Bool, Int16, UInt16, Int32, UInt32, Int64, UInt64, Float32, Float64, String }

public enum TagAccess { RO, RW }

/// <summary>Logical device address as seen from the core; physical address stays inside the driver's own config.</summary>
public readonly record struct DeviceHandle(string Channel, string Device)
{
    public override string ToString() => $"{Channel}.{Device}";
}

/// <summary>Driver-native address for a tag, opaque to the core (e.g. "HR:100" or an OPC UA NodeId).</summary>
public readonly record struct TagAddress(DeviceHandle Device, string Native);

public sealed record TagValue(object? Value, TagQuality Quality, QualitySubCode SubCode, DateTime SourceTimestampUtc, DateTime? ServerTimestampUtc = null);

public sealed record WriteResult(bool Ok, string? ErrorCode, double LatencyMs)
{
    public static WriteResult Success(double latencyMs = 0) => new(true, null, latencyMs);
    public static WriteResult Failure(string errorCode, double latencyMs = 0) => new(false, errorCode, latencyMs);
    public static readonly WriteResult NotSupported = new(false, "not_supported", 0);
}

public sealed record DriverCapabilities(bool SupportsWrite, bool SupportsBrowse, bool SupportsSubscription);

public sealed record DriverConnectResult(bool Ok, string? Error)
{
    public static readonly DriverConnectResult Success = new(true, null);
    public static DriverConnectResult Failure(string error) => new(false, error);
}

public sealed record DriverHealth(bool Connected, long ErrorCount, string? LastError, double LatencyMs);

public sealed record InterfaceHealth(int ClientCount, long RequestsTotal, long ExceptionsTotal);

/// <summary>Core publishes nothing to drivers; a driver only ever pushes values in through this sink.</summary>
public interface ITagSink
{
    void Publish(string tagId, object? value, TagQuality quality, DateTime sourceTimestampUtc, QualitySubCode subCode = QualitySubCode.None, DateTime? serverTimestampUtc = null);
}

/// <summary>
/// The one contract every data source implements (Modbus TCP client, OPC UA client, and any future
/// protocol). The core never contains protocol-specific code — only calls through this interface.
/// </summary>
public interface IProtocolDriver
{
    string DriverTypeId { get; }
    DriverCapabilities Capabilities { get; }

    Task<DriverConnectResult> ConnectAsync(DeviceHandle device, CancellationToken ct);
    Task DisconnectAsync(DeviceHandle device, CancellationToken ct);

    /// <summary>Driver publishes values into the Tag Space via <paramref name="sink"/>; the core never polls the driver.</summary>
    Task StartPollingAsync(DeviceHandle device, ITagSink sink, CancellationToken ct);

    Task<WriteResult> WriteAsync(TagAddress address, TagValue value, CancellationToken ct);

    DriverHealth GetHealth(DeviceHandle device);
}

/// <summary>The symmetric contract for delivery interfaces (Modbus TCP server today, OPC UA server / MQTT tomorrow).</summary>
public interface IProtocolInterface
{
    string InterfaceTypeId { get; }

    Task StartAsync(ITagSource source, CancellationToken ct);
    Task StopAsync(CancellationToken ct);

    /// <summary>External client write request; core routes it to the owning driver.</summary>
    Task<WriteResult> OnExternalWriteAsync(string tagId, TagValue value, CancellationToken ct);

    InterfaceHealth GetHealth();
}
