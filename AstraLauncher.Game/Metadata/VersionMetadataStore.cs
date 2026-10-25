using System.Security.Cryptography;
using AstraLauncher.Core.Models;
using AstraLauncher.Core.Services;
using Microsoft.Extensions.Logging;

namespace AstraLauncher.Game.Metadata;

/// <summary>
/// Makes the local metadata of a version complete before it is resolved: the version JSON and every JSON of its
/// inheritance chain are downloaded through the configured sources when missing, verified and stored atomically.
/// </summary>
public sealed class VersionMetadataStore
{
    private readonly ILauncherPaths _paths;
    private readonly IVersionManifestService _manifest;
    private readonly IDownloadSourceResolver _sources;
    private readonly ISettingsService _settings;
    private readonly HttpClient _httpClient;
    private readonly ILogger<VersionMetadataStore> _logger;
    private readonly SemaphoreSlim _resolutionGate = new(1, 1);

    public VersionMetadataStore(
        ILauncherPaths paths,
        IVersionManifestService manifest,
        IDownloadSourceResolver sources,
        ISettingsService settings,
        HttpClient httpClient,
        ILogger<VersionMetadataStore> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);
        _paths = paths;
        _manifest = manifest;
        _sources = sources;
        _settings = settings;
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <summary>
    /// Ensures <c>versions/&lt;id&gt;/&lt;id&gt;.json</c> exists for the version and all of its ancestors,
    /// downloading whatever is missing, then returns the merged metadata.
    /// </summary>
    public async Task<MinecraftVersionMetadata> EnsureResolvedAsync(string versionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);
        await _resolutionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureVersionFileAsync(versionId, new HashSet<string>(StringComparer.Ordinal), cancellationToken).ConfigureAwait(false);
            return await new VersionMetadataResolver(_paths.ApplicationDataDirectory).ResolveAsync(versionId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _resolutionGate.Release();
        }
    }

    private async Task EnsureVersionFileAsync(string versionId, HashSet<string> ancestry, CancellationToken cancellationToken)
    {
        ValidateVersionId(versionId);
        if (!ancestry.Add(versionId))
        {
            throw new VersionMetadataException($"Version inheritance contains a cycle at '{versionId}'.");
        }

        try
        {
            string metadataPath = Path.Combine(_paths.VersionsDirectory, versionId, $"{versionId}.json");
            MinecraftVersionMetadata local;
            if (File.Exists(metadataPath))
            {
                local = await ReadMetadataAsync(metadataPath, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                VersionSummary summary = await _manifest.GetVersionSummaryAsync(versionId, cancellationToken).ConfigureAwait(false);
                byte[] payload = await DownloadMetadataAsync(versionId, summary, cancellationToken).ConfigureAwait(false);
                // Parse before writing so a malformed document never leaves a broken version directory behind.
                local = await ParseMetadataAsync(payload, metadataPath, cancellationToken).ConfigureAwait(false);
                await WriteFileAtomicallyAsync(metadataPath, payload, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Stored metadata for version {VersionId} at {MetadataPath}.", versionId, metadataPath);
            }

            if (!string.IsNullOrWhiteSpace(local.InheritsFrom))
            {
                await EnsureVersionFileAsync(local.InheritsFrom, ancestry, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ancestry.Remove(versionId);
        }
    }

    private async Task<byte[]> DownloadMetadataAsync(string versionId, VersionSummary summary, CancellationToken cancellationToken)
    {
        Uri officialUri = ParseOfficialUri(versionId, summary.Url);
        LauncherSettings settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<Uri> candidates = _sources.GetCandidates(officialUri, settings.DownloadSourceMode);
        if (candidates.Count == 0)
        {
            _logger.LogDebug("No candidate sources for {VersionId}; falling back to the official URL.", versionId);
            candidates = [officialUri];
        }

        Exception? lastError = null;
        foreach (Uri candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                _logger.LogInformation("Downloading metadata for version {VersionId} from {Source}.", versionId, candidate);
                using HttpResponseMessage response = await _httpClient.GetAsync(candidate, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    lastError = new HttpRequestException($"GET {candidate} returned {(int)response.StatusCode} ({response.ReasonPhrase}).");
                    _logger.LogWarning("Metadata download for {VersionId} from {Source} returned HTTP {StatusCode}.", versionId, candidate, (int)response.StatusCode);
                    continue;
                }

                byte[] payload = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(summary.Sha1))
                {
                    string actual = Convert.ToHexString(SHA1.HashData(payload));
                    if (!actual.Equals(summary.Sha1, StringComparison.OrdinalIgnoreCase))
                    {
                        lastError = new IOException($"SHA-1 mismatch for version metadata '{versionId}' from '{candidate}' (expected {summary.Sha1}, got {actual}).");
                        _logger.LogWarning("Metadata SHA-1 mismatch for {VersionId} from {Source}.", versionId, candidate);
                        continue;
                    }
                }

                return payload;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                lastError = exception;
                _logger.LogWarning(exception, "Metadata download for {VersionId} from {Source} failed; trying the next source.", versionId, candidate);
            }
        }

        throw new VersionMetadataException($"Failed to download metadata for version '{versionId}' from all configured sources: {lastError?.Message}", null, lastError);
    }

    private async Task<MinecraftVersionMetadata> ReadMetadataAsync(string metadataPath, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(metadataPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await VersionMetadataParser.ParseAsync(stream, metadataPath, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<MinecraftVersionMetadata> ParseMetadataAsync(byte[] payload, string sourcePath, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(payload, writable: false);
        return await VersionMetadataParser.ParseAsync(stream, sourcePath, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteFileAtomicallyAsync(string destinationPath, byte[] payload, CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new IOException($"Version metadata destination '{destinationPath}' does not have a parent directory.");
        }

        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(_paths.TempDirectory);
        string temporaryPath = Path.Combine(_paths.TempDirectory, $"{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, payload, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static Uri ParseOfficialUri(string versionId, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new VersionMetadataException($"The manifest URL for version '{versionId}' is not a valid HTTP(S) URL: '{url}'.");
        }

        return uri;
    }

    private static void ValidateVersionId(string versionId)
    {
        if (versionId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || versionId.Contains(Path.DirectorySeparatorChar)
            || versionId.Contains(Path.AltDirectorySeparatorChar)
            || versionId is "." or "..")
        {
            throw new ArgumentException("Version IDs must be a single directory-safe name.", nameof(versionId));
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
            // Temporary file cleanup is best effort; stale files are ignored by later downloads.
        }
        catch (UnauthorizedAccessException)
        {
            // Same as above: a locked temporary file must not fail an otherwise successful download.
        }
    }
}
