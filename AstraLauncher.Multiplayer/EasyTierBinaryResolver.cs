using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using AstraLauncher.Core.Services;
using AstraLauncher.Downloader;
using Microsoft.Extensions.Logging;

namespace AstraLauncher.Multiplayer;

/// <summary>
/// Locates easytier-core.exe / easytier-cli.exe, downloading and safely extracting the official Windows
/// release when they are missing. The whole official ZIP is kept (DLLs and drivers included) because
/// EasyTier needs more than just the two executables.
/// </summary>
public sealed class EasyTierBinaryResolver
{
    /// <summary>Number of parallel HTTP Range requests used for one file.</summary>
    public const int DownloadThreads = 32;

    public const string Version = "2.6.4";
    public const string ZipFileName = "easytier-windows-x86_64-v2.6.4.zip";
    public const string DownloadUrl = "https://github.com/EasyTier/EasyTier/releases/download/v2.6.4/" + ZipFileName;

    /// <summary>Size of the official v2.6.4 Windows x86_64 ZIP; rejects truncated downloads before extraction.</summary>
    public const long ExpectedZipBytes = 32_657_720;

    private readonly HttpClient _http;
    private readonly ILauncherPaths _paths;
    private readonly ILogger<EasyTierBinaryResolver> _logger;

    public EasyTierBinaryResolver(HttpClient http, ILauncherPaths paths, ILogger<EasyTierBinaryResolver> logger)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(logger);
        _http = http;
        _paths = paths;
        _logger = logger;
    }

    public string ToolsDirectory => Path.Combine(_paths.ApplicationDataDirectory, "tools", "easytier");

    /// <summary>Returns the binaries if a configured directory or the managed install contains them.</summary>
    public EasyTierBinaries? TryResolve(string? configuredDirectory)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configuredDirectory))
        {
            candidates.Add(configuredDirectory);
        }

        candidates.Add(Path.Combine(ToolsDirectory, "v" + Version));
        candidates.Add(ToolsDirectory);

        foreach (string candidate in candidates)
        {
            EasyTierBinaries? found = FindIn(candidate);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    public async Task<EasyTierBinaries> EnsureInstalledAsync(
        string? configuredDirectory,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        EasyTierBinaries? existing = TryResolve(configuredDirectory);
        if (existing is not null)
        {
            return existing;
        }

        if (!string.IsNullOrWhiteSpace(configuredDirectory))
        {
            throw new EasyTierException(
                $"在指定的 EasyTier 目录中未找到 easytier-core.exe 与 easytier-cli.exe：{configuredDirectory}。请确认已完整解压官方 ZIP。");
        }

        Directory.CreateDirectory(ToolsDirectory);
        string zipPath = Path.Combine(ToolsDirectory, ZipFileName);
        if (File.Exists(zipPath))
        {
            File.Delete(zipPath); // never resume a possibly stale/corrupt archive
        }

        progress?.Report($"正在下载 EasyTier {Version}（官方 GitHub Release · {DownloadThreads} 线程）…");
        _logger.LogInformation(
            "Downloading EasyTier {Version} from {Url} with up to {Threads} parallel Range segments.",
            Version, DownloadUrl, DownloadThreads);

        try
        {
            // The shared HttpClient has an infinite timeout, so cap the whole download explicitly.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            CancellationToken token = timeout.Token;

            // DownloadQueue only splits a file when its size is known, so probe it first. If neither
            // HEAD nor a Range probe yields a size, the queue transparently falls back to one connection.
            long? expectedSize = await ProbeDownloadSizeAsync(token).ConfigureAwait(false);
            using var queue = new DownloadQueue(_http, CreateDownloadOptions());
            DownloadRequest request = DownloadRequest.Create(new Uri(DownloadUrl), zipPath, expectedSize: expectedSize);
            IDownloadHandle handle = queue.Enqueue(request);
            using CancellationTokenRegistration registration = token.Register(handle.Cancel);

            void OnProgress(object? sender, DownloadProgress value)
            {
                if (value.TotalBytes is long total && total > 0)
                {
                    int percent = (int)Math.Clamp(value.BytesReceived * 100 / total, 0L, 100L);
                    progress?.Report($"正在下载 EasyTier … {percent}%（{DownloadThreads} 线程）");
                }
            }

            handle.ProgressChanged += OnProgress;
            try
            {
                await handle.Completion.ConfigureAwait(false);
            }
            finally
            {
                handle.ProgressChanged -= OnProgress;
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException or InvalidOperationException)
        {
            throw new EasyTierException(
                "自动下载 EasyTier 失败。请从 https://github.com/EasyTier/EasyTier/releases 下载 " + ZipFileName
                + "，完整解压后把目录路径填进「EasyTier 目录」。", exception);
        }

        progress?.Report("正在解压 EasyTier …");
        var zipInfo = new FileInfo(zipPath);
        if (zipInfo.Length != ExpectedZipBytes)
        {
            TryDelete(zipPath);
            throw new EasyTierException(
                $"EasyTier 压缩包大小不正确（{zipInfo.Length} / {ExpectedZipBytes} 字节），可能下载不完整或镜像被篡改，请重试。");
        }

        string destination = Path.Combine(ToolsDirectory, "v" + Version);
        ExtractSafely(zipPath, destination);

        EasyTierBinaries? installed = TryResolve(destination);
        return installed ?? throw new EasyTierException(
            "解压后的目录中缺少 easytier-core.exe 或 easytier-cli.exe。请检查官方 ZIP 是否完整。");
    }

    /// <summary>Download queue tuned for the single large EasyTier ZIP: up to 32 parallel Range requests.</summary>
    public static DownloadQueueOptions CreateDownloadOptions() => new()
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

    /// <summary>
    /// Gets the archive size so the queue can split it into parallel Range requests. HEAD is preferred;
    /// some GitHub CDN edges reject it, so a 1-byte Range GET is used as a fallback. A null result makes
    /// the queue fall back to a single connection instead of failing.
    /// </summary>
    private async Task<long?> ProbeDownloadSizeAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, DownloadUrl);
            using HttpResponseMessage headResponse = await _http
                .SendAsync(head, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (headResponse.IsSuccessStatusCode
                && headResponse.Content.Headers.ContentLength is long headLength
                && headLength > 0)
            {
                return headLength;
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            _logger.LogDebug(exception, "HEAD size probe failed; trying a Range probe.");
        }

        try
        {
            using var probe = new HttpRequestMessage(HttpMethod.Get, DownloadUrl);
            probe.Headers.Range = new RangeHeaderValue(0, 0);
            using HttpResponseMessage response = await _http
                .SendAsync(probe, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.Content.Headers.ContentRange?.Length is long rangeLength && rangeLength > 0)
            {
                return rangeLength;
            }

            return response.Content.Headers.ContentLength is long contentLength && contentLength > 0
                ? contentLength
                : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            _logger.LogDebug(exception, "Range size probe failed; falling back to a single connection.");
            return null;
        }
    }

    /// <summary>Extracts a ZIP while rejecting entry paths that escape the destination (zip-slip).</summary>
    public static void ExtractSafely(string zipPath, string destinationDirectory)
    {
        string root = Path.GetFullPath(destinationDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(destinationDirectory);
        using ZipArchive archive = ZipFile.OpenRead(zipPath);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue; // directory entry
            }

            string target = Path.GetFullPath(Path.Combine(destinationDirectory, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new EasyTierException($"压缩包包含越界路径，已拒绝解压：{entry.FullName}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    private EasyTierBinaries? FindIn(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return null;
        }

        try
        {
            string? core = Directory
                .EnumerateFiles(directory, "easytier-core.exe", SearchOption.AllDirectories)
                .OrderBy(path => path.Length)
                .ThenBy(path => path, StringComparer.Ordinal)
                .FirstOrDefault();
            if (core is null)
            {
                return null;
            }

            string root = Path.GetDirectoryName(core)!;
            string cli = Path.Combine(root, "easytier-cli.exe");
            if (!File.Exists(cli))
            {
                cli = Directory
                    .EnumerateFiles(directory, "easytier-cli.exe", SearchOption.AllDirectories)
                    .FirstOrDefault() ?? string.Empty;
            }

            return File.Exists(cli) ? new EasyTierBinaries(core, cli, root) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Cannot scan EasyTier directory {Directory}.", directory);
            return null;
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
            // Best effort; a later install attempt overwrites it.
        }
    }
}
