namespace PGW.Drivers.OpcUa;

public enum OpcUaAuthMode { Anonymous, UsernamePassword, Certificate }

public sealed record OpcUaDeviceSettings(
    string EndpointUrl,
    string SecurityPolicy = "None",
    string SecurityMode = "None",
    OpcUaAuthMode AuthMode = OpcUaAuthMode.Anonymous,
    string? Username = null,
    string? Password = null,
    int PublishingIntervalMs = 500,
    int SamplingIntervalMs = 500,
    uint QueueSize = 10,
    bool DiscardOldest = true,
    int SessionTimeoutMs = 60000,
    int KeepAliveIntervalMs = 5000,
    int ReconnectMinMs = 1000,
    int ReconnectMaxMs = 30000,
    bool AutoAcceptUntrustedCertificates = false,
    string CertsPath = "certs");
