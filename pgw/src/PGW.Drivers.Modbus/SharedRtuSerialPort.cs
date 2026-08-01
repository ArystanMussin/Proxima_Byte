using FluentModbus;
using PGW.Core;

namespace PGW.Drivers.Modbus;

/// <summary>
/// Adapts a shared <see cref="SerialTransport"/> (§4.1.1) to FluentModbus's <see cref="IModbusRtuSerialPort"/>
/// so a <c>ModbusRtuClient</c> can be handed a physical port it doesn't own via
/// <c>ModbusRtuClient.Initialize(port, endianness)</c>, instead of opening its own dedicated
/// <see cref="System.IO.Ports.SerialPort"/> via <c>Connect(portName)</c>. Bus-wide mutual exclusion for a
/// whole request/response transaction is handled one level up, around each FluentModbus call in
/// <see cref="ModbusClientDriverBase"/> — this adapter itself is just a byte-level pass-through.
/// </summary>
internal sealed class SharedRtuSerialPort(SerialTransport transport) : IModbusRtuSerialPort
{
    public string PortName => transport.PortName;
    public bool IsOpen => transport.IsOpen;

    public void Open() => transport.EnsureOpen();

    // Never close a port shared with other devices just because one of them disconnected/reconnected —
    // it stays open for the gateway's lifetime, torn down only when the whole engine stops.
    public void Close() { }

    public int Read(byte[] buffer, int offset, int count) => transport.BaseStream.Read(buffer, offset, count);

    public Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        transport.BaseStream.ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public void Write(byte[] buffer, int offset, int count) => transport.BaseStream.Write(buffer, offset, count);

    public Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        transport.BaseStream.WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
}
