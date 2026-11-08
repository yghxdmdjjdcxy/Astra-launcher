using System.IO;
using System.Net.Http;
using System.Text.Json;
using AstraLauncher.Core.Models;
using AstraLauncher.Core.Services;
using AstraLauncher.Plugin.Abstractions;
using AstraLauncher.Plugins;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AstraLauncher.App.Services;

/// <summary>
/// Checks the GitHub plugin index on startup and stages newer plugin ZIPs into <c>plugins/.updates</c>.
/// Updates are applied by <see cref="PluginUpdateApplier"/> before the next plugin load.
/// </summary>
public sealed class PluginAutoUpdateWorker : BackgroundService
{
    private readonly IPluginMarketplaceService _marketplace;
    private readonly ISettingsService _settings;
    private readonly ILauncherPaths _paths;
    private readonly ILogger<PluginAutoUpdateWorker> _logger;

    public PluginAutoUpdateWorker(
        IPluginMarketplaceService marketplace,
        ISettingsService settings,
        ILauncherPaths paths,
        ILogger<PluginAutoUpdateWorker> logger)
    {
        ArgumentNullException.ThrowIfNull(marketplace);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(logger);
        _marketplace = marketplace;
        _settings = settings;
        _paths = paths;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken).ConfigureAwait(false);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                LauncherSettings settings = await _settings.GetAsync(stoppingToken).ConfigureAwait(false);
                if (!settings.PluginAutoUpdate)
                {
                    return;
                }

                await CheckAndStageAsync(stoppingToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Plugin auto-update check failed.");
                return;
            }
        }
    }

    private async Task CheckAndStageAsync(CancellationToken cancellationToken)
    {
        string root = Path.Combine(_paths.ApplicationDataDirectory, "plugins");
        if (!Directory.Exists(root))
        {
            return;
        }

        IReadOnlyList<PluginMarketEntry> catalog = await _marketplace.GetCatalogAsync(cancellationToken).ConfigureAwait(false);
        if (catalog.Count == 0)
        {
            return;
        }

        foreach (string directory in Directory.EnumerateDirectories(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string manifestPath = Path.Combine(directory, PluginCatalog.ManifestFileName);
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            try
            {
                PluginManifest? manifest = JsonSerializer.Deserialize<PluginManifest>(
                    await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false),
                    PluginApi.ManifestJsonOptions);
                if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id))
                {
                    continue;
                }

                PluginMarketEntry? entry = await _marketplace.FindUpdateAsync(manifest, cancellationToken).ConfigureAwait(false);
                if (entry is null)
                {
                    continue;
                }

                _logger.LogInformation("Staging plugin update {Id} {Old} -> {New}.", manifest.Id, manifest.Version, entry.Version);
                await _marketplace.StageUpdateAsync(entry, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or HttpRequestException)
            {
                _logger.LogWarning(exception, "Cannot stage plugin update from {Directory}.", directory);
            }
        }
    }
}
