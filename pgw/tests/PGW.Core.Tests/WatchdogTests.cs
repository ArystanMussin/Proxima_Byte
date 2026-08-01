using PGW.Core;
using PGW.Host;
using Xunit;

namespace PGW.Core.Tests;

file sealed class WatchdogTempPathProvider : IPathProvider
{
    public string ConfigDir { get; }
    public string DataDir { get; }
    public string LogsDir { get; }

    public WatchdogTempPathProvider()
    {
        var root = Path.Combine(Path.GetTempPath(), "pgw-watchdog-test-" + Guid.NewGuid());
        ConfigDir = Path.Combine(root, "config");
        DataDir = Path.Combine(root, "data");
        LogsDir = Path.Combine(root, "logs");
        Directory.CreateDirectory(ConfigDir);
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LogsDir);
    }
}

/// <summary>A driver whose polling task throws an unhandled exception a fixed number of times before
/// settling down and polling normally — used to drive the §9.0 watchdog through restart-with-backoff,
/// and (with <see cref="AlwaysCrash"/>) all the way into the Faulted state (§13 acceptance test 13).</summary>
internal sealed class CrashingFakeDriver : IProtocolDriver
{
    public string DriverTypeId => "fake_crashing_protocol";
    public DriverCapabilities Capabilities { get; } = new(SupportsWrite: false, SupportsBrowse: false, SupportsSubscription: false);

    public int CrashesBeforeRecovering { get; init; }
    public bool AlwaysCrash { get; init; }
    public int StartCount { get; private set; }

    public Task<DriverConnectResult> ConnectAsync(DeviceHandle device, CancellationToken ct) => Task.FromResult(DriverConnectResult.Success);
    public Task DisconnectAsync(DeviceHandle device, CancellationToken ct) => Task.CompletedTask;

    public async Task StartPollingAsync(DeviceHandle device, ITagSink sink, CancellationToken ct)
    {
        StartCount++;
        if (AlwaysCrash || StartCount <= CrashesBeforeRecovering)
            throw new InvalidOperationException($"deliberate test crash #{StartCount}");

        // Recovered: poll normally until cancelled, like every real driver's loop.
        try
        {
            while (!ct.IsCancellationRequested)
            {
                sink.Publish($"{device.Channel}.value", 99, TagQuality.Good, DateTime.UtcNow);
                await Task.Delay(20, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    public Task<WriteResult> WriteAsync(TagAddress address, TagValue value, CancellationToken ct) =>
        Task.FromResult(WriteResult.NotSupported);

    public DriverHealth GetHealth(DeviceHandle device) => new(StartCount > CrashesBeforeRecovering, 0, null, 0);
}

/// <summary>§9.0 / §13 acceptance test 13: a driver's polling task crashing with an unhandled exception
/// is restarted by the engine's watchdog (with backoff), logs the event, and resumes polling on its
/// own — or, if it keeps crashing past the failure-window limit, the device is marked Faulted and the
/// watchdog stops restarting it.</summary>
public class WatchdogTests
{
    private static (GatewayEngine Engine, GatewayEngine.DriverEntry Entry) MakeEngine(CrashingFakeDriver driver, string name)
    {
        var engine = new GatewayEngine(new WatchdogTempPathProvider(), "unused.yaml");
        var device = new DeviceHandle(name, name);
        var sourceConfig = new SourceConfig { Name = name, Driver = driver.DriverTypeId };
        var tagId = $"{name}.value";
        var def = new TagDefinition(tagId, TagDataType.Int32, TagAccess.RO, device, "n/a");
        engine.TagSpace.Register(def, driver, new TagAddress(device, "n/a"));
        engine.RegisterSourceSystemTags(sourceConfig);
        var entry = new GatewayEngine.DriverEntry(sourceConfig, driver, device, new List<string> { tagId });
        return (engine, entry);
    }

    [Fact]
    public async Task Crashing_Driver_Is_Restarted_With_Backoff_And_Resumes_Polling()
    {
        var driver = new CrashingFakeDriver { CrashesBeforeRecovering = 2 };
        var (engine, entry) = MakeEngine(driver, "crashy_recovers");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var supervise = engine.SuperviseDriverPollingAsync(entry, cts.Token,
            restartMinMs: 20, restartMaxMs: 200, maxFailuresInWindow: 10, window: TimeSpan.FromSeconds(30));

        // Two crashes get restarted (well under the failure-window limit of 10), then the driver's
        // third StartPollingAsync call succeeds and starts publishing — status should read "Running"
        // again once it's back up, not "Faulted".
        await TestUtil.WaitUntilAsync(() => engine.TagSpace.Get("crashy_recovers.value")?.Quality == TagQuality.Good, TimeSpan.FromSeconds(5));

        Assert.Equal(3, driver.StartCount); // 2 crashes + 1 successful restart
        Assert.Equal("Running", engine.TagSpace.Get("_System.crashy_recovers.status")?.Value);

        var events = engine.EventLog.Snapshot(20);
        Assert.Contains(events, e => e.Message.Contains("polling task crashed") && e.Message.Contains("restarting"));

        cts.Cancel();
        try { await supervise; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Driver_That_Keeps_Crashing_Is_Marked_Faulted_And_Watchdog_Gives_Up()
    {
        var driver = new CrashingFakeDriver { AlwaysCrash = true };
        var (engine, entry) = MakeEngine(driver, "crashy_forever");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        // Tiny backoff and a low failure-window limit (3) so the test trips Faulted in well under a second.
        var supervise = engine.SuperviseDriverPollingAsync(entry, cts.Token,
            restartMinMs: 5, restartMaxMs: 20, maxFailuresInWindow: 3, window: TimeSpan.FromSeconds(30));

        await TestUtil.WaitUntilAsync(() => engine.TagSpace.Get("_System.crashy_forever.status")?.Value as string == "Faulted", TimeSpan.FromSeconds(5));

        // Tag quality must reflect the outage — a Faulted device's last-known value is frozen as Bad,
        // not left looking like a healthy "just hasn't updated yet" tag.
        Assert.Equal(TagQuality.Bad, engine.TagSpace.Get("crashy_forever.value")?.Quality);

        var events = engine.EventLog.Snapshot(50);
        Assert.Contains(events, e => e.Message.Contains("marked Faulted"));

        // The watchdog loop itself must have returned (given up), not still be looping/restarting.
        var completed = await Task.WhenAny(supervise, Task.Delay(TimeSpan.FromSeconds(2))) == supervise;
        Assert.True(completed, "watchdog loop should have stopped restarting once Faulted");

        var startCountAtFault = driver.StartCount;
        await Task.Delay(200); // give it a moment; a still-looping watchdog would call StartPollingAsync again
        Assert.Equal(startCountAtFault, driver.StartCount);

        cts.Cancel();
    }
}
