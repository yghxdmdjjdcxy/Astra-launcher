using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.RegularExpressions;
using AstraLauncher.Core.Services;
using AstraLauncher.Downloader;
using Microsoft.Extensions.Logging;

namespace AstraLauncher.App.Services;

/// <summary>A JDK major version offered by the one-click installer.</summary>
public sealed record JavaDownloadOption(int Major, string Display);

/// <summary>Downloads a JDK from a Chinese Adoptium mirror and installs it under the launcher data directory.</summary>
public interface IJavaInstallerService
{
    IReadOnlyList<JavaDownloadOption> Options { get; }

    /// <summary>Downloads and extracts the requested JDK, returning the path to <c>javaw.exe</c>.</summary>
    Task<string> InstallAsync(int majorVersion, IProgress<string>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>One-click Java installer backed by the TUNA Adoptium mirror and the segmented download queue.</summary>
public sealed class JavaInstallerService : IJavaInstallerService
{
    private static readonly string[] MirrorBases =
    [
        "https://mirrors.tuna.tsinghua.edu.cn/Adoptium",
        "https://mirrors.ustc.edu.cn/adoptium",
    ];

    private readonly DownloadQueue _downloadQueue;
    private readonly ILogger<JavaInstallerService> _logger;
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(60) };

    public JavaInstallerService(DownloadQueue downloadQueue, ILogger<JavaInstallerService> logger)
    {
        ArgumentNullException.ThrowIfNull(downloadQueue);
        ArgumentNullException.ThrowIfNull(logger);
        _downloadQueue = downloadQueue;
        _logger = logger;
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("AstraLauncher/1.0");
    }

    /// <inheritdoc />
    public IReadOnlyList<JavaDownloadOption> Options { get; } =
    [
        new(8, "Java 8"),
        new(17, "Java 17"),
        new(21, "Java 21"),
    ];

    /// <inheritdoc />
    public async Task<string> InstallAsync(int majorVersion, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (Options.All(option => option.Major != majorVersion))
        {
            throw new ArgumentOutOfRangeException(nameof(majorVersion), "仅支持 Java 8 / 17 / 21。");
        }

        progress?.Report($"正在查找 Java {majorVersion} 镜像文件…");
        Uri archiveUri = await ResolveArchiveUriAsync(majorVersion, cancellationToken).ConfigureAwait(false);
        string downloadDirectory = Path.Combine(Path.GetTempPath(), "AstraLauncher", "Java");
        Directory.CreateDirectory(downloadDirectory);
        string archivePath = Path.Combine(downloadDirectory, Path.GetFileName(archiveUri.LocalPath));

        try
        {
            progress?.Report($"正在下载 {Path.GetFileName(archiveUri.LocalPath)}…");
            DownloadRequest request = DownloadRequest.Create(archiveUri, archivePath, priority: DownloadPriority.High);
            IDownloadHandle handle = _downloadQueue.Enqueue(request);
            await using (cancellationToken.Register(handle.Cancel))
            {
                handle.ProgressChanged += (_, value) =>
                {
                    if (value.TotalBytes is > 0)
                    {
                        double percent = Math.Clamp(value.BytesReceived * 100d / value.TotalBytes.Value, 0, 100);
                        progress?.Report($"正在下载 Java {majorVersion}… {percent:0}%");
                    }
                };
                await handle.Completion.ConfigureAwait(false);
            }

            progress?.Report("下载完成，正在解压…");
            string targetRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AstraLauncher",
                "runtime",
                $"jdk-{majorVersion}-{DateTime.Now:yyyyMMddHHmmss}");
            Directory.CreateDirectory(targetRoot);
            ZipFile.ExtractToDirectory(archivePath, targetRoot, overwriteFiles: true);
            string? javaw = Directory.EnumerateFiles(targetRoot, "javaw.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (javaw is null)
            {
                throw new InvalidDataException("Java 压缩包中未找到 bin\\javaw.exe。");
            }

            _logger.LogInformation("Java {Major} installed at {Path}.", majorVersion, javaw);
            progress?.Report($"Java {majorVersion} 安装完成。");
            return javaw;
        }
        finally
        {
            try
            {
                if (File.Exists(archivePath))
                {
                    File.Delete(archivePath);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }

    private async Task<Uri> ResolveArchiveUriAsync(int majorVersion, CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        foreach (string baseUrl in MirrorBases)
        {
            string directoryUrl = $"{baseUrl}/{majorVersion}/jdk/x64/windows/";
            try
            {
                using HttpResponseMessage response = await _httpClient.GetAsync(directoryUrl, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                string html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                string? fileName = Regex.Matches(html, "href=\"([^\"]+\\.zip)\"")
                    .Select(match => match.Groups[1].Value)
                    .Where(name => name.Contains("jdk", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(name => name, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (fileName is not null)
                {
                    return new Uri(new Uri(directoryUrl), fileName);
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException)
            {
                lastError = exception;
                _logger.LogWarning(exception, "Java mirror {Mirror} failed.", directoryUrl);
            }
        }

        throw new InvalidOperationException("无法从 Java 镜像站获取安装包。", lastError);
    }
}
