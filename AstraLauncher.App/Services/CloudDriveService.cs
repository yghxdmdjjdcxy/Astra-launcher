using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace AstraLauncher.App.Services;

/// <summary>One file stored in the Cloudreve WebDAV directory.</summary>
public sealed record CloudDriveEntry(string Name, long Size, DateTimeOffset? Modified, string Href);

/// <summary>Minimal WebDAV client used to store compressed game-instance slots on a Cloudreve server.</summary>
public interface ICloudDriveService
{
    /// <summary>Verifies the WebDAV endpoint and credentials.</summary>
    Task TestAsync(string serverUrl, string userName, string password, CancellationToken cancellationToken = default);

    /// <summary>Lists files directly under the WebDAV endpoint.</summary>
    Task<IReadOnlyList<CloudDriveEntry>> ListAsync(string serverUrl, string userName, string password, CancellationToken cancellationToken = default);

    /// <summary>Uploads a local file into the WebDAV directory.</summary>
    Task UploadAsync(string serverUrl, string userName, string password, string localPath, string remoteName, IProgress<string>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Downloads a remote file into a local path.</summary>
    Task DownloadAsync(string serverUrl, string userName, string password, string remoteName, string localPath, IProgress<string>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>Cloudreve-compatible WebDAV client (also works with Nextcloud/other WebDAV servers).</summary>
public sealed class CloudDriveService : ICloudDriveService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<CloudDriveService> _logger;
    private static readonly HttpMethod Propfind = new("PROPFIND");

    public CloudDriveService(ILogger<CloudDriveService> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(60) };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("AstraLauncher/1.0");
    }

    /// <inheritdoc />
    public async Task TestAsync(string serverUrl, string userName, string password, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = CreateRequest(Propfind, serverUrl, userName, password, depth: "0");
        request.Content = new StringContent(PropfindBody, Encoding.UTF8, "application/xml");
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new UnauthorizedAccessException("WebDAV 用户名或密码错误。");
        }

        response.EnsureSuccessStatusCode();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CloudDriveEntry>> ListAsync(string serverUrl, string userName, string password, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = CreateRequest(Propfind, serverUrl, userName, password, depth: "1");
        request.Content = new StringContent(PropfindBody, Encoding.UTF8, "application/xml");
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        string xml = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var entries = new List<CloudDriveEntry>();
        XDocument document = XDocument.Parse(xml);
        foreach (XElement element in document.Descendants().Where(item => item.Name.LocalName == "response"))
        {
            string? href = element.Descendants().FirstOrDefault(item => item.Name.LocalName == "href")?.Value;
            string? name = element.Descendants().FirstOrDefault(item => item.Name.LocalName == "displayname")?.Value;
            if (string.IsNullOrWhiteSpace(href))
            {
                continue;
            }

            string decodedHref = Uri.UnescapeDataString(href);
            string decodedPath = decodedHref.TrimEnd('/');
            string fileName = string.IsNullOrWhiteSpace(name) ? decodedPath.Split('/').LastOrDefault() ?? string.Empty : name.Trim();
            if (string.IsNullOrWhiteSpace(fileName) || !decodedPath.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
            {
                // The root PROPFIND response is a directory; only files are listed in slots.
            }

            if (href.EndsWith('/') || string.IsNullOrWhiteSpace(fileName))
            {
                continue;
            }

            long size = 0;
            string? length = element.Descendants().FirstOrDefault(item => item.Name.LocalName == "getcontentlength")?.Value;
            _ = long.TryParse(length, out size);
            DateTimeOffset? modified = null;
            string? modifiedText = element.Descendants().FirstOrDefault(item => item.Name.LocalName == "getlastmodified")?.Value;
            if (DateTimeOffset.TryParse(modifiedText, out DateTimeOffset parsed))
            {
                modified = parsed;
            }

            entries.Add(new CloudDriveEntry(fileName, size, modified, href));
        }

        return entries;
    }

    /// <inheritdoc />
    public async Task UploadAsync(
        string serverUrl,
        string userName,
        string password,
        string localPath,
        string remoteName,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report("正在上传压缩包…");
        using HttpRequestMessage request = CreateRequest(HttpMethod.Put, Combine(serverUrl, remoteName), userName, password);
        request.Content = new StreamContent(File.OpenRead(localPath));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        _logger.LogInformation("Uploaded {File} to Cloudreve WebDAV.", remoteName);
        progress?.Report("上传完成。");
    }

    /// <inheritdoc />
    public async Task DownloadAsync(
        string serverUrl,
        string userName,
        string password,
        string remoteName,
        string localPath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report("正在下载压缩包…");
        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, Combine(serverUrl, remoteName), userName, password);
        using HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(localPath))!);
        await using (Stream input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var output = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }

        progress?.Report("下载完成。");
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string url, string userName, string password, string? depth = null)
    {
        var request = new HttpRequestMessage(method, NormalizeServerUrl(url));
        string credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{userName}:{password}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        if (depth is not null)
        {
            request.Headers.TryAddWithoutValidation("Depth", depth);
        }

        return request;
    }

    private static string NormalizeServerUrl(string url)
    {
        string trimmed = url.Trim();
        return trimmed.EndsWith('/') ? trimmed : trimmed + "/";
    }

    private static string Combine(string serverUrl, string remoteName) =>
        NormalizeServerUrl(serverUrl) + Uri.EscapeDataString(remoteName);

    private const string PropfindBody = """
        <?xml version="1.0" encoding="utf-8"?>
        <d:propfind xmlns:d="DAV:">
          <d:prop>
            <d:displayname/>
            <d:getcontentlength/>
            <d:getlastmodified/>
            <d:resourcetype/>
          </d:prop>
        </d:propfind>
        """;
}
