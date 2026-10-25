using AstraLauncher.Core.Services;

namespace AstraLauncher.Infrastructure.FileSystem;

public sealed class LauncherPaths : ILauncherPaths
{
    public LauncherPaths(string? applicationDataDirectory = null)
    {
        ApplicationDataDirectory = applicationDataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AstraLauncher");
        LogsDirectory = Path.Combine(ApplicationDataDirectory, "logs");
        InstancesDirectory = Path.Combine(ApplicationDataDirectory, "instances");
    }

    public string ApplicationDataDirectory { get; }
    public string LogsDirectory { get; }
    public string InstancesDirectory { get; }
}
