namespace AstraLauncher.Core.Services;

public interface ILauncherPaths
{
    string ApplicationDataDirectory { get; }
    string LogsDirectory { get; }
    string InstancesDirectory { get; }
}
