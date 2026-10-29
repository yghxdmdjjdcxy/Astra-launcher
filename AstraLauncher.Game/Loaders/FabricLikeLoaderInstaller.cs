using System.IO;
using System.Text.Json;
using AstraLauncher.Core.Models;
using AstraLauncher.Core.Services;
using Microsoft.Extensions.Logging;

namespace AstraLauncher.Game.Loaders;

/// <summary>
/// Installs Fabric and Quilt from their JSON profiles: the profile is downloaded into
/// <c>versions/&lt;id&gt;/&lt;id&gt;.json</c> and the version install service then verifies and downloads the
/// inherited Minecraft files and the loader libraries.
/// </summary>
public sealed class FabricLikeLoaderInstaller : IFabricLikeLoaderInstaller
{
    private const string FabricProfileBaseUrl = "https://meta.fabricmc.net/v2/versions/loader/";
    private const string QuiltProfileBaseUrl = "https://meta.quiltmc.org/v3/versions/loader/";
    private const string DownloadStage = "下载加载器配置";
    private const string WriteStage = "写入版本描述";
    private const string VerifyStage = "校验/补齐加载器文件";

    private readonly ILauncherPaths _paths;
    private readonly HttpClient _httpClient;
    private readonly IVersionInstallService _installService;
    private readonly ILogger<FabricLikeLoaderInstaller> _logger;

    public FabricLikeLoaderInstaller(
        ILauncherPaths paths,
        HttpClient httpClient,
        IVersionInstallService installService,
        ILogger<FabricLikeLoaderInstaller> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(installService);
        ArgumentNullException.ThrowIfNull(logger);
        _paths = paths;
        _httpClient = httpClient;
        _installService = installService;
        _logger = logger;
    }

    public async Task<LoaderInstallResult> InstallAsync(
        LoaderKind kind,
        string minecraftVersion,
        string loaderVersion,
        IProgress<LoaderInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (kind is not (LoaderKind.Fabric or LoaderKind.Quilt))
        {
            throw new ArgumentException("仅 Fabric 与 Quilt 支持通过 JSON 配置安装。", nameof(kind));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(minecraftVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(loaderVersion);
        string minecraft = minecraftVersion.Trim();
        string loader = loaderVersion.Trim();
        _logger.LogInformation(
            "Installing {LoaderKind} {LoaderVersion} for Minecraft {MinecraftVersion}.",
            kind,
            loader,
            minecraft);

        progress?.Report(new LoaderInstallProgress(DownloadStage, $"正在下载 {kind} 加载器配置…"));
        string profileUrl = BuildProfileUrl(kind, minecraft, loader);
        byte[] profileBytes = await DownloadProfileAsync(profileUrl, kind, cancellationToken).ConfigureAwait(false);
        string versionId = ParseProfile(profileBytes, minecraft, kind);
        string profilePath = Path.Combine(_paths.VersionsDirectory, versionId, $"{versionId}.json");

        progress?.Report(new LoaderInstallProgress(WriteStage, $"正在写入 {versionId}.json…"));
        await WriteProfileAsync(profilePath, profileBytes, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Stored {LoaderKind} profile for Minecraft {MinecraftVersion} as version {VersionId}.",
            kind,
            minecraft,
            versionId);

        progress?.Report(new LoaderInstallProgress(VerifyStage, "正在校验并补齐加载器文件…"));
        IProgress<VersionInstallProgress>? mappedProgress = null;
        if (progress is not null)
        {
            IProgress<LoaderInstallProgress> outerProgress = progress;
            mappedProgress = new InlineProgress<VersionInstallProgress>(
                value => outerProgress.Report(MapProgress(value)));
        }

        await _installService.InstallAsync(versionId, mappedProgress, cancellationToken).ConfigureAwait(false);
        return new LoaderInstallResult(versionId, minecraft, kind, loader);
    }

    private static string BuildProfileUrl(LoaderKind kind, string minecraftVersion, string loaderVersion)
    {
        string baseUrl = kind == LoaderKind.Fabric ? FabricProfileBaseUrl : QuiltProfileBaseUrl;
        return $"{baseUrl}{Uri.EscapeDataString(minecraftVersion)}/{Uri.EscapeDataString(loaderVersion)}/profile/json";
    }

    /// <summary>
    /// Downloads the profile. Transport failures are surfaced as <see cref="InvalidOperationException"/>; a non-success
    /// status or an empty body is invalid profile data and is surfaced as <see cref="InvalidDataException"/>.
    /// </summary>
    private async Task<byte[]> DownloadProfileAsync(string url, LoaderKind kind, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await _httpClient
                .GetAsync(new Uri(url, UriKind.Absolute), HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidDataException(
                    $"下载 {kind} 加载器配置失败：HTTP {(int)response.StatusCode} ({response.ReasonPhrase})。");
            }

            byte[] payload = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            return payload.Length == 0
                ? throw new InvalidDataException($"下载 {kind} 加载器配置失败：响应内容为空。")
                : payload;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw new InvalidOperationException($"下载 {kind} 加载器配置超时或已取消。", exception);
        }
        catch (HttpRequestException exception)
        {
            throw new InvalidOperationException($"下载 {kind} 加载器配置失败：{exception.Message}", exception);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException($"下载 {kind} 加载器配置失败：{exception.Message}", exception);
        }
    }

    /// <summary>
    /// Validates the profile shape and returns its version id. Ids are used as a single directory name, so any value
    /// that could escape <c>versions/</c> is rejected.
    /// </summary>
    private static string ParseProfile(byte[] payload, string minecraftVersion, LoaderKind kind)
    {
        using JsonDocument document = ParseProfileDocument(payload, kind);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{kind} 加载器配置的根节点必须是 JSON 对象。");
        }

        string? versionId = ReadString(root, "id");
        if (string.IsNullOrWhiteSpace(versionId) || !IsSafeVersionId(versionId))
        {
            throw new InvalidDataException($"{kind} 加载器配置的 'id' 不是安全的单目录名称：'{versionId}'。");
        }

        string? inheritsFrom = ReadString(root, "inheritsFrom");
        if (inheritsFrom is null)
        {
            throw new InvalidDataException($"{kind} 加载器配置缺少字符串字段 'inheritsFrom'。");
        }

        if (!string.Equals(inheritsFrom, minecraftVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"{kind} 加载器配置的 'inheritsFrom'（'{inheritsFrom}'）与 Minecraft 版本 '{minecraftVersion}' 不一致。");
        }

        if (!root.TryGetProperty("libraries", out JsonElement libraries) || libraries.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"{kind} 加载器配置缺少 'libraries' 数组。");
        }

        return versionId;
    }

    private static JsonDocument ParseProfileDocument(byte[] payload, LoaderKind kind)
    {
        try
        {
            return JsonDocument.Parse(payload);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"{kind} 加载器配置不是有效的 JSON。", exception);
        }
    }

    private static string? ReadString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool IsSafeVersionId(string versionId) =>
        versionId.Length > 0
        && versionId is not "." and not ".."
        && versionId.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && !versionId.Contains(Path.DirectorySeparatorChar)
        && !versionId.Contains(Path.AltDirectorySeparatorChar)
        && !versionId.Contains("..", StringComparison.Ordinal);

    /// <summary>Writes through a uniquely named temporary file so a failed write never leaves a partial profile.</summary>
    private static async Task WriteProfileAsync(string destinationPath, byte[] payload, CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException($"加载器配置目标路径 '{destinationPath}' 没有父目录。");
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(directory, $"{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");
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

    private static LoaderInstallProgress MapProgress(VersionInstallProgress progress)
    {
        double? percent;
        if (progress.TotalBytes > 0)
        {
            percent = Math.Clamp(progress.DownloadedBytes * 100d / progress.TotalBytes, 0d, 100d);
        }
        else if (progress.TotalFiles > 0)
        {
            percent = Math.Clamp(progress.CompletedFiles * 100d / progress.TotalFiles, 0d, 100d);
        }
        else
        {
            percent = null;
        }

        string message = string.IsNullOrWhiteSpace(progress.CurrentFile)
            ? progress.Stage
            : $"{progress.Stage}：{progress.CurrentFile}";
        return new LoaderInstallProgress(VerifyStage, message, percent);
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
            // Best-effort cleanup: the next install uses a fresh temporary name, so a stale file is harmless.
        }
        catch (UnauthorizedAccessException)
        {
            // Same as above: cleanup must not turn a successfully written profile into a failure.
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
