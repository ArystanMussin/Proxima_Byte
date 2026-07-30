using System.Net;
using FluentModbus;

namespace PGW.Simulator;

/// <summary>
/// Self-contained Modbus TCP device with a handful of oscillating registers — lets `pgw simulate`
/// exercise the real <c>ModbusTcpClientDriver</c> and a SCADA client without any real PLC.
/// </summary>
public sealed class SimulatedModbusServer : IDisposable
{
    private readonly ModbusTcpServer _server = new();
    private readonly CancellationTokenSource _cts = new();
    private byte _unitId;
    private Task? _loop;

    public void Start(string bind, int port, byte unitId = 1)
    {
        _unitId = unitId;
        _server.Start(new IPEndPoint(IPAddress.Parse(bind), port));
        _server.AddUnit(unitId);
        Seed();
        _loop = RunAsync(_cts.Token);
    }

    private void Seed()
    {
        lock (_server.Lock)
        {
            var hr = _server.GetHoldingRegisters(_unitId);
            hr[100] = 250; hr[102] = 505; hr[104] = 700; // T1_supply, P1_supply, setpoint
            var di = _server.GetDiscreteInputs(_unitId);
            di[0] |= 1; // pump1_run
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var rnd = new Random();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(700, ct);
                Wiggle(rnd);
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Wiggle(Random rnd)
    {
        lock (_server.Lock)
        {
            var hr = _server.GetHoldingRegisters(_unitId);
            hr[100] = (short)(240 + rnd.Next(0, 20));
            hr[102] = (short)(500 + rnd.Next(0, 10));
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _server.Stop();
        _server.Dispose();
    }
}
