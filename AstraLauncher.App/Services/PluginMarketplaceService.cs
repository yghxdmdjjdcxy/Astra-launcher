using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using AstraLauncher.Core.Models;
using AstraLauncher.Core.Services;
using AstraLauncher.Downloader;
using AstraLauncher.Plugin.Abstractions;
using Microsoft.Extensions.Logging;

namespace AstraLauncher.App.Services;

/// <summary>One plugin package listed in the curated GitHub index or discovered from GitHub releases.</summary>
public sealed record PluginMarketEntry(
    string Id,
    string Name,
    string Description,
    string Author,
    string Version,
    string DownloadUrl,
    string? Repository = null,
    string? Homepage = null,
    string? Sha256 = null);

public sealed class PluginIndex
{
    public int SchemaVersion { get; set; } = 1;
    public List<PluginMarketEntry> Plugins { get; set; } = [];
}

public interface IPluginMarketplaceService
{
    Task<IReadOnlyList<PluginMarketEntry>> GetCatalogAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns the latest package for an installed plugin, or null when no newer version exists.</summary>
    Task<PluginMarketEntry?> FindUpdateAsync(PluginManifest installed, CancellationToken cancellationToken = default);

    /// <summary>Downloads a plugin ZIP to a temporary file with parallel Range requests.</summary>
    Task<string> DownloadAsync(
        PluginMarketEntry entry,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>Downloads an update into <c>plugins/.updates</c>; it is applied on the next launcher start.</summary>
    Task StageUpdateAsync(
        PluginMarketEntry entry,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads the curated <c>plugins/index.json</c> (hosted on GitHub), falls back to GitHub topic search,
/// downloads packages through <see cref="DownloadQueue"/>, and stages updates for the next startup.
/// </summary>
public sealed class PluginMarketplaceService : IPluginMarketplaceService
{
    private const int DownloadThreads = 32;
    private const string GitHubApi = "https://api.github.com";
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);

    private readonly HttpClient _http;
    private readonly ISettingsService _settings;
    private readonly ILauncherPaths _paths;
    private readonly ILogger<PluginMarketplaceService> _logger;
    private readonly object _cacheGate = new();
    private IReadOnlyList<PluginMarketEntry>? _cache;
    private DateTimeOffset _cacheTime;

    public PluginMarketplaceService(
        HttpClient http,
        ISettingsService settings,
        ILauncherPaths paths,
        ILogger<PluginMarketplaceService> logger)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(logger);
        _http = http;
        _settings = settings;
        _paths = paths;
        _logger = logger;
    }

    private string PluginRoot => Path.Combine(_paths.ApplicationDataDirectory, "plugins");

    public async Task<IReadOnlyList<PluginMarketEntry>> GetCatalogAsync(CancellationToken cancellationToken = default)
    {
        lock (_cacheGate)
        {
            if (_cache is not null && DateTimeOffset.UtcNow - _cacheTime < CacheLifetime)
            {
                return _cache;
            }
        }

        IReadOnlyList<PluginMarketEntry> entries;
        try
        {
            LauncherSettings settings = await _settings.GetAsync().ConfigureAwait(false);
            string indexUrl = string.IsNullOrWhiteSpace(settings.PluginIndexUrl)
                ? LauncherSettings.DefaultPluginIndexUrl
                : settings.PluginIndexUrl;
            string json = (await _http.GetStringAsync(indexUrl, cancellationToken).ConfigureAwait(false)).TrimStart('﻿');
            PluginIndex? index = JsonSerializer.Deserialize<PluginIndex>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            entries = Normalize(index?.Plugins);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException or TaskCanceledException)
        {
            _logger.LogWarning(exception, "Plugin index unavailable; falling back to GitHub topic search.");
            entries = await SearchGitHubTopicAsync(cancellationToken).ConfigureAwait(false);
        }

        lock (_cacheGate)
        {
            _cache = entries;
            _cacheTime = DateTimeOffset.UtcNow;
        }

        return entries;
    }

    public async Task<PluginMarketEntry?> FindUpdateAsync(PluginManifest installed, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<PluginMarketEntry> catalog = await GetCatalogAsync(cancellationToken).ConfigureAwait(false);
        PluginMarketEntry? entry = catalog.FirstOrDefault(item =>
            string.Equals(item.Id, installed.Id, StringComparison.OrdinalIgnoreCase));
        if (entry is null && !string.IsNullOrWhiteSpace(installed.Repository))
        {
            entry = await GetLatestReleaseEntryAsync(installed.Repository, cancellationToken).ConfigureAwait(false);
        }

        if (entry is null || !IsNewer(entry.Version, installed.Version))
        {
            return null;
        }

        return entry;
    }

    public async Task<string> DownloadAsync(
        PluginMarketEntry entry,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!Uri.TryCreate(entry.DownloadUrl, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException("插件下载地址必须是 HTTP(S) URL。");
        }

        string directory = Path.Combine(Path.GetTempPath(), "AstraLauncher-PluginDownloads");
        Directory.CreateDirectory(directory);
        string target = Path.Combine(directory, Sanitize(entry.Id) + "-" + Guid.NewGuid().ToString("N") + ".zip");

        long? size = await ProbeSizeAsync(uri, cancellationToken).ConfigureAwait(false);
        using var queue = new DownloadQueue(_http, CreateDownloadOptions());
        DownloadRequest request = DownloadRequest.Create(uri, target, expectedSize: size);
        IDownloadHandle handle = queue.Enqueue(request);
        using CancellationTokenRegistration registration = cancellationToken.Register(handle.Cancel);
        void OnProgress(object? sender, DownloadProgress value)
        {
            if (value.TotalBytes is long total && total > 0)
            {
                progress?.Report($"下载 {entry.Name} … {value.BytesReceived * 100 / total}%");
            }
        }

        handle.ProgressChanged += OnProgress;
        try
        {
            await handle.Completion.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException or InvalidOperationException)
        {
            TryDelete(target);
            throw new InvalidOperationException("插件下载失败：" + exception.Message, exception);
        }
        finally
        {
            handle.ProgressChanged -= OnProgress;
        }

        if (!File.Exists(target) || new FileInfo(target).Length == 0)
        {
            TryDelete(target);
            throw new InvalidOperationException("插件下载结果为空。");
        }

        if (!string.IsNullOrWhiteSpace(entry.Sha256))
        {
            string actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(target)));
            if (!string.Equals(actual, entry.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(target);
                throw new InvalidOperationException("插件 SHA-256 校验失败，已丢弃下载文件。");
            }
        }

        return target;
    }

    public async Task StageUpdateAsync(
        PluginMarketEntry entry,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string package = await DownloadAsync(entry, progress, cancellationToken).ConfigureAwait(false);
        string updates = Path.Combine(PluginRoot, ".updates");
        Directory.CreateDirectory(updates);
        string target = Path.Combine(updates, Sanitize(entry.Id) + ".zip");
        File.Move(package, target, overwrite: true);
        progress?.Report($"{entry.Name} 更新已下载，重启启动器后生效。");
    }

    private static DownloadQueueOptions CreateDownloadOptions() => new()
    {
        MaximumConcurrentDownloads = 1,
        MaximumConcurrentDownloadsPerHost = DownloadThreads,
        EnableSegmentedDownloads = true,
        SegmentsPerFile = DownloadThreads,
        MinimumSegmentSizeBytes = 1024 * 1024,
        MaximumConcurrentSegments = DownloadThreads,
        RequestHeadersTimeout = TimeSpan.FromSeconds(30),
        ReadIdleTimeout = TimeSpan.FromSeconds(60),
    };

    private async Task<long?> ProbeSizeAsync(Uri uri, CancellationToken cancellationToken)
    {
        try
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, uri);
            using HttpResponseMessage response = await _http.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode && response.Content.Headers.ContentLength is long length && length > 0)
            {
                return length;
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            _logger.LogDebug(exception, "Plugin HEAD probe failed.");
        }

        return null;
    }

    private async Task<IReadOnlyList<PluginMarketEntry>> SearchGitHubTopicAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                GitHubApi + "/search/repositories?q=topic:astra-launcher-plugin&sort=updated&order=desc&per_page=20");
            request.Headers.UserAgent.ParseAdd("AstraLauncher/1.0");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("items", out JsonElement items) || items.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var entries = new List<PluginMarketEntry>();
            foreach (JsonElement item in items.EnumerateArray())
            {
                string? fullName = item.TryGetProperty("full_name", out JsonElement full) ? full.GetString() : null;
                if (string.IsNullOrWhiteSpace(fullName))
                {
                    continue;
                }

                PluginMarketEntry? entry = await GetLatestReleaseEntryAsync(fullName, cancellationToken).ConfigureAwait(false);
                if (entry is not null)
                {
                    entries.Add(entry);
                }
            }

            return entries;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException)
        {
            _logger.LogDebug(exception, "GitHub topic search failed.");
            return [];
        }
    }

    private async Task<PluginMarketEntry?> GetLatestReleaseEntryAsync(string repository, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{GitHubApi}/repos/{repository}/releases/latest");
            request.Headers.UserAgent.ParseAdd("AstraLauncher/1.0");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            string tag = root.TryGetProperty("tag_name", out JsonElement tagElement) ? tagElement.GetString() ?? string.Empty : string.Empty;
            string version = tag.TrimStart('v', 'V');
            if (string.IsNullOrWhiteSpace(version) ||
                !root.TryGetProperty("assets", out JsonElement assets) ||
                assets.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (JsonElement asset in assets.EnumerateArray())
            {
                string? name = asset.TryGetProperty("name", out JsonElement nameElement) ? nameElement.GetString() : null;
                string? url = asset.TryGetProperty("browser_download_url", out JsonElement urlElement) ? urlElement.GetString() : null;
                if (name is null || url is null || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string id = repository.Replace('/', '.');
                return new PluginMarketEntry(
                    id,
                    repository.Split('/').Last(),
                    root.TryGetProperty("body", out JsonElement body) ? (body.GetString() ?? string.Empty) : string.Empty,
                    repository.Split('/').First(),
                    string.IsNullOrWhiteSpace(version) ? "0.0.0" : version,
                    url,
                    repository,
                    $"https://github.com/{repository}");
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException)
        {
            _logger.LogDebug(exception, "Cannot read latest GitHub release for {Repository}.", repository);
        }

        return null;
    }

    private static IReadOnlyList<PluginMarketEntry> Normalize(IEnumerable<PluginMarketEntry>? plugins)
    {
        if (plugins is null)
        {
            return [];
        }

        return plugins
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Id)
                && !string.IsNullOrWhiteSpace(entry.Version)
                && Uri.TryCreate(entry.DownloadUrl, UriKind.Absolute, out _))
            .GroupBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsNewer(string candidate, string current)
    {
        if (Version.TryParse(candidate.TrimStart('v', 'V').Split('-')[0], out Version? left) &&
            Version.TryParse(current.TrimStart('v', 'V').Split('-')[0], out Version? right))
        {
            return left > right;
        }

        return !string.Equals(candidate, current, StringComparison.OrdinalIgnoreCase);
    }

    private static string Sanitize(string value)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalid, '_');
        }

        return string.IsNullOrWhiteSpace(value) ? "plugin" : value;
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
            // Best effort.
        }
    }
}
