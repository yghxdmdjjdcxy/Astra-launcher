using AstraLauncher.Core.Models;

namespace AstraLauncher.Game.Mods;

/// <summary>Loader family detected from a mod's metadata.</summary>
public enum ModPlatform { Unknown, Fabric, Quilt, Forge, NeoForge }

/// <summary>A dependency declared by a mod.</summary>
public sealed record ModDependency(string ModId, string? VersionRange = null, bool Mandatory = true);

/// <summary>A mod jar found in an instance's mods directory.</summary>
public sealed record LocalMod(
    string FilePath,
    string FileName,
    string ModId,
    string DisplayName,
    string? Version,
    ModPlatform Platform,
    bool IsEnabled,
    long SizeBytes,
    IReadOnlyList<ModDependency> Dependencies)
{
    /// <summary>Gets the id and version in the form used for display.</summary>
    public string DisplayVersion => string.IsNullOrWhiteSpace(Version) ? ModId : $"{ModId} {Version}";
}

/// <summary>Result of importing one or more local jar files.</summary>
public sealed record ModImportResult(IReadOnlyList<LocalMod> Imported, IReadOnlyList<string> Errors);

/// <summary>Lists, imports, enables/disables and deletes mods inside an instance's mods directory.</summary>
public interface ILocalModService
{
    /// <summary>Returns the mods directory of the instance (it is not required to exist yet).</summary>
    string GetModsDirectory(GameInstance instance);

    Task<IReadOnlyList<LocalMod>> ListAsync(GameInstance instance, CancellationToken cancellationToken = default);

    Task<ModImportResult> ImportAsync(GameInstance instance, IReadOnlyList<string> sourceFilePaths, CancellationToken cancellationToken = default);

    /// <summary>Renames the jar between <c>.jar</c> and <c>.jar.disabled</c>; returns the updated entry.</summary>
    Task<LocalMod> SetEnabledAsync(GameInstance instance, LocalMod mod, bool enabled, CancellationToken cancellationToken = default);

    Task DeleteAsync(GameInstance instance, LocalMod mod, CancellationToken cancellationToken = default);
}

/// <summary>One project returned by the Modrinth search endpoint.</summary>
public sealed record ModrinthSearchHit(
    string ProjectId,
    string Slug,
    string Title,
    string Description,
    string? IconUrl,
    long Downloads,
    IReadOnlyList<string> Categories);

public sealed record ModrinthSearchResult(IReadOnlyList<ModrinthSearchHit> Hits, int TotalHits);

/// <summary>A downloadable file of a Modrinth project version.</summary>
public sealed record ModrinthVersionFile(string FileName, Uri Url, long Size, string Sha1, bool IsPrimary);

/// <summary>A Modrinth project version filtered by game version and loader.</summary>
public sealed record ModrinthVersion(
    string VersionId,
    string ProjectId,
    string Name,
    string VersionNumber,
    IReadOnlyList<string> GameVersions,
    IReadOnlyList<string> Loaders,
    IReadOnlyList<ModrinthVersionFile> Files);

public sealed record ModrinthDownloadProgress(string Stage, double? Percent = null);

/// <summary>Queries Modrinth's public API and installs compatible mod files into an instance.</summary>
public interface IModrinthClient
{
    Task<ModrinthSearchResult> SearchAsync(string query, string gameVersion, string loader, int offset = 0, int limit = 20, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ModrinthVersion>> GetVersionsAsync(string projectId, string gameVersion, string loader, CancellationToken cancellationToken = default);

    /// <summary>Downloads the primary file of the version into the mods directory and returns the installed path.</summary>
    Task<string> DownloadAsync(ModrinthVersion version, string modsDirectory, IProgress<ModrinthDownloadProgress>? progress = null, CancellationToken cancellationToken = default);
}
