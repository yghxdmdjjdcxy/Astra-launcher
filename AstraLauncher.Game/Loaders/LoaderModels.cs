namespace AstraLauncher.Game.Loaders;

/// <summary>Supported mod loader families.</summary>
public enum LoaderKind
{
    Fabric,
    Quilt,
    Forge,
    NeoForge,
}

/// <summary>A loader build offered for a specific Minecraft version.</summary>
public sealed record LoaderVersion(string Version, string DisplayName, bool IsRecommended = false);

/// <summary>Progress reported while installing a loader.</summary>
public sealed record LoaderInstallProgress(string Stage, string Message, double? Percent = null);

/// <summary>The result of installing a loader profile.</summary>
public sealed record LoaderInstallResult(string VersionId, string MinecraftVersion, LoaderKind LoaderKind, string LoaderVersion);

/// <summary>Lists loader builds available for a Minecraft version.</summary>
public interface ILoaderCatalog
{
    Task<IReadOnlyList<LoaderVersion>> GetLoaderVersionsAsync(LoaderKind kind, string minecraftVersion, CancellationToken cancellationToken = default);
}

/// <summary>Installs Fabric/Quilt loader profiles (JSON based, no external installer).</summary>
public interface IFabricLikeLoaderInstaller
{
    Task<LoaderInstallResult> InstallAsync(LoaderKind kind, string minecraftVersion, string loaderVersion, IProgress<LoaderInstallProgress>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>Installs Forge/NeoForge by running their official installer jar.</summary>
public interface IForgeLikeLoaderInstaller
{
    Task<LoaderInstallResult> InstallAsync(LoaderKind kind, string minecraftVersion, string loaderVersion, IProgress<LoaderInstallProgress>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>Runs an external executable (used for the Forge/NeoForge installer jars).</summary>
public interface IExternalProcessRunner
{
    Task<int> RunAsync(string executablePath, IReadOnlyList<string> arguments, string workingDirectory, Action<string>? onOutput, CancellationToken cancellationToken = default);
}

/// <summary>Unified loader installation entry point used by the UI.</summary>
public interface ILoaderInstallService
{
    Task<LoaderInstallResult> InstallAsync(LoaderKind kind, string minecraftVersion, string loaderVersion, IProgress<LoaderInstallProgress>? progress = null, CancellationToken cancellationToken = default);
}
