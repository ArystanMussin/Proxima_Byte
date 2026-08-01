using System.Net;
using FluentModbus;
using PGW.Drivers.Modbus;
using Xunit;

namespace PGW.Core.Tests;

/// <summary>§4.5 / §13 acceptance test 14: scans a small real address range against a real
/// <see cref="ModbusTcpServer"/> (unit 1 registered) plus a non-responding address, over real TCP
/// sockets — no protocol mocks.</summary>
public class ModbusNetworkScannerTests
{
    [Fact]
    public void ParseRange_Handles_Cidr_And_Dash_Forms()
    {
        Assert.Equal(2, ModbusScanRange.Parse("10.0.0.0/30").Count); // /30: 4 addresses total, minus network+broadcast = 2 usable
        Assert.Equal(2, ModbusScanRange.Parse("10.0.0.1/31").Count); // /31: no network/broadcast exclusion (RFC 3021)
        Assert.Single(ModbusScanRange.Parse("10.0.0.5/32"));
        Assert.Equal(5, ModbusScanRange.Parse("10.0.0.1-10.0.0.5").Count);
        Assert.Single(ModbusScanRange.Parse("10.0.0.7"));
    }

    [Fact]
    public void ParseRange_Rejects_Ranges_Bigger_Than_The_Safety_Cap()
    {
        Assert.Throws<FormatException>(() => ModbusScanRange.Parse("10.0.0.0/8"));
        Assert.Throws<FormatException>(() => ModbusScanRange.Parse("10.0.0.0-10.5.0.0"));
    }

    [Fact]
    public async Task Scan_Finds_A_Real_Responder_And_Its_Unit_Id_And_Skips_A_Non_Responder()
    {
        var port = TestUtil.GetFreePort();
        using var plc = new ModbusTcpServer();
        plc.Start(new IPEndPoint(IPAddress.Loopback, port));
        plc.AddUnit(1);

        using var scanner = new ModbusNetworkScanner();
        // 127.0.0.1 (real responder, unit 1) and 127.0.0.2 (loopback range, nothing listening there) —
        // a real two-host range, not a mock.
        var job = scanner.Start("127.0.0.1-127.0.0.2", port, timeoutMs: 300, maxConcurrency: 4);

        await TestUtil.WaitUntilAsync(() => job.State != ScanJobState.Running, TimeSpan.FromSeconds(10));

        Assert.Equal(ScanJobState.Completed, job.State);
        Assert.Equal(2, job.Total);
        Assert.Equal(2, job.Scanned);

        var results = job.Results.ToDictionary(r => r.Ip);
        Assert.True(results["127.0.0.1"].Responded);
        Assert.Contains((byte)1, results["127.0.0.1"].UnitIdsFound);
        Assert.False(results["127.0.0.2"].Responded);
        Assert.Empty(results["127.0.0.2"].UnitIdsFound);
    }

    [Fact]
    public void Cancel_Returns_False_For_An_Unknown_Scan_Id()
    {
        using var scanner = new ModbusNetworkScanner();
        Assert.False(scanner.Cancel("no-such-scan-id"));
    }

    [Fact]
    public async Task Scan_Can_Be_Cancelled_And_Winds_Down_Without_Hanging()
    {
        // A large serial (maxConcurrency: 1) scan of loopback addresses nothing listens on: each
        // refusal is fast but not instantaneous, and with hundreds of them serialized, there's a real
        // window to cancel mid-flight rather than racing a scan that might already be done. Whether it
        // lands as Cancelled or (on a very fast machine) Completed first, the important behavior under
        // test is that Cancel() is accepted and the job reliably reaches a terminal state — not a
        // precise "stopped at exactly host N" timing guarantee.
        using var scanner = new ModbusNetworkScanner();
        var job = scanner.Start("127.0.0.2-127.0.2.255", port: 1, timeoutMs: 300, maxConcurrency: 1);

        await Task.Delay(50); // let it get going
        Assert.True(scanner.Cancel(job.Id));

        await TestUtil.WaitUntilAsync(() => job.State != ScanJobState.Running, TimeSpan.FromSeconds(15));
        Assert.NotEqual(ScanJobState.Running, job.State);
    }
}
