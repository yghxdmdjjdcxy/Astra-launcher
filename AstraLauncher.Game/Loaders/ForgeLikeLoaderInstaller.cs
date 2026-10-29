using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using AstraLauncher.Core.Models;
using AstraLauncher.Core.Services;
using AstraLauncher.Game.Java;
using Microsoft.Extensions.Logging;

namespace AstraLauncher.Game.Loaders;

/// <summary>
/// Installs Forge and NeoForge by downloading their official installer jar and running it with
/// <c>--installClient</c>. The installer creates a version profile which is then completed and verified through
/// <see cref="IVersionInstallService"/>.
/// </summary>
public sealed class ForgeLikeLoaderInstaller : IForgeLikeLoaderInstaller
{
    private const int MinimumJavaMajorVersion = 17;
    private const int InstallerOutputTailLength = 30;
    private const int InstallerFileBufferSize = 81920;
    private const string DownloadStage = "下载官方安装器";
    private const string RunStage = "运行官方安装器";
    private const string VerifyStage = "校验/补齐加载器文件";
    private const string InstallerDirectoryName = "loader-installers";
    private const string ForgeMavenBase = "https://maven.minecraftforge.net/net/minecraftforge/forge";
    private const string NeoForgeMavenBase = "https://maven.neoforged.net/releases/net/neoforged/neoforge";
    private const string ForgeBmclApiBase = "https://bmclapi2.bangbang93.com/maven/net/minecraftforge/forge";
    private const string NeoForgeBmclApiBase = "https://bmclapi2.bangbang93.com/maven/net/neoforged/neoforge";

    private readonly ILauncherPaths _paths;
    private readonly HttpClient _httpClient;
    private readonly IJavaRuntimeDetector _java;
    private readonly IExternalProcessRunner _runner;
    private readonly IVersionInstallService _installService;
    private readonly ILogger<ForgeLikeLoaderInstaller> _logger;

    public ForgeLikeLoaderInstaller(
        ILauncherPaths paths,
        HttpClient httpClient,
        IJavaRuntimeDetector java,
        IExternalProcessRunner runner,
        IVersionInstallService installService,
        ILogger<ForgeLikeLoaderInstaller> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(java);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(installService);
        ArgumentNullException.ThrowIfNull(logger);
        _paths = paths;
        _httpClient = httpClient;
        _java = java;
        _runner = runner;
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
        if (kind is not (LoaderKind.Forge or LoaderKind.NeoForge))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "仅支持 Forge 或 NeoForge 加载器安装。");
        }

        ValidateVersionToken(minecraftVersion, nameof(minecraftVersion));
        ValidateVersionToken(loaderVersion, nameof(loaderVersion));

        _logger.LogInformation(
            "Installing {LoaderKind} {LoaderVersion} for Minecraft {MinecraftVersion}.",
            kind,
            loaderVersion,
            minecraftVersion);

        JavaRuntimeInfo javaRuntime = await SelectJavaAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<Uri> installerUris = BuildInstallerUris(kind, minecraftVersion, loaderVersion);

        progress?.Report(new LoaderInstallProgress(DownloadStage, $"正在下载 {installerUris[0].AbsoluteUri}", null));
        string installerPath = await EnsureInstallerAsync(kind, minecraftVersion, loaderVersion, installerUris, cancellationToken).ConfigureAwait(false);

        // NeoForge installer jars embed the complete version profile; extracting it avoids running the installer,
        // which requires direct access to maven.neoforged.net (blocked on some networks).
        string? embeddedVersionId = kind == LoaderKind.NeoForge ? TryInstallEmbeddedProfile(installerPath, minecraftVersion) : null;
        string versionId;
        if (embeddedVersionId is not null)
        {
            progress?.Report(new LoaderInstallProgress(RunStage, "已从安装器内提取版本描述", null));
            versionId = embeddedVersionId;
        }
        else
        {
            progress?.Report(new LoaderInstallProgress(RunStage, "正在运行官方安装器", null));
            EnsureLauncherProfiles();
            versionId = await RunInstallerAsync(kind, minecraftVersion, loaderVersion, javaRuntime, installerPath, progress, cancellationToken).ConfigureAwait(false);
        }

        progress?.Report(new LoaderInstallProgress(VerifyStage, $"正在校验/补齐版本 {versionId} 的文件", null));
        await VerifyVersionAsync(versionId, progress, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Installed {LoaderKind} {LoaderVersion} for Minecraft {MinecraftVersion} as version {VersionId}.",
            kind,
            loaderVersion,
            minecraftVersion,
            versionId);
        return new LoaderInstallResult(versionId, minecraftVersion, kind, loaderVersion);
    }

    /// <summary>
    /// The Forge/NeoForge installers refuse to run unless a <c>launcher_profiles.json</c> exists (they believe the
    /// official launcher was never started). Create the minimal profile document without touching an existing one.
    /// </summary>
    private void EnsureLauncherProfiles()
    {
        string path = Path.Combine(_paths.MinecraftDirectory, "launcher_profiles.json");
        if (File.Exists(path))
        {
            return;
        }

        string document = "{\"profiles\":{},\"settings\":{},\"version\":3}";
        string temporary = path + ".astra.tmp";
        File.WriteAllText(temporary, document);
        File.Move(temporary, path, overwrite: true);
        _logger.LogInformation("Created a minimal launcher_profiles.json for the offered loader installer.");
    }

    private async Task<JavaRuntimeInfo> SelectJavaAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<JavaRuntimeInfo> runtimes = await _java.DetectAsync(cancellationToken).ConfigureAwait(false);
        var candidates = runtimes
            .Where(runtime => runtime.MajorVersion >= MinimumJavaMajorVersion && !string.IsNullOrWhiteSpace(runtime.ExecutablePath))
            .ToList();
        // The official installers are LTS-oriented: prefer Java 21, then 17, and only then the highest available.
        // Selecting the newest JDK (for example 26) can break older Forge/NeoForge installers.
        JavaRuntimeInfo? selected = candidates.FirstOrDefault(runtime => runtime.MajorVersion == 21)
            ?? candidates.FirstOrDefault(runtime => runtime.MajorVersion == 17)
            ?? candidates.OrderByDescending(runtime => runtime.MajorVersion).ThenBy(runtime => runtime.ExecutablePath, StringComparer.OrdinalIgnoreCase).FirstOrDefault();

        if (selected is null)
        {
            throw new InvalidOperationException(
                $"未检测到 Java {MinimumJavaMajorVersion} 或更高版本，无法运行 Forge/NeoForge 官方安装器。请安装 Java 17 或 Java 21 后重试。");
        }

        _logger.LogInformation(
            "Using Java {JavaVersion} ({JavaMajorVersion}) at {JavaPath} for the loader installer.",
            selected.Version,
            selected.MajorVersion,
            selected.ExecutablePath);
        return selected;
    }

    private static Uri BuildInstallerUri(LoaderKind kind, string minecraftVersion, string loaderVersion)
    {
        if (kind == LoaderKind.NeoForge)
        {
            return new Uri($"{NeoForgeMavenBase}/{loaderVersion}/neoforge-{loaderVersion}-installer.jar", UriKind.Absolute);
        }

        string fullVersion = $"{minecraftVersion}-{loaderVersion}";
        return new Uri($"{ForgeMavenBase}/{fullVersion}/forge-{fullVersion}-installer.jar", UriKind.Absolute);
    }

    private static IReadOnlyList<Uri> BuildInstallerUris(LoaderKind kind, string minecraftVersion, string loaderVersion)
    {
        if (kind == LoaderKind.NeoForge)
        {
            return
            [
                new Uri($"{NeoForgeMavenBase}/{loaderVersion}/neoforge-{loaderVersion}-installer.jar", UriKind.Absolute),
                new Uri($"{NeoForgeBmclApiBase}/{loaderVersion}/neoforge-{loaderVersion}-installer.jar", UriKind.Absolute),
            ];
        }

        string fullVersion = $"{minecraftVersion}-{loaderVersion}";
        return
        [
            new Uri($"{ForgeMavenBase}/{fullVersion}/forge-{fullVersion}-installer.jar", UriKind.Absolute),
            new Uri($"{ForgeBmclApiBase}/{fullVersion}/forge-{fullVersion}-installer.jar", UriKind.Absolute),
        ];
    }

    /// <summary>
    /// NeoForge installers embed the final version profile as <c>version.json</c>. Writing it directly avoids a second
    /// network dependency on the NeoForge Maven host (the libraries are then fetched through the mirror-aware installer).
    /// </summary>
    private string? TryInstallEmbeddedProfile(string installerPath, string minecraftVersion)
    {
        try
        {
            using System.IO.Compression.ZipArchive archive = System.IO.Compression.ZipFile.OpenRead(installerPath);
            System.IO.Compression.ZipArchiveEntry? entry = archive.Entries.FirstOrDefault(candidate =>
                string.Equals(candidate.FullName, "version.json", StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                return null;
            }

            using Stream stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            byte[] payload = buffer.ToArray();
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("id", out JsonElement idElement) || idElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            string versionId = idElement.GetString() ?? string.Empty;
            if (!IsSafeVersionId(versionId))
            {
                _logger.LogWarning("The NeoForge installer embedded an unsafe version id '{VersionId}'.", versionId);
                return null;
            }

            if (root.TryGetProperty("inheritsFrom", out JsonElement inheritsElement)
                && inheritsElement.ValueKind == JsonValueKind.String
                && !string.Equals(inheritsElement.GetString(), minecraftVersion, StringComparison.Ordinal))
            {
                _logger.LogWarning("The NeoForge installer embedded a profile for a different Minecraft version; falling back to running the installer.");
                return null;
            }

            string targetPath = Path.Combine(_paths.VersionsDirectory, versionId, versionId + ".json");
            string directory = Path.GetDirectoryName(targetPath) ?? throw new InvalidOperationException("加载器版本描述路径没有父目录。");
            Directory.CreateDirectory(directory);
            string temporaryPath = targetPath + ".astra.tmp";
            File.WriteAllBytes(temporaryPath, payload);
            File.Move(temporaryPath, targetPath, overwrite: true);
            _logger.LogInformation("Installed NeoForge profile {VersionId} from the embedded installer metadata.", versionId);
            return versionId;
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
        {
            _logger.LogWarning(exception, "Could not extract the embedded NeoForge profile; falling back to running the official installer.");
            return null;
        }
    }

    private static bool IsSafeVersionId(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value is not "." and not ".."
        && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && !value.Contains(Path.DirectorySeparatorChar)
        && !value.Contains(Path.AltDirectorySeparatorChar)
        && !value.Contains("..", StringComparison.Ordinal);

    private async Task<string> EnsureInstallerAsync(
        LoaderKind kind,
        string minecraftVersion,
        string loaderVersion,
        IReadOnlyList<Uri> installerUris,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        foreach (Uri installerUri in installerUris)
        {
            try
            {
                return await EnsureInstallerAsyncSingle(kind, minecraftVersion, loaderVersion, installerUri, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException or IOException)
            {
                lastError = exception;
                _logger.LogWarning(exception, "Downloading the {LoaderKind} installer from {InstallerUri} failed; trying the next source.", kind, installerUri);
            }
        }

        throw new InvalidOperationException($"下载 {kind} 安装器失败：{lastError?.Message}", lastError);
    }

    private async Task<string> EnsureInstallerAsyncSingle(
        LoaderKind kind,
        string minecraftVersion,
        string loaderVersion,
        Uri installerUri,
        CancellationToken cancellationToken)
    {
        string installerDirectory = Path.Combine(_paths.TempDirectory, InstallerDirectoryName);
        Directory.CreateDirectory(installerDirectory);

        string installerFileName = kind == LoaderKind.NeoForge
            ? $"neoforge-{loaderVersion}-installer.jar"
            : $"forge-{minecraftVersion}-{loaderVersion}-installer.jar";
        string destinationPath = Path.GetFullPath(Path.Combine(installerDirectory, SanitizeFileName(installerFileName)));
        if (!IsWithinDirectory(destinationPath, installerDirectory))
        {
            throw new InvalidOperationException("安装器缓存路径超出了临时目录，已拒绝写入。");
        }

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.GetAsync(installerUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidOperationException or OperationCanceledException)
        {
            _logger.LogError(exception, "Failed to download the {LoaderKind} installer from {InstallerUri}.", kind, installerUri);
            throw new InvalidOperationException($"下载 {kind} 官方安装器失败：{exception.Message}", exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"下载 {kind} 官方安装器失败：HTTP {(int)response.StatusCode}（{response.ReasonPhrase}）。");
            }

            long? expectedLength = response.Content.Headers.ContentLength;
            if (File.Exists(destinationPath))
            {
                // Reuse a cached installer only when its size matches the server's Content-Length; a stale or
                // partially downloaded file has a different length and is fetched again.
                long existingLength = new FileInfo(destinationPath).Length;
                if (existingLength > 0 && expectedLength == existingLength)
                {
                    _logger.LogInformation("Reusing cached loader installer {InstallerPath}.", destinationPath);
                    return destinationPath;
                }
            }

            string temporaryPath = destinationPath + ".part";
            try
            {
                await using (Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                {
                    await using FileStream target = new(
                        temporaryPath,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None,
                        InstallerFileBufferSize,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                    await target.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                long downloadedLength = new FileInfo(temporaryPath).Length;
                if (downloadedLength == 0)
                {
                    throw new InvalidOperationException($"下载的 {kind} 官方安装器内容为空。");
                }

                if (expectedLength is long length && downloadedLength != length)
                {
                    throw new InvalidOperationException(
                        $"下载的 {kind} 官方安装器不完整：预期 {length} 字节，实际 {downloadedLength} 字节。");
                }

                File.Move(temporaryPath, destinationPath, overwrite: true);
                _logger.LogInformation("Downloaded loader installer {InstallerPath} ({Length} bytes).", destinationPath, downloadedLength);
                return destinationPath;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                TryDeleteFile(temporaryPath);
                throw;
            }
            catch (InvalidOperationException)
            {
                TryDeleteFile(temporaryPath);
                throw;
            }
            catch (Exception exception) when (exception is IOException or HttpRequestException or UnauthorizedAccessException or OperationCanceledException)
            {
                TryDeleteFile(temporaryPath);
                _logger.LogError(exception, "Failed to save the {LoaderKind} installer to {InstallerPath}.", kind, destinationPath);
                throw new InvalidOperationException($"保存 {kind} 官方安装器失败：{exception.Message}", exception);
            }
        }
    }

    private async Task<string> RunInstallerAsync(
        LoaderKind kind,
        string minecraftVersion,
        string loaderVersion,
        JavaRuntimeInfo javaRuntime,
        string installerPath,
        IProgress<LoaderInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        string minecraftDirectory = _paths.MinecraftDirectory;
        Directory.CreateDirectory(minecraftDirectory);

        var recentOutput = new ConcurrentQueue<string>();
        void OnOutput(string line)
        {
            recentOutput.Enqueue(line);
            while (recentOutput.Count > InstallerOutputTailLength)
            {
                recentOutput.TryDequeue(out _);
            }

            progress?.Report(new LoaderInstallProgress(RunStage, line, null));
        }

        int exitCode = await _runner.RunAsync(
            javaRuntime.ExecutablePath,
            ["-jar", installerPath, "--installClient", minecraftDirectory],
            minecraftDirectory,
            OnOutput,
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "The {LoaderKind} installer for Minecraft {MinecraftVersion} with loader {LoaderVersion} exited with code {ExitCode}.",
            kind,
            minecraftVersion,
            loaderVersion,
            exitCode);

        if (exitCode != 0)
        {
            string tail = string.Join(Environment.NewLine, recentOutput);
            throw new InvalidOperationException(
                $"{kind} 官方安装器退出码为 {exitCode}，安装失败。最后输出：{tail}");
        }

        return await ScanInstalledVersionAsync(kind, minecraftVersion, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> ScanInstalledVersionAsync(LoaderKind kind, string minecraftVersion, CancellationToken cancellationToken)
    {
        string versionsDirectory = _paths.VersionsDirectory;
        if (!Directory.Exists(versionsDirectory))
        {
            throw new InvalidOperationException($"官方安装器已完成，但版本目录不存在：{versionsDirectory}。");
        }

        CapturedProfile? newest = null;
        foreach (string directory in Directory.EnumerateDirectories(versionsDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? directoryId = Path.GetFileName(directory);
            if (string.IsNullOrWhiteSpace(directoryId) || !IsSafeVersionToken(directoryId))
            {
                _logger.LogWarning("Skipping loader profile directory with an unsafe name: {Directory}.", directory);
                continue;
            }

            string jsonPath = Path.Combine(directory, $"{directoryId}.json");
            if (!File.Exists(jsonPath))
            {
                continue;
            }

            try
            {
                string json = await File.ReadAllTextAsync(jsonPath, cancellationToken).ConfigureAwait(false);
                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string? id = ReadStringProperty(root, "id");
                string? inheritsFrom = ReadStringProperty(root, "inheritsFrom");
                if (string.IsNullOrWhiteSpace(id)
                    || !IsSafeVersionToken(id)
                    || !string.Equals(id, directoryId, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(inheritsFrom, minecraftVersion, StringComparison.OrdinalIgnoreCase)
                    || !MatchesLoader(id, kind))
                {
                    continue;
                }

                DateTime lastWriteTimeUtc = File.GetLastWriteTimeUtc(jsonPath);
                if (newest is null || lastWriteTimeUtc > newest.LastWriteTimeUtc)
                {
                    newest = new CapturedProfile(id, jsonPath, lastWriteTimeUtc);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                _logger.LogWarning(exception, "Skipping unreadable loader profile {VersionJsonPath}.", jsonPath);
            }
        }

        if (newest is null)
        {
            throw new InvalidOperationException(
                $"官方安装器已完成，但在“{versionsDirectory}”中未找到适用于 Minecraft {minecraftVersion} 的 {kind} 版本（要求 inheritsFrom 匹配且 id 包含加载器名称）。");
        }

        _logger.LogInformation("Detected installed loader profile {VersionId} at {VersionJsonPath}.", newest.Id, newest.JsonPath);
        return newest.Id;
    }

    private async Task VerifyVersionAsync(string versionId, IProgress<LoaderInstallProgress>? progress, CancellationToken cancellationToken)
    {
        if (!IsSafeVersionToken(versionId))
        {
            throw new InvalidOperationException($"安装器生成的版本 id“{versionId}”不安全，已拒绝使用。");
        }

        string jsonPath = Path.Combine(_paths.VersionsDirectory, versionId, $"{versionId}.json");
        if (!File.Exists(jsonPath))
        {
            throw new InvalidOperationException($"安装器生成的版本文件不存在：{jsonPath}。");
        }

        try
        {
            await using FileStream stream = new(
                jsonPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException($"安装器生成的版本文件不是 JSON 对象：{jsonPath}。");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"安装器生成的版本文件无法解析：{jsonPath}。", exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"无法读取安装器生成的版本文件：{jsonPath}。", exception);
        }

        IProgress<VersionInstallProgress>? mappedProgress = null;
        if (progress is not null)
        {
            IProgress<LoaderInstallProgress> reporter = progress;
            mappedProgress = new DelegatingProgress<VersionInstallProgress>(value =>
            {
                string detail = string.IsNullOrWhiteSpace(value.CurrentFile)
                    ? value.Stage
                    : string.Concat(value.Stage, "：", value.CurrentFile);
                double? percent = value.TotalBytes > 0
                    ? Math.Clamp(value.DownloadedBytes * 100d / value.TotalBytes, 0d, 100d)
                    : value.TotalFiles > 0
                        ? Math.Clamp(value.CompletedFiles * 100d / value.TotalFiles, 0d, 100d)
                        : null;
                reporter.Report(new LoaderInstallProgress(VerifyStage, detail, percent));
            });
        }

        await _installService.InstallAsync(versionId, mappedProgress, cancellationToken).ConfigureAwait(false);
    }

    private static bool MatchesLoader(string versionId, LoaderKind kind)
    {
        if (kind == LoaderKind.NeoForge)
        {
            return versionId.Contains("neoforge", StringComparison.OrdinalIgnoreCase);
        }

        // NeoForge ids also contain the substring "forge"; a Forge install must not adopt a NeoForge profile.
        return versionId.Contains("forge", StringComparison.OrdinalIgnoreCase)
            && !versionId.Contains("neoforge", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadStringProperty(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static void ValidateVersionToken(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("版本字符串不能为空。", parameterName);
        }

        bool safe = !Path.IsPathRooted(value)
            && value is not "." and not ".."
            && !value.Contains("..", StringComparison.Ordinal)
            && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
            && value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' or '+');
        if (!safe)
        {
            throw new ArgumentException($"版本字符串“{value}”包含路径分隔符或非法字符，已拒绝使用。", parameterName);
        }
    }

    private static bool IsSafeVersionToken(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value is not "." and not ".."
        && !Path.IsPathRooted(value)
        && !value.Contains("..", StringComparison.Ordinal)
        && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' or '+');

    private static bool IsWithinDirectory(string fullPath, string directory)
    {
        string root = Path.GetFullPath(directory);
        if (!root.EndsWith(Path.DirectorySeparatorChar))
        {
            root += Path.DirectorySeparatorChar;
        }

        return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeFileName(string fileName)
    {
        char[] invalidCharacters = Path.GetInvalidFileNameChars();
        string sanitized = string.Concat(fileName.Select(character =>
            Array.IndexOf(invalidCharacters, character) >= 0 || character is '/' or '\\' ? '_' : character));
        return sanitized is "." or ".." ? "_" : sanitized;
    }

    private static void TryDeleteFile(string path)
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
            // A leftover partial file is harmless; the next attempt overwrites it.
        }
        catch (UnauthorizedAccessException)
        {
            // A leftover partial file is harmless; the next attempt overwrites it.
        }
    }

    private sealed record CapturedProfile(string Id, string JsonPath, DateTime LastWriteTimeUtc);

    private sealed class DelegatingProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
