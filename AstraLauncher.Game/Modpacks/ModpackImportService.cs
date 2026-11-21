using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using AstraLauncher.Core.Models;
using AstraLauncher.Core.Services;
using AstraLauncher.Downloader;
using AstraLauncher.Game.Instances;
using AstraLauncher.Game.Install;
using AstraLauncher.Game.Loaders;
using Microsoft.Extensions.Logging;

namespace AstraLauncher.Game.Modpacks;

public sealed record ModpackFile(string RelativePath, string DownloadUrl, string? Sha1, long? Size);

public sealed record ModpackImportProgress(string Stage, string Message, double? Percent = null);

public sealed record ModpackManifest(
    string Name,
    string MinecraftVersion,
    LoaderKind? Loader,
    string? LoaderVersion,
    IReadOnlyList<ModpackFile> Files);

public interface IModpackImportService
{
    Task<GameInstance> ImportAsync(
        string mrpackPath,
        string? instanceName = null,
        IProgress<ModpackImportProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Imports Modrinth <c>.mrpack</c> files: installs the vanilla version and loader, creates an isolated
/// instance, downloads the pack files with SHA-1 checks and extracts overrides into the instance directory.
/// </summary>
public sealed class ModpackImportService : IModpackImportService
{
    private readonly ILauncherPaths _paths;
    private readonly IGameInstanceRepository _instances;
    private readonly IVersionInstallService _versionInstall;
    private readonly ILoaderInstallService _loaderInstall;
    private readonly HttpClient _http;
    private readonly ILogger<ModpackImportService> _logger;

    public ModpackImportService(
        ILauncherPaths paths,
        IGameInstanceRepository instances,
        IVersionInstallService versionInstall,
        ILoaderInstallService loaderInstall,
        HttpClient http,
        ILogger<ModpackImportService> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(versionInstall);
        ArgumentNullException.ThrowIfNull(loaderInstall);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(logger);
        _paths = paths;
        _instances = instances;
        _versionInstall = versionInstall;
        _loaderInstall = loaderInstall;
        _http = http;
        _logger = logger;
    }

    public async Task<GameInstance> ImportAsync(
        string mrpackPath,
        string? instanceName = null,
        IProgress<ModpackImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mrpackPath);
        if (!File.Exists(mrpackPath))
        {
            throw new FileNotFoundException("整合包文件不存在。", mrpackPath);
        }

        string temp = Path.Combine(Path.GetTempPath(), "AstraLauncher-Mrpack", Guid.NewGuid().ToString("N"));
        GameInstance? created = null;
        try
        {
            Directory.CreateDirectory(temp);
            progress?.Report(new ModpackImportProgress("解析整合包", "正在解压 .mrpack…"));
            ExtractSafely(mrpackPath, temp);
            string indexPath = Path.Combine(temp, "modrinth.index.json");
            if (!File.Exists(indexPath))
            {
                throw new InvalidDataException("不是有效的 Modrinth 整合包：缺少 modrinth.index.json。");
            }

            ModpackManifest manifest = ParseManifest(await File.ReadAllTextAsync(indexPath, cancellationToken).ConfigureAwait(false));
            string name = string.IsNullOrWhiteSpace(instanceName) ? manifest.Name : instanceName.Trim();
            progress?.Report(new ModpackImportProgress("安装游戏", $"正在安装 Minecraft {manifest.MinecraftVersion}…", 0));

            await _versionInstall.InstallAsync(
                manifest.MinecraftVersion,
                new Progress<VersionInstallProgress>(value => progress?.Report(new ModpackImportProgress(
                    "安装游戏", value.CurrentFile ?? value.Stage,
                    value.TotalBytes > 0 ? value.DownloadedBytes * 100.0 / value.TotalBytes : null))),
                cancellationToken).ConfigureAwait(false);

            string versionId = manifest.MinecraftVersion;
            if (manifest.Loader is LoaderKind loader && !string.IsNullOrWhiteSpace(manifest.LoaderVersion))
            {
                progress?.Report(new ModpackImportProgress("安装加载器", $"正在安装 {loader} {manifest.LoaderVersion}…", 0));
                LoaderInstallResult result = await _loaderInstall.InstallAsync(
                    loader,
                    manifest.MinecraftVersion,
                    manifest.LoaderVersion,
                    new Progress<LoaderInstallProgress>(value => progress?.Report(new ModpackImportProgress(
                        "安装加载器", value.Message, value.Percent))),
                    cancellationToken).ConfigureAwait(false);
                versionId = result.VersionId;
            }

            created = await _instances.CreateAsync(name, versionId, cancellationToken).ConfigureAwait(false);
            await DownloadFilesAsync(manifest, created, progress, cancellationToken).ConfigureAwait(false);
            await ExtractOverridesAsync(temp, created.DirectoryPath, progress, cancellationToken).ConfigureAwait(false);
            progress?.Report(new ModpackImportProgress("完成", $"整合包「{name}」导入完成。", 100));
            return created;
        }
        catch
        {
            if (created is not null)
            {
                try
                {
                    await _instances.DeleteAsync(created.Id, deleteManagedDirectory: true, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception cleanupException)
                {
                    _logger.LogWarning(cleanupException, "Failed to remove partially imported instance {Id}.", created.Id);
                }
            }

            throw;
        }
        finally
        {
            TryDeleteDirectory(temp);
        }
    }

    public static ModpackManifest ParseManifest(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("dependencies", out JsonElement dependencies) || dependencies.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("整合包缺少 dependencies。");
        }

        string minecraft = ReadString(dependencies, "minecraft")
            ?? throw new InvalidDataException("整合包缺少 Minecraft 版本。");
        LoaderKind? loader = null;
        string? loaderVersion = null;
        foreach ((string key, LoaderKind kind) in new[]
                 {
                     ("fabric-loader", LoaderKind.Fabric),
                     ("quilt-loader", LoaderKind.Quilt),
                     ("forge", LoaderKind.Forge),
                     ("neoforge", LoaderKind.NeoForge),
                 })
        {
            string? value = ReadString(dependencies, key);
            if (!string.IsNullOrWhiteSpace(value))
            {
                loader = kind;
                loaderVersion = value;
                break;
            }
        }

        var files = new List<ModpackFile>();
        if (root.TryGetProperty("files", out JsonElement fileArray) && fileArray.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement file in fileArray.EnumerateArray())
            {
                string? path = ReadString(file, "path");
                if (string.IsNullOrWhiteSpace(path) || !IsSafeRelativePath(path))
                {
                    continue;
                }

                if (file.TryGetProperty("env", out JsonElement env) &&
                    string.Equals(ReadString(env, "client"), "unsupported", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string? url = null;
                if (file.TryGetProperty("downloads", out JsonElement downloads) && downloads.ValueKind == JsonValueKind.Array)
                {
                    url = downloads.EnumerateArray()
                        .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : null)
                        .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item));
                }

                if (string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }

                string? sha1 = null;
                if (file.TryGetProperty("hashes", out JsonElement hashes))
                {
                    sha1 = ReadString(hashes, "sha1");
                }

                long? size = file.TryGetProperty("fileSize", out JsonElement sizeElement) && sizeElement.TryGetInt64(out long parsedSize)
                    ? parsedSize
                    : null;
                files.Add(new ModpackFile(path.Replace('\\', '/'), url, sha1, size));
            }
        }

        string name = ReadString(root, "name") ?? "导入的整合包";
        return new ModpackManifest(name, minecraft, loader, loaderVersion, files);
    }

    private async Task DownloadFilesAsync(
        ModpackManifest manifest,
        GameInstance instance,
        IProgress<ModpackImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (manifest.Files.Count == 0)
        {
            return;
        }

        long totalBytes = manifest.Files.Sum(file => file.Size ?? 0);
        long completedBytes = 0;
        using var queue = new DownloadQueue(_http, new DownloadQueueOptions
        {
            MaximumConcurrentDownloads = 4,
            MaximumConcurrentDownloadsPerHost = 8,
            EnableSegmentedDownloads = true,
            SegmentsPerFile = 4,
            MinimumSegmentSizeBytes = 2 * 1024 * 1024,
            MaximumConcurrentSegments = 8,
            RequestHeadersTimeout = TimeSpan.FromSeconds(30),
            ReadIdleTimeout = TimeSpan.FromSeconds(60),
        });

        for (int index = 0; index < manifest.Files.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ModpackFile file = manifest.Files[index];
            string target = Path.GetFullPath(Path.Combine(instance.DirectoryPath, file.RelativePath));
            string root = Path.GetFullPath(instance.DirectoryPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("整合包文件路径越界：" + file.RelativePath);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            progress?.Report(new ModpackImportProgress(
                "下载整合包文件",
                $"正在下载 {file.RelativePath}（{index + 1}/{manifest.Files.Count}）",
                totalBytes > 0 ? completedBytes * 100.0 / totalBytes : null));
            DownloadRequest request = DownloadRequest.Create(
                new Uri(file.DownloadUrl, UriKind.Absolute),
                target,
                expectedSha1: file.Sha1,
                expectedSize: file.Size);
            IDownloadHandle handle = queue.Enqueue(request);
            using CancellationTokenRegistration registration = cancellationToken.Register(handle.Cancel);
            await handle.Completion.ConfigureAwait(false);
            completedBytes += file.Size ?? 0;
        }

        progress?.Report(new ModpackImportProgress("下载整合包文件", "整合包文件下载完成。", 100));
    }

    private async Task ExtractOverridesAsync(
        string sourceRoot,
        string instanceDirectory,
        IProgress<ModpackImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        foreach (string folderName in new[] { "overrides", "client-overrides" })
        {
            string source = Path.Combine(sourceRoot, folderName);
            if (!Directory.Exists(source))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new ModpackImportProgress("应用覆盖文件", $"正在应用 {folderName}…"));
            await Task.Run(() => CopyDirectory(source, instanceDirectory), cancellationToken).ConfigureAwait(false);
        }
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
        {
            return false;
        }

        string[] parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 && !parts.Any(part => part is "." or "..");
    }

    private static void ExtractSafely(string zipPath, string destination)
    {
        string root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using ZipArchive archive = ZipFile.OpenRead(zipPath);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            string target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("整合包 ZIP 包含越界路径：" + entry.FullName);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort.
        }
    }
}
