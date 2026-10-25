using AstraLauncher.Core.Models;

namespace AstraLauncher.Core.Services;

/// <summary>Creates install plans and downloads every file required to launch a Minecraft version.</summary>
public interface IVersionInstallService
{
    /// <summary>
    /// Builds the file plan for a version. May download the version metadata and asset index as a side effect
    /// so the resulting plan is complete.
    /// </summary>
    Task<VersionInstallPlan> CreatePlanAsync(string versionId, CancellationToken cancellationToken = default);

    /// <summary>Ensures every file of the version is present and verified, reporting aggregated progress.</summary>
    Task InstallAsync(string versionId, IProgress<VersionInstallProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Returns true when the version metadata and client jar files are already present.</summary>
    Task<bool> IsVersionInstalledAsync(string versionId, CancellationToken cancellationToken = default);
}
