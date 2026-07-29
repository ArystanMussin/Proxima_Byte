using PGW.Core;

namespace PGW.Drivers.OpcUa;

/// <summary>Builds an OPC UA client driver instance + tag definitions from a YAML `sources:` entry (§5, §7.2).</summary>
public static class OpcUaSourceFactory
{
    public const string TypeId = "opcua_client";

    public static (IProtocolDriver Driver, DeviceHandle Device, List<(TagDefinition Def, TagAddress Addr)> Tags) Build(
        SourceConfig src, RingLog log, IPathProvider paths)
    {
        var s = src.Settings;
        var security = s.GetMap("security") ?? new();
        var subscription = s.GetMap("subscription") ?? new();

        var authMode = security.GetStr("auth", "anonymous").ToLowerInvariant() switch
        {
            "username" or "username_password" => OpcUaAuthMode.UsernamePassword,
            "certificate" => OpcUaAuthMode.Certificate,
            _ => OpcUaAuthMode.Anonymous,
        };

        string? password = null;
        if (security.TryGetValue("password_env", out var envKeyObj) && envKeyObj is string envName)
            password = Environment.GetEnvironmentVariable(envName);
        else if (security.TryGetValue("password", out var pv) && pv is not null)
            password = pv.ToString();

        var publishingIntervalMs = subscription.GetInt("publishing_interval_ms", 500);

        var cfg = new OpcUaDeviceSettings(
            EndpointUrl: s.GetStr("endpoint"),
            SecurityPolicy: security.GetStr("policy", "None"),
            SecurityMode: security.GetStr("mode", "None"),
            AuthMode: authMode,
            Username: authMode == OpcUaAuthMode.UsernamePassword ? security.GetStr("username") : null,
            Password: password,
            PublishingIntervalMs: publishingIntervalMs,
            SamplingIntervalMs: subscription.GetInt("sampling_interval_ms", publishingIntervalMs),
            QueueSize: (uint)subscription.GetInt("queue_size", 10),
            DiscardOldest: subscription.GetBool("discard_oldest", true),
            SessionTimeoutMs: s.GetInt("session_timeout_ms", 60000),
            KeepAliveIntervalMs: s.GetInt("keep_alive_interval_ms", 5000),
            AutoAcceptUntrustedCertificates: security.GetBool("autoaccept", false),
            CertsPath: Path.Combine(paths.DataDir, "certs", src.Name));

        var device = new DeviceHandle(src.Name, src.Name);
        var deadband = subscription.GetMap("deadband")?.GetDouble("value") ?? 0;

        var tags = new Dictionary<string, OpcUaTagInfo>();
        var defs = new List<(TagDefinition, TagAddress)>();

        foreach (var t in src.Tags)
        {
            var name = t.GetStr("name");
            var tagId = $"{src.Name}.{name}";
            var nodeId = t.GetStr("node_id");
            var type = Enum.Parse<TagDataType>(t.GetStr("type", "float64"), ignoreCase: true);
            var access = t.GetStr("access", "RO").Equals("RW", StringComparison.OrdinalIgnoreCase) ? TagAccess.RW : TagAccess.RO;

            var def = new TagDefinition(
                Id: tagId,
                DataType: type,
                Access: access,
                OwnerDevice: device,
                NativeAddress: nodeId,
                ScanRateMs: publishingIntervalMs,
                Deadband: deadband,
                Description: OrNull(t.GetStr("description")),
                Units: OrNull(t.GetStr("units")));

            tags[tagId] = new OpcUaTagInfo(nodeId, type);
            defs.Add((def, new TagAddress(device, nodeId)));
        }

        var driver = new OpcUaClientDriver(cfg, tags, log);
        return (driver, device, defs);
    }

    private static string? OrNull(string s) => s.Length == 0 ? null : s;
}
