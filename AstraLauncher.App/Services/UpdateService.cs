using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AstraLauncher.App.Services;

/// <summary>An available GitHub release that contains a newer Windows executable.</summary>
public sealed record LauncherUpdate(
    string Tag,
    string Name,
    Uri DownloadUri,
    long Size,
    string? Sha256,
    Uri ReleasePage);

/// <summary>Checks GitHub Releases, downloads and applies launcher self-updates.</summary>
public interface IUpdateService
{
    /// <summary>Gets the running launcher version as <c>major.minor.patch</c>.</summary>
    string CurrentVersion { get; }

    /// <summary>Returns the newest update, or <see langword="null"/> when already up to date.</summary>
    Task<LauncherUpdate?> CheckAsync(CancellationToken cancellationToken = default);

    /// <summary>Downloads the update executable into a temporary directory and returns its path.</summary>
    Task<string> DownloadAsync(LauncherUpdate update, IProgress<double>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Replaces the current executable using a detached PowerShell script and restarts the launcher.</summary>
    void ApplyAndRestart(string downloadedExePath);
}

/// <summary>GitHub Releases based updater for the portable single-file executable.</summary>
public sealed class UpdateService : IUpdateService
{
    private const string LatestReleaseApi = "https://api.github.com/repos/yghxdmdjjdcxy/Astra-launcher/releases/latest";

    private readonly HttpClient _httpClient;
    private readonly ILogger<UpdateService> _logger;

    public UpdateService(ILogger<UpdateService> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("AstraLauncher/" + CurrentVersion);
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    /// <inheritdoc />
    public string CurrentVersion
    {
        get
        {
            Version version = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0, 0);
            return $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
        }
    }

    /// <inheritdoc />
    public async Task<LauncherUpdate?> CheckAsync(CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await _httpClient.GetAsync(LatestReleaseApi, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Update check failed with HTTP {StatusCode}.", (int)response.StatusCode);
            throw new HttpRequestException($"GitHub Releases 返回 HTTP {(int)response.StatusCode}。");
        }

        string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.TryGetProperty("draft", out JsonElement draft) && draft.ValueKind == JsonValueKind.True)
        {
            return null;
        }

        if (root.TryGetProperty("prerelease", out JsonElement prerelease) && prerelease.ValueKind == JsonValueKind.True)
        {
            return null;
        }

        string? tag = GetString(root, "tag_name");
        if (string.IsNullOrWhiteSpace(tag) || !IsNewerTag(tag, CurrentVersion))
        {
            return null;
        }

        string? releaseUrl = GetString(root, "html_url");
        string? sha256 = null;
        Uri? downloadUri = null;
        long size = 0;
        if (root.TryGetProperty("assets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement asset in assets.EnumerateArray())
            {
                string? name = GetString(asset, "name");
                if (string.IsNullOrWhiteSpace(name) ||
                    !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string? url = GetString(asset, "browser_download_url");
                if (url is not null && Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed))
                {
                    downloadUri = parsed;
                    size = asset.TryGetProperty("size", out JsonElement sizeElement) && sizeElement.TryGetInt64(out long parsedSize)
                        ? parsedSize
                        : 0;
                    string? digest = GetString(asset, "digest");
                    if (digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    {
                        sha256 = digest["sha256:".Length..];
                    }

                    break;
                }
            }
        }

        if (downloadUri is null)
        {
            _logger.LogWarning("Release {Tag} has no .exe asset.", tag);
            return null;
        }

        _logger.LogInformation("Update available: {Tag} ({Size} bytes).", tag, size);
        return new LauncherUpdate(
            tag,
            GetString(root, "name") ?? tag,
            downloadUri,
            size,
            sha256,
            Uri.TryCreate(releaseUrl, UriKind.Absolute, out Uri? page) ? page : new Uri("https://github.com/yghxdmdjjdcxy/Astra-launcher/releases"));
    }

    /// <inheritdoc />
    public async Task<string> DownloadAsync(
        LauncherUpdate update,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        string directory = Path.Combine(Path.GetTempPath(), "AstraLauncher", "Updates");
        Directory.CreateDirectory(directory);
        string safeTag = string.Concat(update.Tag.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        string target = Path.Combine(directory, $"AstraLauncher-{safeTag}.exe");
        string part = target + ".part";

        using HttpResponseMessage response = await _httpClient
            .GetAsync(update.DownloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? update.Size;
        try
        {
            await using (Stream input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                byte[] buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    received += read;
                    if (total > 0)
                    {
                        progress?.Report(Math.Clamp((double)received / total * 100, 0, 100));
                    }
                }
            }

            File.Move(part, target, overwrite: true);
            if (update.Size > 0 && new FileInfo(target).Length != update.Size)
            {
                throw new InvalidDataException("下载文件大小与 GitHub Release 不一致。");
            }

            if (!string.IsNullOrWhiteSpace(update.Sha256))
            {
                string actual = await ComputeSha256Async(target, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(actual, update.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(target);
                    throw new InvalidDataException("更新文件 SHA-256 校验失败。");
                }
            }

            _logger.LogInformation("Update {Tag} downloaded to {Path}.", update.Tag, target);
            return target;
        }
        catch
        {
            try
            {
                if (File.Exists(part))
                {
                    File.Delete(part);
                }
            }
            catch (IOException)
            {
                // Best effort cleanup.
            }

            throw;
        }
    }

    /// <inheritdoc />
    public void ApplyAndRestart(string downloadedExePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(downloadedExePath);
        string currentExe = Environment.ProcessPath
            ?? Path.Combine(AppContext.BaseDirectory, "AstraLauncher.exe");
        if (!currentExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("当前进程不是 Windows 可执行文件，无法自动更新。");
        }

        string scriptPath = Path.Combine(Path.GetTempPath(), "AstraLauncher", "Updates", $"apply-update-{Guid.NewGuid():N}.ps1");
        Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
        File.WriteAllText(scriptPath, UpdaterScript, Encoding.UTF8);

        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
        startInfo.ArgumentList.Add(currentExe);
        startInfo.ArgumentList.Add(downloadedExePath);
        Process? updater = Process.Start(startInfo);
        if (updater is null)
        {
            throw new InvalidOperationException("无法启动更新程序。");
        }

        _logger.LogInformation("Update script started (PID {Pid}); shutting down for restart.", updater.Id);
        System.Windows.Application.Current?.Shutdown();
    }

    /// <summary>Returns true when <paramref name="tag"/> (e.g. <c>v1.2.3</c>) is newer than <paramref name="current"/>.</summary>
    public static bool IsNewerTag(string tag, Version current)
    {
        ArgumentNullException.ThrowIfNull(current);
        Version? parsed = ParseVersion(tag);
        return parsed is not null && parsed > current;
    }

    /// <summary>Returns true when <paramref name="tag"/> is newer than the running launcher version string.</summary>
    public static bool IsNewerTag(string tag, string currentVersion)
    {
        Version? current = Version.TryParse(NormalizeVersionText(currentVersion), out Version? parsed)
            ? parsed
            : new Version(0, 0, 0);
        return IsNewerTag(tag, current);
    }

    private static Version? ParseVersion(string tag)
    {
        string normalized = NormalizeVersionText(tag.Trim().TrimStart('v', 'V'));
        return Version.TryParse(normalized, out Version? parsed) ? parsed : null;
    }

    private static string NormalizeVersionText(string value)
    {
        int separator = value.IndexOfAny(['-', '+']);
        return separator >= 0 ? value[..separator] : value;
    }

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private const string UpdaterScript = """
        $ErrorActionPreference = 'SilentlyContinue'
        $pidToWait = [int]$args[0]
        $target = $args[1]
        $source = $args[2]
        try { Wait-Process -Id $pidToWait -Timeout 120 -ErrorAction SilentlyContinue } catch { }
        Start-Sleep -Milliseconds 800
        $staged = "$target.new"
        $old = "$target.old"
        for ($i = 0; $i -lt 20; $i++) {
            try {
                Copy-Item -LiteralPath $source -Destination $staged -Force -ErrorAction Stop
                if (Test-Path -LiteralPath $old) { Remove-Item -LiteralPath $old -Force -ErrorAction SilentlyContinue }
                Move-Item -LiteralPath $target -Destination $old -Force -ErrorAction Stop
                Move-Item -LiteralPath $staged -Destination $target -Force -ErrorAction Stop
                break
            } catch {
                Start-Sleep -Milliseconds 500
            }
        }
        Start-Process -FilePath $target
        Start-Sleep -Seconds 2
        Remove-Item -LiteralPath $source -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $old -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
        """;
}
