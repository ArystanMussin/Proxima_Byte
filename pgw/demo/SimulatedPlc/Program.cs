using System.Net;
using FluentModbus;

// A stand-in Modbus TCP device for `demo/project.yaml`, purely so PGW has something to poll
// without needing a real PLC (§13 style setup, but for a look-and-feel demo, not a test).
var server = new ModbusTcpServer();
server.Start(new IPEndPoint(IPAddress.Loopback, 15020));
server.AddUnit(1);
Seed(server);

var rnd = new Random();
_ = Task.Run(async () =>
{
    while (true)
    {
        await Task.Delay(700);
        Wiggle(server, rnd);
    }
});

Console.WriteLine("Simulated PLC listening on 127.0.0.1:15020 (unit 1). Ctrl+C to stop.");
await Task.Delay(-1);

static void Seed(ModbusTcpServer server)
{
    lock (server.Lock)
    {
        var hr = server.GetHoldingRegisters(1);
        hr[100] = 250; hr[102] = 505; hr[104] = 700;
        var di = server.GetDiscreteInputs(1);
        di[0] |= 1;
    }
}

static void Wiggle(ModbusTcpServer server, Random rnd)
{
    lock (server.Lock)
    {
        var hr = server.GetHoldingRegisters(1);
        hr[100] = (short)(240 + rnd.Next(0, 20));
        hr[102] = (short)(500 + rnd.Next(0, 10));
    }
}
