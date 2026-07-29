using PGW.Core;

namespace PGW.Core.Tests;

/// <summary>
/// A driver that knows nothing about Modbus or OPC UA — used by <see cref="CoreIsolationTests"/> to prove
/// the core and delivery interfaces are genuinely protocol-agnostic (§13 acceptance test 12).
/// </summary>
public sealed class FakeProtocolDriver : IProtocolDriver
{
    public string DriverTypeId => "fake_test_protocol";
    public DriverCapabilities Capabilities { get; } = new(SupportsWrite: true, SupportsBrowse: false, SupportsSubscription: false);
    public object? LastWritten { get; private set; }

    public Task<DriverConnectResult> ConnectAsync(DeviceHandle device, CancellationToken ct) => Task.FromResult(DriverConnectResult.Success);

    public Task DisconnectAsync(DeviceHandle device, CancellationToken ct) => Task.CompletedTask;

    public async Task StartPollingAsync(DeviceHandle device, ITagSink sink, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                sink.Publish($"{device.Channel}.value", 4242, TagQuality.Good, DateTime.UtcNow);
                await Task.Delay(30, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    public Task<WriteResult> WriteAsync(TagAddress address, TagValue value, CancellationToken ct)
    {
        LastWritten = value.Value;
        return Task.FromResult(WriteResult.Success());
    }

    public DriverHealth GetHealth(DeviceHandle device) => new(true, 0, null, 0);
}
