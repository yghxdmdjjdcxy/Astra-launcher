using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AstraLauncher.Core.Models;
using AstraLauncher.Core.Services;
using Microsoft.Extensions.Logging;

namespace AstraLauncher.Infrastructure.Versioning;

/// <summary>
/// Downloads the Mojang version manifest through the configured download sources, validates it and keeps a
/// thirty-minute disk cache that is reused when the network or every configured source is unavailable.
/// </summary>
public sealed class VersionManifestService : IVersionManifestService
{
    private const string CacheFileName = "version_manifest_v2.json";
    private const string CacheTimestampFileName = "version_manifest_v2.fetched";
    private const string ReleaseVersionType = "release";
    private const string SnapshotVersionType = "snapshot";
    private const string OfficialManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

    private static readonly Uri OfficialManifestUri = new(OfficialManifestUrl, UriKind.Absolute);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);
    private static readonly JsonSerializerOptions ManifestJsonOptions = new() { AllowTrailingCommas = true };

    private readonly IDownloadSourceResolver _sources;
    private readonly ISettingsService _settings;
    private readonly HttpClient _httpClient;
    private readonly ILogger<VersionManifestService> _logger;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly string _cacheDirectory;
    private readonly string _cacheFilePath;
    private readonly string _cacheTimestampPath;

    private CacheEntry? _cache;

    /// <summary>Creates the service. The caller keeps ownership of <paramref name="httpClient"/>.</summary>
    public VersionManifestService(
        ILauncherPaths paths,
        IDownloadSourceResolver sources,
        ISettingsService settings,
        HttpClient httpClient,
        ILogger<VersionManifestService> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);

        _sources = sources;
        _settings = settings;
        _httpClient = httpClient;
        _logger = logger;
        _cacheDirectory = paths.CacheDirectory;
        _cacheFilePath = Path.Combine(_cacheDirectory, CacheFileName);
        _cacheTimestampPath = Path.Combine(_cacheDirectory, CacheTimestampFileName);
    }

    /// <inheritdoc />
    public async Task<VersionManifest> GetManifestAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        CacheEntry? cached = Volatile.Read(ref _cache);
        if (!forceRefresh && cached is not null && IsFresh(cached.FetchedAt))
        {
            return cached.Manifest;
        }

        await _loadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cached = Volatile.Read(ref _cache);
            if (!forceRefresh && cached is not null && IsFresh(cached.FetchedAt))
            {
                return cached.Manifest;
            }

            return await RefreshAsync(forceRefresh, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _loadLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<VersionSummary>> GetVersionsAsync(bool includeSnapshots, CancellationToken cancellationToken = default)
    {
        VersionManifest manifest = await GetManifestAsync(forceRefresh: false, cancellationToken).ConfigureAwait(false);
        VersionSummary[] versions = manifest.Versions
            .Where(version => IsListedVersion(version, includeSnapshots))
            .OrderByDescending(version => version.ReleaseTime)
            .ToArray();
        return versions;
    }

    /// <inheritdoc />
    public async Task<VersionSummary> GetVersionSummaryAsync(string versionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);

        VersionManifest manifest = await GetManifestAsync(forceRefresh: false, cancellationToken).ConfigureAwait(false);
        foreach (VersionSummary version in manifest.Versions)
        {
            if (string.Equals(version.Id, versionId, StringComparison.Ordinal))
            {
                return version;
            }
        }

        throw new KeyNotFoundException($"The version manifest does not contain version '{versionId}'.");
    }

    private async Task<VersionManifest> RefreshAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        CacheEntry? diskCache = await TryReadCacheAsync(cancellationToken).ConfigureAwait(false);
        if (!forceRefresh && diskCache is not null && IsFresh(diskCache.FetchedAt))
        {
            Volatile.Write(ref _cache, diskCache);
            _logger.LogDebug("Version manifest cache is fresh; loaded it from {CachePath}.", _cacheFilePath);
            return diskCache.Manifest;
        }

        try
        {
            (VersionManifest manifest, string json, Uri sourceUri) = await FetchAsync(cancellationToken).ConfigureAwait(false);
            DateTimeOffset fetchedAt = DateTimeOffset.UtcNow;
            await WriteCacheAsync(json, fetchedAt, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _cache, new CacheEntry(manifest, fetchedAt));
            _logger.LogInformation(
                "Version manifest refreshed from {SourceUri} with {VersionCount} version(s).",
                sourceUri,
                manifest.Versions.Count);
            return manifest;
        }
        catch (InvalidOperationException exception) when (diskCache is not null)
        {
            return UseCachedManifest(exception, diskCache);
        }
        catch (InvalidDataException exception) when (diskCache is not null)
        {
            return UseCachedManifest(exception, diskCache);
        }
        catch (FormatException exception) when (diskCache is not null)
        {
            return UseCachedManifest(exception, diskCache);
        }
        catch (InvalidOperationException exception)
        {
            LogRefreshFailure(exception);
            throw;
        }
        catch (InvalidDataException exception)
        {
            LogRefreshFailure(exception);
            throw;
        }
        catch (FormatException exception)
        {
            LogRefreshFailure(exception);
            throw;
        }
    }

    private async Task<(VersionManifest Manifest, string Json, Uri SourceUri)> FetchAsync(CancellationToken cancellationToken)
    {
        LauncherSettings settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<Uri> candidates = _sources.GetCandidates(OfficialManifestUri, settings.DownloadSourceMode);
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                $"No download source is configured for the version manifest (mode: {settings.DownloadSourceMode}).");
        }

        Exception? lastError = null;
        foreach (Uri candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using HttpResponseMessage response = await _httpClient
                    .GetAsync(candidate, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    lastError = new HttpRequestException(
                        $"The version manifest request to {candidate} returned HTTP {(int)response.StatusCode} ({response.StatusCode}).",
                        inner: null,
                        response.StatusCode);
                    _logger.LogDebug(lastError, "Version manifest candidate {SourceUri} returned HTTP {StatusCode}.", candidate, response.StatusCode);
                    continue;
                }

                string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                VersionManifest manifest = ParseManifest(json);
                return (manifest, json, candidate);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                lastError = new TimeoutException($"The version manifest request to {candidate} timed out.", exception);
                _logger.LogDebug(lastError, "Version manifest candidate {SourceUri} timed out.", candidate);
            }
            catch (HttpRequestException exception)
            {
                lastError = exception;
                _logger.LogDebug(exception, "Version manifest candidate {SourceUri} failed.", candidate);
            }
            catch (IOException exception)
            {
                lastError = exception;
                _logger.LogDebug(exception, "Version manifest candidate {SourceUri} failed.", candidate);
            }
        }

        throw new InvalidOperationException(
            $"The version manifest could not be downloaded from any of the {candidates.Count} configured source(s).",
            lastError);
    }

    private VersionManifest UseCachedManifest(Exception exception, CacheEntry diskCache)
    {
        Volatile.Write(ref _cache, diskCache);
        _logger.LogWarning(
            exception,
            "Version manifest refresh failed; using the cached manifest fetched at {FetchedAt}.",
            diskCache.FetchedAt);
        return diskCache.Manifest;
    }

    private void LogRefreshFailure(Exception exception) =>
        _logger.LogWarning(exception, "Version manifest refresh failed and no cached manifest is available.");

    private async Task<CacheEntry?> TryReadCacheAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_cacheFilePath) || !File.Exists(_cacheTimestampPath))
        {
            return null;
        }

        try
        {
            string timestampText = await File.ReadAllTextAsync(_cacheTimestampPath, cancellationToken).ConfigureAwait(false);
            if (!TryParseFetchedAt(timestampText, out DateTimeOffset fetchedAt))
            {
                _logger.LogDebug("Version manifest cache timestamp {CacheTimestampPath} is invalid; the cache is treated as stale.", _cacheTimestampPath);
                return null;
            }

            string json = await File.ReadAllTextAsync(_cacheFilePath, cancellationToken).ConfigureAwait(false);
            return new CacheEntry(ParseManifest(json), fetchedAt);
        }
        catch (IOException exception)
        {
            _logger.LogWarning(exception, "The version manifest cache at {CachePath} could not be read; it is treated as stale.", _cacheFilePath);
            return null;
        }
        catch (UnauthorizedAccessException exception)
        {
            _logger.LogWarning(exception, "Access to the version manifest cache at {CachePath} was denied; it is treated as stale.", _cacheFilePath);
            return null;
        }
        catch (InvalidDataException exception)
        {
            _logger.LogDebug(exception, "The version manifest cache at {CachePath} is corrupt; it is treated as stale.", _cacheFilePath);
            return null;
        }
        catch (FormatException exception)
        {
            _logger.LogDebug(exception, "The version manifest cache at {CachePath} contains an invalid entry; it is treated as stale.", _cacheFilePath);
            return null;
        }
    }

    private async Task WriteCacheAsync(string manifestJson, DateTimeOffset fetchedAt, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            await WriteFileAtomicallyAsync(_cacheFilePath, manifestJson, cancellationToken).ConfigureAwait(false);
            await WriteFileAtomicallyAsync(
                _cacheTimestampPath,
                fetchedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                cancellationToken).ConfigureAwait(false);
        }
        catch (IOException exception)
        {
            _logger.LogWarning(exception, "The version manifest was downloaded but could not be cached at {CachePath}.", _cacheDirectory);
        }
        catch (UnauthorizedAccessException exception)
        {
            _logger.LogWarning(exception, "The version manifest was downloaded but the cache at {CachePath} is not writable.", _cacheDirectory);
        }
    }

    private static async Task WriteFileAtomicallyAsync(string path, string content, CancellationToken cancellationToken)
    {
        string temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, content, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup: every write uses a unique temporary name, so a stale file is harmless.
        }
        catch (UnauthorizedAccessException)
        {
            // Same as above: cleanup must never turn a successfully written cache into a failure.
        }
    }

    private static bool IsFresh(DateTimeOffset fetchedAt) => DateTimeOffset.UtcNow - fetchedAt <= CacheTtl;

    private static bool TryParseFetchedAt(string text, out DateTimeOffset fetchedAt) =>
        DateTimeOffset.TryParse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out fetchedAt);

    private static bool IsListedVersion(VersionSummary version, bool includeSnapshots) =>
        string.Equals(version.Type, ReleaseVersionType, StringComparison.Ordinal)
        || (includeSnapshots && string.Equals(version.Type, SnapshotVersionType, StringComparison.Ordinal));

    private static VersionManifest ParseManifest(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidDataException("The version manifest response is empty.");
        }

        ManifestDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<ManifestDocument>(json, ManifestJsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The version manifest response is not valid JSON.", exception);
        }

        if (document is null)
        {
            throw new InvalidDataException("The version manifest response does not contain a JSON document.");
        }

        ManifestLatestDocument latest = document.Latest
            ?? throw new InvalidDataException("The version manifest is missing the 'latest' section.");
        string release = RequireText(latest.Release, "latest.release");
        string snapshot = RequireText(latest.Snapshot, "latest.snapshot");

        List<VersionSummary> versions = new(document.Versions?.Count ?? 0);
        if (document.Versions is not null)
        {
            foreach (VersionDocument? item in document.Versions)
            {
                versions.Add(MapVersion(item));
            }
        }

        return new VersionManifest(new VersionManifestLatest(release, snapshot), versions);
    }

    private static VersionSummary MapVersion(VersionDocument? document)
    {
        if (document is null)
        {
            throw new InvalidDataException("The version manifest contains an empty version entry.");
        }

        string id = RequireText(document.Id, "versions[].id");
        string type = RequireText(document.Type, "versions[].type");
        string url = RequireText(document.Url, "versions[].url");
        if (!IsAbsoluteHttpUrl(url))
        {
            throw new FormatException($"Version '{id}' has an invalid metadata URL '{url}'.");
        }

        DateTimeOffset time = document.Time
            ?? throw new InvalidDataException($"Version '{id}' is missing the 'time' field.");
        DateTimeOffset releaseTime = document.ReleaseTime
            ?? throw new InvalidDataException($"Version '{id}' is missing the 'releaseTime' field.");
        return new VersionSummary(id, type, url, time, releaseTime, document.Sha1);
    }

    private static string RequireText(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"The version manifest field '{fieldName}' is missing or empty.");
        }

        return value;
    }

    private static bool IsAbsoluteHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
        && (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));

    private sealed record CacheEntry(VersionManifest Manifest, DateTimeOffset FetchedAt);

    private sealed class ManifestDocument
    {
        [JsonPropertyName("latest")]
        public ManifestLatestDocument? Latest { get; init; }

        [JsonPropertyName("versions")]
        public List<VersionDocument?>? Versions { get; init; }
    }

    private sealed class ManifestLatestDocument
    {
        [JsonPropertyName("release")]
        public string? Release { get; init; }

        [JsonPropertyName("snapshot")]
        public string? Snapshot { get; init; }
    }

    private sealed class VersionDocument
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("type")]
        public string? Type { get; init; }

        [JsonPropertyName("url")]
        public string? Url { get; init; }

        [JsonPropertyName("time")]
        public DateTimeOffset? Time { get; init; }

        [JsonPropertyName("releaseTime")]
        public DateTimeOffset? ReleaseTime { get; init; }

        [JsonPropertyName("sha1")]
        public string? Sha1 { get; init; }
    }
}
