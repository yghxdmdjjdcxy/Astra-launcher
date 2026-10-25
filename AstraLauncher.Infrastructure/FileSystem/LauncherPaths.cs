using AstraLauncher.Core.Services;

namespace AstraLauncher.Infrastructure.FileSystem;

/// <summary>Default <see cref="ILauncherPaths"/> implementation rooted at <c>%LOCALAPPDATA%\AstraLauncher</c> unless overridden.</summary>
public sealed class LauncherPaths : ILauncherPaths
{
    private const string DefaultDirectoryName = "AstraLauncher";

    public LauncherPaths(string? applicationDataDirectory = null)
    {
        ApplicationDataDirectory = applicationDataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), DefaultDirectoryName);
        LogsDirectory = Path.Combine(ApplicationDataDirectory, "logs");
        InstancesDirectory = Path.Combine(ApplicationDataDirectory, "instances");
        VersionsDirectory = Path.Combine(ApplicationDataDirectory, "versions");
        LibrariesDirectory = Path.Combine(ApplicationDataDirectory, "libraries");
        AssetsDirectory = Path.Combine(ApplicationDataDirectory, "assets");
        RuntimeDirectory = Path.Combine(ApplicationDataDirectory, "java-runtime");
        CacheDirectory = Path.Combine(ApplicationDataDirectory, "cache");
        TempDirectory = Path.Combine(ApplicationDataDirectory, "temp");
    }

    public string ApplicationDataDirectory { get; }
    public string LogsDirectory { get; }
    public string InstancesDirectory { get; }
    public string VersionsDirectory { get; }
    public string LibrariesDirectory { get; }
    public string AssetsDirectory { get; }
    public string RuntimeDirectory { get; }
    public string CacheDirectory { get; }
    public string TempDirectory { get; }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(ApplicationDataDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(InstancesDirectory);
        Directory.CreateDirectory(VersionsDirectory);
        Directory.CreateDirectory(LibrariesDirectory);
        Directory.CreateDirectory(AssetsDirectory);
        Directory.CreateDirectory(RuntimeDirectory);
        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(TempDirectory);
    }
}
