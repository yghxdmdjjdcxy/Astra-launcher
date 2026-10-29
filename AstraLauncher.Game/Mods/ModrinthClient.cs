using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AstraLauncher.Game.Mods;

/// <summary>
/// Minimal Modrinth v2 API client (search, compatible versions, download). The API is public and needs no key; requests
/// carry a descriptive User-Agent as required by Modrinth's rules.
/// </summary>
public sealed class ModrinthClient : IModrinthClient
{
    private const string ApiBase = "https://api.modrinth.com/v2";
    private const string UserAgent = "AstraLauncher/1.0 (Windows; Minecraft launcher)";

    private readonly HttpClient _httpClient;
    private readonly ILogger<ModrinthClient> _logger;

    public ModrinthClient(HttpClient httpClient, ILogger<ModrinthClient> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <summary>Maps a detected mod platform to the loader id used by Modrinth facets and version filters.</summary>
    public static string GetLoaderName(ModPlatform platform) => platform switch
    {
        ModPlatform.Fabric => "fabric",
        ModPlatform.Quilt => "quilt",
        ModPlatform.Forge => "forge",
        ModPlatform.NeoForge => "neoforge",
        _ => "fabric",
    };

    public async Task<ModrinthSearchResult> SearchAsync(
        string query,
        string gameVersion,
        string loader,
        int offset = 0,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(loader);
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, 50);
        string facets = $"[[\"project_type:mod\"],[\"versions:{EscapeToken(gameVersion)}\"],[\"categories:{EscapeToken(loader)}\"]]";
        string url = $"{ApiBase}/search?query={Uri.EscapeDataString(query ?? string.Empty)}&limit={limit}&offset={offset}&facets={Uri.EscapeDataString(facets)}";
        string json = await GetJsonAsync(url, "搜索 MOD", cancellationToken).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        var hits = new List<ModrinthSearchHit>();
        if (root.TryGetProperty("hits", out JsonElement hitArray) && hitArray.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement hit in hitArray.EnumerateArray())
            {
                string? projectId = GetString(hit, "project_id");
                string? title = GetString(hit, "title");
                if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                var categories = new List<string>();
                if (hit.TryGetProperty("categories", out JsonElement categoryArray) && categoryArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement category in categoryArray.EnumerateArray())
                    {
                        if (category.ValueKind == JsonValueKind.String && category.GetString() is { Length: > 0 } value)
                        {
                            categories.Add(value);
                        }
                    }
                }

                hits.Add(new ModrinthSearchHit(
                    projectId,
                    GetString(hit, "slug") ?? projectId,
                    title,
                    GetString(hit, "description") ?? string.Empty,
                    GetString(hit, "icon_url"),
                    hit.TryGetProperty("downloads", out JsonElement downloads) && downloads.TryGetInt64(out long downloadCount) ? downloadCount : 0,
                    categories));
            }
        }

        int totalHits = root.TryGetProperty("total_hits", out JsonElement total) && total.TryGetInt32(out int totalValue) ? totalValue : hits.Count;
        return new ModrinthSearchResult(hits, totalHits);
    }

    public async Task<IReadOnlyList<ModrinthVersion>> GetVersionsAsync(
        string projectId,
        string gameVersion,
        string loader,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(loader);
        string url = $"{ApiBase}/project/{Uri.EscapeDataString(projectId)}/version?game_versions={Uri.EscapeDataString($"[\"{gameVersion}\"]")}&loaders={Uri.EscapeDataString($"[\"{loader}\"]")}";
        string json = await GetJsonAsync(url, "获取 MOD 版本", cancellationToken).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("MOD 版本接口返回了非数组 JSON。");
        }

        var versions = new List<ModrinthVersion>();
        foreach (JsonElement element in document.RootElement.EnumerateArray())
        {
            ModrinthVersion? version = ParseVersion(element);
            if (version is not null)
            {
                versions.Add(version);
            }
        }

        return versions;
    }

    public async Task<string> DownloadAsync(
        ModrinthVersion version,
        string modsDirectory,
        IProgress<ModrinthDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(modsDirectory);
        ModrinthVersionFile? file = version.Files.FirstOrDefault(candidate => candidate.IsPrimary) ?? version.Files.FirstOrDefault();
        if (file is null)
        {
            throw new InvalidOperationException($"MOD 版本 {version.Name} 没有可下载文件。");
        }

        Directory.CreateDirectory(modsDirectory);
        string fileName = SanitizeFileName(file.FileName);
        string destination = Path.Combine(modsDirectory, fileName);
        if (File.Exists(destination))
        {
            string existingHash = await ComputeSha1Async(destination, cancellationToken).ConfigureAwait(false);
            if (string.Equals(existingHash, file.Sha1, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Modrinth file {FileName} already exists and matches its SHA-1; reusing it.", fileName);
                return destination;
            }

            string stem = Path.GetFileNameWithoutExtension(fileName);
            for (int suffix = 2; suffix < 1000; suffix++)
            {
                string candidate = Path.Combine(modsDirectory, $"{stem}-{suffix}.jar");
                if (!File.Exists(candidate))
                {
                    destination = candidate;
                    break;
                }
            }
        }

        progress?.Report(new ModrinthDownloadProgress($"正在下载 {fileName}", 0));
        string temporaryPath = destination + ".part";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, file.Url);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            using HttpResponseMessage response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"下载 MOD 失败：HTTP {(int)response.StatusCode}（{response.ReasonPhrase}）。");
            }

            long totalBytes = response.Content.Headers.ContentLength ?? file.Size;
            await using (Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (FileStream target = new(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1))
            {
                byte[] buffer = new byte[81920];
                long downloaded = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, read);
                    downloaded += read;
                    if (totalBytes > 0)
                    {
                        progress?.Report(new ModrinthDownloadProgress($"正在下载 {fileName}", Math.Clamp(downloaded * 100d / totalBytes, 0, 100)));
                    }
                }

                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
                string actualHash = Convert.ToHexString(hash.GetHashAndReset());
                if (!string.Equals(actualHash, file.Sha1, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"MOD 文件 SHA-1 校验失败：预期 {file.Sha1}，实际 {actualHash}。");
                }
            }

            File.Move(temporaryPath, destination, overwrite: true);
            progress?.Report(new ModrinthDownloadProgress($"已安装 {fileName}", 100));
            _logger.LogInformation("Downloaded Modrinth mod {FileName} ({Length} bytes) into {ModsDirectory}.", fileName, new FileInfo(destination).Length, modsDirectory);
            return destination;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryDelete(temporaryPath);
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or HttpRequestException or InvalidDataException)
        {
            TryDelete(temporaryPath);
            _logger.LogError(exception, "下载或安装 MOD 失败：{FileName}", file.FileName);
            throw new InvalidOperationException($"下载 MOD 失败：{exception.Message}", exception);
        }
    }

    private ModrinthVersion? ParseVersion(JsonElement element)
    {
        string? id = GetString(element, "id");
        string? projectId = GetString(element, "project_id");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(projectId))
        {
            return null;
        }

        var files = new List<ModrinthVersionFile>();
        if (element.TryGetProperty("files", out JsonElement fileArray) && fileArray.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement file in fileArray.EnumerateArray())
            {
                string? urlText = GetString(file, "url");
                string? fileName = GetString(file, "filename");
                string? sha1 = file.TryGetProperty("hashes", out JsonElement hashes) ? GetString(hashes, "sha1") : null;
                if (string.IsNullOrWhiteSpace(urlText) || string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(sha1)
                    || !Uri.TryCreate(urlText, UriKind.Absolute, out Uri? uri))
                {
                    continue;
                }

                long size = file.TryGetProperty("size", out JsonElement sizeElement) && sizeElement.TryGetInt64(out long value) ? value : 0;
                bool primary = file.TryGetProperty("primary", out JsonElement primaryElement) && primaryElement.ValueKind == JsonValueKind.True;
                files.Add(new ModrinthVersionFile(fileName, uri, size, sha1, primary));
            }
        }

        if (files.Count == 0)
        {
            return null;
        }

        return new ModrinthVersion(
            id,
            projectId,
            GetString(element, "name") ?? id,
            GetString(element, "version_number") ?? id,
            GetStringArray(element, "game_versions"),
            GetStringArray(element, "loaders"),
            files);
    }

    private async Task<string> GetJsonAsync(string url, string description, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            using HttpResponseMessage response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"{description}失败：HTTP {(int)response.StatusCode}（{response.ReasonPhrase}）。");
            }

            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw new InvalidOperationException($"{description}超时或已取消。", exception);
        }
        catch (HttpRequestException exception)
        {
            throw new InvalidOperationException($"{description}失败：{exception.Message}", exception);
        }
    }

    private static List<string> GetStringArray(JsonElement element, string propertyName)
    {
        var values = new List<string>();
        if (element.TryGetProperty(propertyName, out JsonElement array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in array.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } value)
                {
                    values.Add(value);
                }
            }
        }

        return values;
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string SanitizeFileName(string fileName)
    {
        string name = Path.GetFileName(fileName);
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        if (!name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
        {
            name += ".jar";
        }

        return name.Length == 0 ? "mod.jar" : name;
    }

    private static string EscapeToken(string value) => value.Replace("\"", string.Empty, StringComparison.Ordinal);

    private static async Task<string> ComputeSha1Async(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA1.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
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
            // Best effort only.
        }
    }
}
