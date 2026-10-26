using AstraLauncher.Core.Services;

namespace AstraLauncher.Infrastructure.FileSystem;

/// <summary>
/// Default <see cref="ILauncherPaths"/> implementation.
/// Launcher-private data (settings, logs, instances, cache) lives under <c>%LOCALAPPDATA%\AstraLauncher</c>;
/// version/libraries/assets files live in a Minecraft root directory (normally a shared <c>.minecraft</c> folder)
/// so other launchers can reuse the same installation.
/// </summary>
public sealed class LauncherPaths : ILauncherPaths
{
    private const string DefaultDirectoryName = "AstraLauncher";
    private const string MinecraftDirectoryName = ".minecraft";

    private string _minecraftDirectory;
    private string _versionsDirectory;
    private string _librariesDirectory;
    private string _assetsDirectory;

    /// <summary>Initializes launcher paths.</summary>
    /// <param name="applicationDataDirectory">Launcher-private directory; defaults to <c>%LOCALAPPDATA%\AstraLauncher</c>.</param>
    /// <param name="minecraftDirectory">
    /// Minecraft root directory. When omitted it defaults to <paramref name="applicationDataDirectory"/> (legacy single-root layout),
    /// which keeps tests and custom layouts working; the application resolves a real <c>.minecraft</c> directory explicitly.
    /// </param>
    public LauncherPaths(string? applicationDataDirectory = null, string? minecraftDirectory = null)
    {
        ApplicationDataDirectory = applicationDataDirectory
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), DefaultDirectoryName);
        LogsDirectory = Path.Combine(ApplicationDataDirectory, "logs");
        InstancesDirectory = Path.Combine(ApplicationDataDirectory, "instances");
        RuntimeDirectory = Path.Combine(ApplicationDataDirectory, "java-runtime");
        CacheDirectory = Path.Combine(ApplicationDataDirectory, "cache");
        TempDirectory = Path.Combine(ApplicationDataDirectory, "temp");

        _minecraftDirectory = Path.GetFullPath(minecraftDirectory ?? ApplicationDataDirectory);
        _versionsDirectory = Path.Combine(_minecraftDirectory, "versions");
        _librariesDirectory = Path.Combine(_minecraftDirectory, "libraries");
        _assetsDirectory = Path.Combine(_minecraftDirectory, "assets");
    }

    public string ApplicationDataDirectory { get; }
    public string LogsDirectory { get; }
    public string InstancesDirectory { get; }
    public string MinecraftDirectory => _minecraftDirectory;
    public string VersionsDirectory => _versionsDirectory;
    public string LibrariesDirectory => _librariesDirectory;
    public string AssetsDirectory => _assetsDirectory;
    public string RuntimeDirectory { get; }
    public string CacheDirectory { get; }
    public string TempDirectory { get; }

    /// <summary>
    /// Resolves the default Minecraft root: an existing <c>.minecraft</c> beside the executable wins (portable mode),
    /// then an existing <c>%APPDATA%\.minecraft</c>; otherwise the portable folder is created when possible and
    /// <c>%APPDATA%\.minecraft</c> is used as the fallback for read-only installation directories.
    /// </summary>
    public static string ResolveDefaultMinecraftDirectory()
    {
        string portable = Path.Combine(AppContext.BaseDirectory, MinecraftDirectoryName);
        if (Directory.Exists(portable))
        {
            return portable;
        }

        string roaming = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), MinecraftDirectoryName);
        if (Directory.Exists(roaming))
        {
            return roaming;
        }

        try
        {
            Directory.CreateDirectory(portable);
            return portable;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return roaming;
        }
    }

    public void SetMinecraftDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path.Trim());
        _minecraftDirectory = fullPath;
        _versionsDirectory = Path.Combine(fullPath, "versions");
        _librariesDirectory = Path.Combine(fullPath, "libraries");
        _assetsDirectory = Path.Combine(fullPath, "assets");
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(ApplicationDataDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(InstancesDirectory);
        Directory.CreateDirectory(RuntimeDirectory);
        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(TempDirectory);
        Directory.CreateDirectory(MinecraftDirectory);
        Directory.CreateDirectory(VersionsDirectory);
        Directory.CreateDirectory(LibrariesDirectory);
        Directory.CreateDirectory(AssetsDirectory);
    }
}
