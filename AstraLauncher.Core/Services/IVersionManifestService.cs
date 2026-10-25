using AstraLauncher.Core.Models;

namespace AstraLauncher.Core.Services;

/// <summary>Fetches and caches the Mojang version manifest via the configured download sources.</summary>
public interface IVersionManifestService
{
    /// <summary>Returns the manifest, refreshing the cached copy when it is stale or <paramref name="forceRefresh"/> is set.</summary>
    Task<VersionManifest> GetManifestAsync(bool forceRefresh = false, CancellationToken cancellationToken = default);

    /// <summary>Returns release versions (and optionally snapshots) ready for display.</summary>
    Task<IReadOnlyList<VersionSummary>> GetVersionsAsync(bool includeSnapshots, CancellationToken cancellationToken = default);

    /// <summary>Returns the manifest entry for a single version id.</summary>
    Task<VersionSummary> GetVersionSummaryAsync(string versionId, CancellationToken cancellationToken = default);
}
