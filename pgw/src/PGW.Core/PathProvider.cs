namespace PGW.Core;

public interface IPathProvider
{
    string ConfigDir { get; }
    string DataDir { get; }
    string LogsDir { get; }
}

/// <summary>Cross-platform data locations per §11.1: Linux FHS paths, Windows ProgramData.</summary>
public sealed class PathProvider : IPathProvider
{
    public string ConfigDir { get; }
    public string DataDir { get; }
    public string LogsDir { get; }

    public PathProvider()
    {
        if (OperatingSystem.IsWindows())
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PGW");
            ConfigDir = Path.Combine(root, "config");
            DataDir = Path.Combine(root, "data");
            LogsDir = Path.Combine(root, "logs");
        }
        else
        {
            ConfigDir = "/etc/pgw";
            DataDir = "/var/lib/pgw";
            LogsDir = "/var/log/pgw";
        }
    }
}
