using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace AstraLauncher.Game.Loaders;

/// <summary>
/// Queries the loader vendors' public version endpoints. Fabric and Quilt expose the complete build list for a
/// Minecraft version, Forge only promotes a latest and a recommended build, and NeoForge publishes a flat Maven
/// version list that is filtered by the NeoForge branch matching the Minecraft version.
/// </summary>
public sealed class LoaderCatalog : ILoaderCatalog
{
    private const string FabricLoaderVersionsBaseUrl = "https://meta.fabricmc.net/v2/versions/loader/";
    private const string QuiltLoaderVersionsBaseUrl = "https://meta.quiltmc.org/v3/versions/loader/";
    private const string ForgePromotionsUrl = "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json";
    private const string NeoForgeVersionsUrl = "https://maven.neoforged.net/api/maven/versions/releases/net/neoforged/neoforge";
    private const string NeoForgeBmclApiMetadataUrl = "https://bmclapi2.bangbang93.com/maven/net/neoforged/neoforge/maven-metadata.xml";
    private const string NeoForgeGameVersionPrefix = "1.";

    private readonly HttpClient _httpClient;
    private readonly ILogger<LoaderCatalog> _logger;

    public LoaderCatalog(HttpClient httpClient, ILogger<LoaderCatalog> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<IReadOnlyList<LoaderVersion>> GetLoaderVersionsAsync(
        LoaderKind kind,
        string minecraftVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(minecraftVersion);
        string version = minecraftVersion.Trim();
        _logger.LogInformation("Fetching {LoaderKind} loader versions for Minecraft {MinecraftVersion}.", kind, version);

        IReadOnlyList<LoaderVersion> versions = kind switch
        {
            LoaderKind.Fabric => await GetFabricVersionsAsync(version, cancellationToken).ConfigureAwait(false),
            LoaderKind.Quilt => await GetQuiltVersionsAsync(version, cancellationToken).ConfigureAwait(false),
            LoaderKind.Forge => await GetForgeVersionsAsync(version, cancellationToken).ConfigureAwait(false),
            LoaderKind.NeoForge => await GetNeoForgeVersionsAsync(version, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "未知的加载器类型。"),
        };

        _logger.LogInformation(
            "Found {VersionCount} {LoaderKind} loader versions for Minecraft {MinecraftVersion}.",
            versions.Count,
            kind,
            version);
        return versions;
    }

    /// <summary>
    /// Reads the Fabric Meta loader list, which is ordered newest to oldest and marks each build as stable or not.
    /// </summary>
    private async Task<IReadOnlyList<LoaderVersion>> GetFabricVersionsAsync(string minecraftVersion, CancellationToken cancellationToken)
    {
        string url = FabricLoaderVersionsBaseUrl + Uri.EscapeDataString(minecraftVersion);
        List<FabricLikeVersionDocument?> entries = await GetJsonAsync<List<FabricLikeVersionDocument?>>(
            url,
            "Fabric 加载器版本列表",
            cancellationToken).ConfigureAwait(false);

        var versions = new List<LoaderVersion>(entries.Count);
        foreach (FabricLikeVersionDocument? entry in entries)
        {
            FabricLikeLoaderDocument? loader = entry?.Loader;
            if (loader is null)
            {
                throw new InvalidOperationException("Fabric 加载器版本列表包含缺少 'loader' 对象的条目。");
            }

            string loaderVersion = RequireLoaderVersion(loader, "Fabric");
            versions.Add(new LoaderVersion(loaderVersion, $"Fabric Loader {loaderVersion}", loader.Stable));
        }

        return versions;
    }

    /// <summary>Reads the Quilt Meta loader list, which is ordered newest to oldest.</summary>
    private async Task<IReadOnlyList<LoaderVersion>> GetQuiltVersionsAsync(string minecraftVersion, CancellationToken cancellationToken)
    {
        string url = QuiltLoaderVersionsBaseUrl + Uri.EscapeDataString(minecraftVersion);
        List<FabricLikeVersionDocument?> entries = await GetJsonAsync<List<FabricLikeVersionDocument?>>(
            url,
            "Quilt 加载器版本列表",
            cancellationToken).ConfigureAwait(false);

        var versions = new List<LoaderVersion>(entries.Count);
        foreach (FabricLikeVersionDocument? entry in entries)
        {
            FabricLikeLoaderDocument? loader = entry?.Loader;
            if (loader is null)
            {
                throw new InvalidOperationException("Quilt 加载器版本列表包含缺少 'loader' 对象的条目。");
            }

            string loaderVersion = RequireLoaderVersion(loader, "Quilt");
            versions.Add(new LoaderVersion(loaderVersion, $"Quilt Loader {loaderVersion}"));
        }

        return versions;
    }

    /// <summary>
    /// Reads the Forge promotions feed. A Minecraft version can have a latest and a recommended build; when both
    /// exist and differ, the recommended build is returned first. Unknown versions yield an empty list.
    /// </summary>
    private async Task<IReadOnlyList<LoaderVersion>> GetForgeVersionsAsync(string minecraftVersion, CancellationToken cancellationToken)
    {
        ForgePromotionsDocument document = await GetJsonAsync<ForgePromotionsDocument>(
            ForgePromotionsUrl,
            "Forge 版本促销信息",
            cancellationToken).ConfigureAwait(false);
        Dictionary<string, string> promos = document.Promos
            ?? throw new InvalidOperationException("Forge 版本促销信息缺少 'promos' 对象。");

        promos.TryGetValue(minecraftVersion + "-recommended", out string? recommended);
        promos.TryGetValue(minecraftVersion + "-latest", out string? latest);
        if (promos.TryGetValue(minecraftVersion, out string? direct))
        {
            // Old promotion documents only carry the bare Minecraft version key.
            if (string.IsNullOrWhiteSpace(recommended))
            {
                recommended = direct;
            }

            if (string.IsNullOrWhiteSpace(latest))
            {
                latest = direct;
            }
        }

        string? recommendedVersion = NormalizeVersion(recommended);
        string? latestVersion = NormalizeVersion(latest);
        var versions = new List<LoaderVersion>(2);
        if (recommendedVersion is not null)
        {
            versions.Add(new LoaderVersion(recommendedVersion, $"Forge {recommendedVersion}", IsRecommended: true));
        }

        if (latestVersion is not null && !string.Equals(latestVersion, recommendedVersion, StringComparison.Ordinal))
        {
            versions.Add(new LoaderVersion(latestVersion, $"Forge {latestVersion}"));
        }

        return versions;
    }

    /// <summary>
    /// Reads the NeoForge Maven release list and keeps the builds of the branch that belongs to the Minecraft
    /// version (for example Minecraft 1.21.1 maps to NeoForge 21.1.*). Matches are ordered newest to oldest.
    /// </summary>
    private async Task<IReadOnlyList<LoaderVersion>> GetNeoForgeVersionsAsync(string minecraftVersion, CancellationToken cancellationToken)
    {
        List<string?> releases;
        try
        {
            NeoForgeVersionsDocument document = await GetJsonAsync<NeoForgeVersionsDocument>(
                NeoForgeVersionsUrl,
                "NeoForge 版本列表",
                cancellationToken).ConfigureAwait(false);
            releases = document.Versions
                ?? throw new InvalidOperationException("NeoForge 版本列表缺少 'versions' 数组。");
        }
        catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException or IOException)
        {
            _logger.LogWarning(exception, "NeoForge 官方 Maven 元数据不可用，改用 BMCLAPI 镜像元数据。");
            releases = await GetNeoForgeVersionsFromBmclApiAsync(cancellationToken).ConfigureAwait(false);
        }

        return BuildNeoForgeVersions(releases, minecraftVersion);
    }

    /// <summary>Reads the Maven metadata XML from the BMCLAPI mirror when the official NeoForge Maven is unreachable.</summary>
    private async Task<List<string?>> GetNeoForgeVersionsFromBmclApiAsync(CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _httpClient
            .GetAsync(new Uri(NeoForgeBmclApiMetadataUrl, UriKind.Absolute), HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"获取 NeoForge 版本列表（镜像）失败：HTTP {(int)response.StatusCode} ({response.ReasonPhrase})。");
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        XDocument document;
        try
        {
            document = XDocument.Load(stream);
        }
        catch (System.Xml.XmlException exception)
        {
            throw new InvalidOperationException("NeoForge 镜像版本列表不是有效的 XML。", exception);
        }

        var versions = new List<string?>();
        foreach (XElement element in document.Descendants("version"))
        {
            versions.Add(element.Value);
        }

        return versions;
    }

    private IReadOnlyList<LoaderVersion> BuildNeoForgeVersions(List<string?> releases, string minecraftVersion)
    {
        string? prefix = GetNeoForgeVersionPrefix(minecraftVersion);
        if (prefix is null)
        {
            return [];
        }

        var matches = new List<string>();
        foreach (string? release in releases)
        {
            if (!string.IsNullOrWhiteSpace(release) && release.StartsWith(prefix, StringComparison.Ordinal))
            {
                matches.Add(release);
            }
        }

        // List<T>.Sort is ascending, so reversing produces the newest-first order the UI expects.
        matches.Sort(NeoForgeVersionComparer.Instance);
        matches.Reverse();

        var versions = new List<LoaderVersion>(matches.Count);
        foreach (string release in matches)
        {
            versions.Add(new LoaderVersion(release, $"NeoForge {release}"));
        }

        return versions;
    }

    /// <summary>Maps a Minecraft version such as 1.21.1 to the NeoForge Maven prefix 21.1.</summary>
    private static string? GetNeoForgeVersionPrefix(string minecraftVersion)
    {
        if (!minecraftVersion.StartsWith(NeoForgeGameVersionPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        string[] segments = minecraftVersion[NeoForgeGameVersionPrefix.Length..].Split('.');
        if (segments.Length == 0 || string.IsNullOrEmpty(segments[0]))
        {
            return null;
        }

        if (segments.Length == 1)
        {
            return segments[0] + ".";
        }

        return string.IsNullOrEmpty(segments[1]) ? null : $"{segments[0]}.{segments[1]}.";
    }

    private static string RequireLoaderVersion(FabricLikeLoaderDocument loader, string loaderName)
    {
        string? version = loader.Version;
        if (string.IsNullOrWhiteSpace(version))
        {
            throw new InvalidOperationException($"{loaderName} 加载器版本列表包含缺少 loader.version 的条目。");
        }

        return version;
    }

    private static string? NormalizeVersion(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Downloads a JSON document and converts every transport or shape failure into an <see cref="InvalidOperationException"/>.
    /// An HTTP 200 response with an empty array is not a failure and is returned as an empty list by the callers.
    /// </summary>
    private async Task<T> GetJsonAsync<T>(string url, string description, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            using HttpResponseMessage response = await _httpClient
                .GetAsync(new Uri(url, UriKind.Absolute), HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"获取{description}失败：HTTP {(int)response.StatusCode} ({response.ReasonPhrase})。");
            }

            string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            T? value;
            try
            {
                value = JsonSerializer.Deserialize<T>(json);
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException($"{description}返回的 JSON 格式无效。", exception);
            }

            return value ?? throw new InvalidOperationException($"{description}返回了空的 JSON 文档。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw new InvalidOperationException($"获取{description}超时或已取消。", exception);
        }
        catch (HttpRequestException exception)
        {
            throw new InvalidOperationException($"获取{description}失败：{exception.Message}", exception);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException($"获取{description}失败：{exception.Message}", exception);
        }
    }

    /// <summary>Compares NeoForge Maven versions numerically segment by segment, falling back to ordinal comparison.</summary>
    private sealed class NeoForgeVersionComparer : IComparer<string>
    {
        public static NeoForgeVersionComparer Instance { get; } = new();

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            string[] left = x.Split('.');
            string[] right = y.Split('.');
            int length = Math.Max(left.Length, right.Length);
            for (int index = 0; index < length; index++)
            {
                string leftSegment = index < left.Length ? left[index] : "0";
                string rightSegment = index < right.Length ? right[index] : "0";
                int comparison = CompareSegment(leftSegment, rightSegment);
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            return string.CompareOrdinal(x, y);
        }

        private static int CompareSegment(string left, string right)
        {
            if (long.TryParse(left, NumberStyles.None, CultureInfo.InvariantCulture, out long leftNumber)
                && long.TryParse(right, NumberStyles.None, CultureInfo.InvariantCulture, out long rightNumber))
            {
                return leftNumber.CompareTo(rightNumber);
            }

            return string.CompareOrdinal(left, right);
        }
    }

    private sealed class FabricLikeVersionDocument
    {
        [JsonPropertyName("loader")]
        public FabricLikeLoaderDocument? Loader { get; init; }
    }

    private sealed class FabricLikeLoaderDocument
    {
        [JsonPropertyName("version")]
        public string? Version { get; init; }

        [JsonPropertyName("stable")]
        public bool Stable { get; init; }
    }

    private sealed class ForgePromotionsDocument
    {
        [JsonPropertyName("promos")]
        public Dictionary<string, string>? Promos { get; init; }
    }

    private sealed class NeoForgeVersionsDocument
    {
        [JsonPropertyName("versions")]
        public List<string?>? Versions { get; init; }
    }
}
