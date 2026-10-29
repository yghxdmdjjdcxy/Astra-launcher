using Microsoft.Extensions.Logging;

namespace AstraLauncher.Game.Loaders;

/// <summary>Dispatches loader installation to the JSON-based (Fabric/Quilt) or external-installer (Forge/NeoForge) path.</summary>
public sealed class LoaderInstallService : ILoaderInstallService
{
    private readonly IFabricLikeLoaderInstaller _fabricLike;
    private readonly IForgeLikeLoaderInstaller _forgeLike;
    private readonly ILogger<LoaderInstallService> _logger;

    public LoaderInstallService(IFabricLikeLoaderInstaller fabricLike, IForgeLikeLoaderInstaller forgeLike, ILogger<LoaderInstallService> logger)
    {
        ArgumentNullException.ThrowIfNull(fabricLike);
        ArgumentNullException.ThrowIfNull(forgeLike);
        ArgumentNullException.ThrowIfNull(logger);
        _fabricLike = fabricLike;
        _forgeLike = forgeLike;
        _logger = logger;
    }

    public Task<LoaderInstallResult> InstallAsync(
        LoaderKind kind,
        string minecraftVersion,
        string loaderVersion,
        IProgress<LoaderInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(minecraftVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(loaderVersion);
        _logger.LogInformation("Installing {LoaderKind} {LoaderVersion} for Minecraft {MinecraftVersion}.", kind, loaderVersion, minecraftVersion);
        return kind switch
        {
            LoaderKind.Fabric or LoaderKind.Quilt => _fabricLike.InstallAsync(kind, minecraftVersion, loaderVersion, progress, cancellationToken),
            LoaderKind.Forge or LoaderKind.NeoForge => _forgeLike.InstallAsync(kind, minecraftVersion, loaderVersion, progress, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown loader kind."),
        };
    }
}
