using System.Net;
using FluentModbus;
using PGW.Core;
using PGW.Drivers.Modbus;
using Xunit;

namespace PGW.Core.Tests;

/// <summary>
/// End-to-end test for the built-in Modbus scanner (dashboard "Modbus Tool" tab, the in-app ModScan
/// replacement): reads/writes a real FluentModbus server directly, independent of any configured source.
/// </summary>
public class ModbusScannerTests
{
    [Fact]
    public async Task Reads_Holding_Registers_As_Uint16_And_Float64()
    {
        var port = TestUtil.GetFreePort();
        using var plc = new ModbusTcpServer();
        plc.Start(new IPEndPoint(IPAddress.Loopback, port));
        plc.AddUnit(1);
        SeedRegisters(plc);

        using var scanner = new ModbusScanner();

        var uintResult = await scanner.ReadAsync(
            new ScanTarget("127.0.0.1", port, 1, ModbusArea.HoldingRegister, 100, 1, TagDataType.UInt16, WordOrder.ABCD), default);
        Assert.True(uintResult.Ok, uintResult.Error);
        Assert.Equal((ushort)1234, Convert.ToUInt16(uintResult.Rows[0].Value));
        Assert.Equal("04D2", uintResult.Rows[0].Hex);

        var floatResult = await scanner.ReadAsync(
            new ScanTarget("127.0.0.1", port, 1, ModbusArea.HoldingRegister, 200, 4, TagDataType.Float64, WordOrder.ABCD), default);
        Assert.True(floatResult.Ok, floatResult.Error);
        Assert.Equal(3.14159, Convert.ToDouble(floatResult.Rows[0].Value), precision: 5);
    }

    [Fact]
    public async Task Writes_HoldingRegister_And_Coil_And_ReadsThemBack()
    {
        var port = TestUtil.GetFreePort();
        using var plc = new ModbusTcpServer();
        plc.Start(new IPEndPoint(IPAddress.Loopback, port));
        plc.AddUnit(1);

        using var scanner = new ModbusScanner();

        var writeReg = await scanner.WriteAsync("127.0.0.1", port, 1, ModbusArea.HoldingRegister, 50,
            TagDataType.UInt16, WordOrder.ABCD, (double)777, 1000, default);
        Assert.True(writeReg.Ok, writeReg.Error);

        var readReg = await scanner.ReadAsync(
            new ScanTarget("127.0.0.1", port, 1, ModbusArea.HoldingRegister, 50, 1, TagDataType.UInt16, WordOrder.ABCD), default);
        Assert.Equal((ushort)777, Convert.ToUInt16(readReg.Rows[0].Value));

        var writeCoil = await scanner.WriteAsync("127.0.0.1", port, 1, ModbusArea.Coil, 10,
            TagDataType.Bool, WordOrder.ABCD, true, 1000, default);
        Assert.True(writeCoil.Ok, writeCoil.Error);

        var readCoil = await scanner.ReadAsync(
            new ScanTarget("127.0.0.1", port, 1, ModbusArea.Coil, 10, 1, TagDataType.Bool, WordOrder.ABCD), default);
        Assert.Equal(true, readCoil.Rows[0].Value);
    }

    [Fact]
    public async Task Rejects_Write_To_ReadOnly_Area()
    {
        var port = TestUtil.GetFreePort();
        using var plc = new ModbusTcpServer();
        plc.Start(new IPEndPoint(IPAddress.Loopback, port));
        plc.AddUnit(1);

        using var scanner = new ModbusScanner();
        var result = await scanner.WriteAsync("127.0.0.1", port, 1, ModbusArea.InputRegister, 0,
            TagDataType.UInt16, WordOrder.ABCD, 1, 1000, default);

        Assert.False(result.Ok);
    }

    [Fact]
    public async Task Reuses_Pooled_Connection_Across_Reads_And_Recovers_After_Close()
    {
        var port = TestUtil.GetFreePort();
        using var plc = new ModbusTcpServer();
        plc.Start(new IPEndPoint(IPAddress.Loopback, port));
        plc.AddUnit(1);

        using var scanner = new ModbusScanner();
        var target = new ScanTarget("127.0.0.1", port, 1, ModbusArea.HoldingRegister, 0, 1, TagDataType.UInt16, WordOrder.ABCD);

        Assert.True((await scanner.ReadAsync(target, default)).Ok);
        Assert.True((await scanner.ReadAsync(target, default)).Ok); // second call must reuse, not fail

        scanner.Close("127.0.0.1", port);
        Assert.True((await scanner.ReadAsync(target, default)).Ok); // must reconnect cleanly after Close
    }

    [Fact]
    public async Task Failed_Read_Reports_Error_Instead_Of_Throwing()
    {
        using var scanner = new ModbusScanner();
        var deadPort = TestUtil.GetFreePort(); // nothing listening
        var target = new ScanTarget("127.0.0.1", deadPort, 1, ModbusArea.HoldingRegister, 0, 1, TagDataType.UInt16, WordOrder.ABCD, TimeoutMs: 300);

        var result = await scanner.ReadAsync(target, default);
        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
    }

    // Span<short> can't live in an async method body (CS4012) — keep the seeding synchronous.
    private static void SeedRegisters(ModbusTcpServer plc)
    {
        lock (plc.Lock)
        {
            var hr = plc.GetHoldingRegisters(1);
            hr[100] = 1234;
            var regs = ModbusCodec.Encode(3.14159, TagDataType.Float64, WordOrder.ABCD, 4);
            for (int i = 0; i < regs.Length; i++) hr[200 + i] = unchecked((short)regs[i]);
        }
    }
}
