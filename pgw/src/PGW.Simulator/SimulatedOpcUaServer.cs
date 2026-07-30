using Opc.Ua;
using Opc.Ua.Configuration;
using Opc.Ua.Server;

namespace PGW.Simulator;

/// <summary>
/// Minimal OPC UA server exposing a few simulated variables — lets `pgw simulate` exercise the real
/// <c>OpcUaClientDriver</c> end to end without a real OPC UA server. No production ambitions: unsecured
/// endpoint only, anonymous auth, self-signed cert auto-generated on first run.
/// </summary>
public sealed class SimulatedOpcUaServer : IDisposable
{
    private sealed class InnerServer : StandardServer
    {
        public SimulatorNodeManager? NodeManager;

        protected override MasterNodeManager CreateMasterNodeManager(IServerInternal server, ApplicationConfiguration configuration)
        {
            NodeManager = new SimulatorNodeManager(server, configuration);
            var nodeManagers = new List<INodeManager> { NodeManager };
            return new MasterNodeManager(server, configuration, null, nodeManagers.ToArray());
        }
    }

    private InnerServer? _server;
    private readonly CancellationTokenSource _cts = new();
    private Task? _wiggleLoop;

    public async Task StartAsync(int port, string certsPath, CancellationToken ct)
    {
        var app = new ApplicationInstance
        {
            ApplicationName = "PGW-Simulator",
            ApplicationType = ApplicationType.Server,
        };

        var config = await app.Build("urn:pgw:simulator", "uri:pgw:simulator")
            .AsServer(new[] { $"opc.tcp://0.0.0.0:{port}/pgw/simulator" }, Array.Empty<string>())
            .AddUnsecurePolicyNone(true)
            .AddUserTokenPolicy(UserTokenType.Anonymous)
            .AddSecurityConfiguration("CN=PGW-Simulator, O=PGW", certsPath)
            .SetAutoAcceptUntrustedCertificates(true)
            .CreateAsync(ct);

        app.ApplicationConfiguration = config;
        await app.CheckApplicationInstanceCertificatesAsync(false, null, ct);

        _server = new InnerServer();
        await app.StartAsync(_server);

        _wiggleLoop = RunWiggleLoopAsync(_cts.Token);
    }

    private async Task RunWiggleLoopAsync(CancellationToken ct)
    {
        var rnd = new Random();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(700, ct);
                _server?.NodeManager?.Wiggle(rnd);
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _server?.Stop();
        _server?.Dispose();
    }
}
