using AstraLauncher.Core.Models;

namespace AstraLauncher.Core.Services;

/// <summary>Loads and persists <see cref="LauncherSettings"/> and notifies observers about successful saves.</summary>
public interface ISettingsService
{
    /// <summary>Raised after settings were successfully persisted; the argument is the normalized settings instance.</summary>
    event EventHandler<LauncherSettings>? Changed;

    /// <summary>Returns the current settings, loading them from disk on first use.</summary>
    Task<LauncherSettings> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Normalizes and persists the supplied settings, replacing any previous document atomically.</summary>
    Task SaveAsync(LauncherSettings settings, CancellationToken cancellationToken = default);
}
