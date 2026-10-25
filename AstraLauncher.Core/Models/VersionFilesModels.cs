namespace AstraLauncher.Core.Models;

/// <summary>The latest release/snapshot identifiers reported by the Mojang version manifest.</summary>
public sealed record VersionManifestLatest(string Release, string Snapshot);

/// <summary>One entry of the Mojang version manifest.</summary>
public sealed record VersionSummary(string Id, string Type, string Url, DateTimeOffset Time, DateTimeOffset ReleaseTime, string? Sha1);

/// <summary>The Mojang version manifest document.</summary>
public sealed record VersionManifest(VersionManifestLatest Latest, IReadOnlyList<VersionSummary> Versions);

/// <summary>One file required to install a game version.</summary>
public sealed record InstallFile(string Url, string DestinationPath, string? Sha1, long? Size, string Category);

/// <summary>The complete set of files required to install a game version.</summary>
public sealed record VersionInstallPlan(string VersionId, IReadOnlyList<InstallFile> Files, long KnownTotalBytes);

/// <summary>Progress information for a running version installation.</summary>
public sealed record VersionInstallProgress(int TotalFiles, int CompletedFiles, long TotalBytes, long DownloadedBytes, string Stage, string? CurrentFile);
