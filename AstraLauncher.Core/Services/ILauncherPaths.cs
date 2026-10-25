namespace AstraLauncher.Core.Services;

/// <summary>Exposes every directory owned by the launcher with stable, platform-neutral names.</summary>
public interface ILauncherPaths
{
    string ApplicationDataDirectory { get; }
    string LogsDirectory { get; }
    string InstancesDirectory { get; }
    string VersionsDirectory { get; }
    string LibrariesDirectory { get; }
    string AssetsDirectory { get; }
    string RuntimeDirectory { get; }
    string CacheDirectory { get; }
    string TempDirectory { get; }

    /// <summary>Creates all launcher directories. The operation is idempotent and safe to call repeatedly.</summary>
    void EnsureCreated();
}
