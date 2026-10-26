namespace AstraLauncher.Core.Services;

/// <summary>Exposes every directory owned by the launcher with stable, platform-neutral names.</summary>
public interface ILauncherPaths
{
    /// <summary>Gets the launcher-private directory (settings, logs, instances, cache) under %LOCALAPPDATA%.</summary>
    string ApplicationDataDirectory { get; }

    string LogsDirectory { get; }

    /// <summary>Gets the directory that holds launcher-created instances (each with its own game directory).</summary>
    string InstancesDirectory { get; }

    /// <summary>Gets the resolved Minecraft root directory (normally a <c>.minecraft</c> folder).</summary>
    string MinecraftDirectory { get; }

    /// <summary>Gets <c>&lt;MinecraftDirectory&gt;\versions</c>, shared with other launchers.</summary>
    string VersionsDirectory { get; }

    /// <summary>Gets <c>&lt;MinecraftDirectory&gt;\libraries</c>, shared with other launchers.</summary>
    string LibrariesDirectory { get; }

    /// <summary>Gets <c>&lt;MinecraftDirectory&gt;\assets</c>, shared with other launchers.</summary>
    string AssetsDirectory { get; }

    string RuntimeDirectory { get; }
    string CacheDirectory { get; }
    string TempDirectory { get; }

    /// <summary>
    /// Switches the Minecraft root directory used for versions, libraries and assets.
    /// Launcher-private directories (settings/logs/instances) keep using <see cref="ApplicationDataDirectory"/>.
    /// </summary>
    void SetMinecraftDirectory(string path);

    /// <summary>Creates all launcher directories. The operation is idempotent and safe to call repeatedly.</summary>
    void EnsureCreated();
}
