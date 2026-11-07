using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
using AstraLauncher.Core.Models;
using AstraLauncher.Core.Services;
using Microsoft.Extensions.Logging;

namespace AstraLauncher.App.Services;

public sealed record FeedbackDraft(
    string Summary,
    string Description,
    string? Contact,
    bool IncludeLogs,
    IReadOnlyList<string> ScreenshotPaths);

public enum FeedbackSubmitState
{
    Submitted,
    Queued,
    Failed,
}

public sealed record FeedbackSubmitResult(FeedbackSubmitState State, string Message);

/// <summary>Builds the feedback package, uploads it, and retries failed uploads from a local queue.</summary>
public interface IFeedbackService
{
    Task<FeedbackSubmitResult> SubmitAsync(FeedbackDraft draft, CancellationToken cancellationToken = default);

    Task<int> FlushQueueAsync(CancellationToken cancellationToken = default);

    int PendingCount { get; }
}

/// <summary>
/// Creates <c>feedback.md</c> with embedded screenshots, losslessly re-encodes screenshots,
/// optionally bundles launcher/game logs, and uploads a ZIP to the configured feedback server.
/// </summary>
public sealed class FeedbackService : IFeedbackService
{
    private const long MaximumPackageBytes = 64L * 1024 * 1024;
    private readonly ISettingsService _settings;
    private readonly ILauncherPaths _paths;
    private readonly ILogger<FeedbackService> _logger;
    private readonly HttpClient _httpClient;

    public FeedbackService(ISettingsService settings, ILauncherPaths paths, ILogger<FeedbackService> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(logger);
        _settings = settings;
        _paths = paths;
        _logger = logger;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
    }

    public int PendingCount
    {
        get
        {
            try
            {
                return Directory.Exists(QueueDirectory)
                    ? Directory.EnumerateFiles(QueueDirectory, "*.zip").Count()
                    : 0;
            }
            catch (IOException)
            {
                return 0;
            }
        }
    }

    private string QueueDirectory => Path.Combine(_paths.ApplicationDataDirectory, "FeedbackQueue");

    public async Task<FeedbackSubmitResult> SubmitAsync(FeedbackDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (string.IsNullOrWhiteSpace(draft.Description))
        {
            return new FeedbackSubmitResult(FeedbackSubmitState.Failed, "请填写问题描述。");
        }

        string packagePath;
        try
        {
            packagePath = await BuildPackageAsync(draft, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            _logger.LogError(exception, "构建反馈包失败。");
            return new FeedbackSubmitResult(FeedbackSubmitState.Failed, "构建反馈包失败：" + exception.Message);
        }

        LauncherSettings settings = await _settings.GetAsync().ConfigureAwait(false);
        string endpoint = string.IsNullOrWhiteSpace(settings.FeedbackEndpoint)
            ? LauncherSettings.DefaultFeedbackEndpoint
            : settings.FeedbackEndpoint;

        try
        {
            await UploadAsync(endpoint, packagePath, cancellationToken).ConfigureAwait(false);
            TryDelete(packagePath);
            return new FeedbackSubmitResult(FeedbackSubmitState.Submitted, "反馈已提交，谢谢！");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException or JsonException)
        {
            _logger.LogWarning(exception, "反馈上传失败，已加入待提交队列。");
            string queuedPath = await MoveToQueueAsync(packagePath, cancellationToken).ConfigureAwait(false);
            return new FeedbackSubmitResult(
                FeedbackSubmitState.Queued,
                $"当前无法连接反馈服务器，已加入待提交队列（{Path.GetFileName(queuedPath)}）。联网后会自动重试。");
        }
    }

    public async Task<int> FlushQueueAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(QueueDirectory))
        {
            return 0;
        }

        LauncherSettings settings = await _settings.GetAsync().ConfigureAwait(false);
        string endpoint = string.IsNullOrWhiteSpace(settings.FeedbackEndpoint)
            ? LauncherSettings.DefaultFeedbackEndpoint
            : settings.FeedbackEndpoint;
        if (!await IsServerOnlineAsync(endpoint, cancellationToken).ConfigureAwait(false))
        {
            return 0;
        }

        int uploaded = 0;
        foreach (string file in Directory.EnumerateFiles(QueueDirectory, "*.zip").OrderBy(path => path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await UploadAsync(endpoint, file, cancellationToken).ConfigureAwait(false);
                TryDelete(file);
                uploaded++;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException or JsonException)
            {
                _logger.LogWarning(exception, "重试反馈队列上传失败：{File}", file);
                break;
            }
        }

        return uploaded;
    }

    private async Task<bool> IsServerOnlineAsync(string endpoint, CancellationToken cancellationToken)
    {
        try
        {
            var uri = new Uri(endpoint);
            string health = new Uri(uri, "/api/health").ToString();
            using var request = new HttpRequestMessage(HttpMethod.Get, health);
            using HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or UriFormatException or TaskCanceledException)
        {
            return false;
        }
    }

    private async Task UploadAsync(string endpoint, string packagePath, CancellationToken cancellationToken)
    {
        using var content = new MultipartFormDataContent();
        await using FileStream stream = File.OpenRead(packagePath);
        var fileContent = new StreamContent(stream);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
        content.Add(fileContent, "file", Path.GetFileName(packagePath));
        content.Add(new StringContent(Version), "launcherVersion");
        content.Add(new StringContent(Environment.MachineName), "machine");
        using HttpResponseMessage response = await _httpClient.PostAsync(endpoint, content, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"反馈服务器返回 {(int)response.StatusCode}：{body}");
        }

        using JsonDocument document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("ok", out JsonElement ok) || !ok.GetBoolean())
        {
            throw new HttpRequestException("反馈服务器未确认接收：" + body);
        }
    }

    private async Task<string> BuildPackageAsync(FeedbackDraft draft, CancellationToken cancellationToken)
    {
        string root = Path.Combine(Path.GetTempPath(), "AstraLauncher-Feedback", Guid.NewGuid().ToString("N"));
        string screenshots = Path.Combine(root, "screenshots");
        string logs = Path.Combine(root, "logs");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(screenshots);
        Directory.CreateDirectory(logs);

        var screenshotNames = new List<string>();
        for (int index = 0; index < draft.ScreenshotPaths.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string source = draft.ScreenshotPaths[index];
            if (!File.Exists(source))
            {
                continue;
            }

            string name = $"screenshot-{index + 1}.png";
            string target = Path.Combine(screenshots, name);
            LosslessCompressPng(source, target);
            screenshotNames.Add(name);
        }

        var logNames = new List<string>();
        if (draft.IncludeLogs)
        {
            logNames = CollectLogs(logs);
        }

        string markdown = BuildMarkdown(draft, screenshotNames, logNames);
        await File.WriteAllTextAsync(Path.Combine(root, "feedback.md"), markdown, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(root, "metadata.json"),
            JsonSerializer.Serialize(new
            {
                createdAt = DateTimeOffset.Now,
                version = Version,
                machine = Environment.MachineName,
                os = Environment.OSVersion.ToString(),
                summary = draft.Summary,
            }, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false),
            cancellationToken).ConfigureAwait(false);

        string zipPath = Path.Combine(Path.GetTempPath(), "AstraLauncher-Feedback", $"feedback-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.zip");
        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }

        ZipFile.CreateFromDirectory(root, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
        TryDeleteDirectory(root);
        if (new FileInfo(zipPath).Length > MaximumPackageBytes)
        {
            TryDelete(zipPath);
            throw new InvalidOperationException("反馈包超过 64 MB，请减少截图或取消附带日志。");
        }

        return zipPath;
    }

    private static string BuildMarkdown(FeedbackDraft draft, IReadOnlyList<string> screenshots, IReadOnlyList<string> logs)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Astra Launcher 反馈");
        builder.AppendLine();
        builder.AppendLine($"- 时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"- 版本：{Version}");
        builder.AppendLine($"- 系统：{Environment.OSVersion}");
        builder.AppendLine($"- 设备：{Environment.MachineName}");
        if (!string.IsNullOrWhiteSpace(draft.Contact))
        {
            builder.AppendLine($"- 联系方式：{draft.Contact}");
        }

        builder.AppendLine();
        builder.AppendLine("## 摘要");
        builder.AppendLine();
        builder.AppendLine(string.IsNullOrWhiteSpace(draft.Summary) ? "（未填写摘要）" : draft.Summary);
        builder.AppendLine();
        builder.AppendLine("## 详细描述");
        builder.AppendLine();
        builder.AppendLine(draft.Description);
        if (screenshots.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## 截图");
            builder.AppendLine();
            foreach (string name in screenshots)
            {
                builder.AppendLine($"![{name}](screenshots/{name})");
            }
        }

        if (logs.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## 日志");
            builder.AppendLine();
            foreach (string name in logs)
            {
                builder.AppendLine($"- [logs/{name}](logs/{name})");
            }
        }

        builder.AppendLine();
        builder.AppendLine("---");
        builder.AppendLine("由 Astra Launcher 内置反馈功能生成。");
        return builder.ToString();
    }

    private List<string> CollectLogs(string targetDirectory)
    {
        var result = new List<string>();
        var candidates = new List<string>();
        try
        {
            if (Directory.Exists(_paths.LogsDirectory))
            {
                candidates.AddRange(Directory.EnumerateFiles(_paths.LogsDirectory, "*.log").OrderByDescending(File.GetLastWriteTimeUtc).Take(3));
            }

            string latestGameLog = Path.Combine(_paths.MinecraftDirectory, "logs", "latest.log");
            if (File.Exists(latestGameLog))
            {
                candidates.Add(latestGameLog);
            }
        }
        catch (IOException)
        {
            return result;
        }

        foreach (string source in candidates)
        {
            try
            {
                var info = new FileInfo(source);
                if (info.Length <= 0 || info.Length > 16L * 1024 * 1024)
                {
                    continue;
                }

                string name = SanitizeFileName(Path.GetFileName(source));
                File.Copy(source, Path.Combine(targetDirectory, name), overwrite: true);
                result.Add(name);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Skip unreadable logs.
            }
        }

        return result;
    }

    private static void LosslessCompressPng(string sourcePath, string targetPath)
    {
        BitmapDecoder decoder = BitmapDecoder.Create(
            new Uri(sourcePath, UriKind.Absolute),
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        BitmapFrame frame = decoder.Frames[0];
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(frame));
        using FileStream output = File.Create(targetPath);
        encoder.Save(output);
    }

    private async Task<string> MoveToQueueAsync(string packagePath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(QueueDirectory);
        string target = Path.Combine(QueueDirectory, Path.GetFileName(packagePath));
        File.Move(packagePath, target, overwrite: true);
        await Task.CompletedTask;
        return target;
    }

    private static string SanitizeFileName(string name)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        return string.IsNullOrWhiteSpace(name) ? "log.txt" : name;
    }

    private static string Version =>
        typeof(FeedbackService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? typeof(FeedbackService).Assembly.GetName().Version?.ToString(3)
        ?? "1.0.8-beta";

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
