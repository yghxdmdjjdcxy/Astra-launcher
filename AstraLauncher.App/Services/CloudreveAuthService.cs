using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AstraLauncher.App.Services;

/// <summary>Cloudreve account credentials plus the generated WebDAV password.</summary>
public sealed record CloudreveWebDavAccount(string Email, string Password);

/// <summary>Registers/logs into Cloudreve over its REST API and provisions a WebDAV account for the launcher.</summary>
public interface ICloudreveAuthService
{
    Task<CloudreveWebDavAccount> LoginAsync(string serverUrl, string email, string password, CancellationToken cancellationToken = default);
    Task<CloudreveWebDavAccount> RegisterAsync(string serverUrl, string email, string password, CancellationToken cancellationToken = default);
    Task<CloudreveWebDavAccount> ProvisionWebDavAsync(string serverUrl, string accessToken, string email, CancellationToken cancellationToken = default);
}

/// <summary>Thin Cloudreve v4 REST client used to avoid showing the Cloudreve web page to launcher users.</summary>
public sealed class CloudreveAuthService : ICloudreveAuthService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<CloudreveAuthService> _logger;

    public CloudreveAuthService(ILogger<CloudreveAuthService> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("AstraLauncher/1.0");
    }

    public async Task<CloudreveWebDavAccount> LoginAsync(string serverUrl, string email, string password, CancellationToken cancellationToken = default)
    {
        string accessToken = await LoginTokenAsync(serverUrl, email, password, cancellationToken).ConfigureAwait(false);
        return await ProvisionWebDavAsync(serverUrl, accessToken, email, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reuses/creates the launcher WebDAV account using an access token obtained from the embedded browser.</summary>
    public async Task<CloudreveWebDavAccount> ProvisionWebDavAsync(
        string serverUrl,
        string accessToken,
        string email,
        CancellationToken cancellationToken = default)
    {
        string webDavPassword = await EnsureWebDavAccountAsync(serverUrl, accessToken, cancellationToken).ConfigureAwait(false);
        return new CloudreveWebDavAccount(email.Trim(), webDavPassword);
    }

    public async Task<CloudreveWebDavAccount> RegisterAsync(string serverUrl, string email, string password, CancellationToken cancellationToken = default)
    {
        string baseUrl = NormalizeBase(serverUrl);
        var payload = new { email = email.Trim(), password };
        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/api/v4/user")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(json);
        int code = GetInt(document.RootElement, "code");
        if (code != 0)
        {
            string message = GetString(document.RootElement, "msg") ?? "注册失败。";
            throw new InvalidOperationException(message);
        }

        _logger.LogInformation("Cloudreve registration succeeded for {Email}.", email);
        return await LoginAsync(baseUrl, email, password, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> LoginTokenAsync(string serverUrl, string email, string password, CancellationToken cancellationToken)
    {
        string baseUrl = NormalizeBase(serverUrl);
        var payload = new { email = email.Trim(), password };
        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/api/v4/session/token")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(json);
        int code = GetInt(document.RootElement, "code");
        if (code != 0)
        {
            string message = GetString(document.RootElement, "msg") ?? "邮箱或密码错误。";
            throw new UnauthorizedAccessException(message);
        }

        return document.RootElement.GetProperty("data").GetProperty("token").GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("Cloudreve 登录响应缺少 access_token。");
    }

    private async Task<string> EnsureWebDavAccountAsync(string serverUrl, string accessToken, CancellationToken cancellationToken)
    {
        string baseUrl = NormalizeBase(serverUrl);
        string? existing = await FindExistingWebDavPasswordAsync(baseUrl, accessToken, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(existing))
        {
            return existing;
        }

        using var request = new HttpRequestMessage(HttpMethod.Put, baseUrl + "/api/v4/devices/dav")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { uri = "cloudreve://my/", name = "AstraLauncher" }),
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new UnauthorizedAccessException("Cloudreve 账号无 WebDAV 权限，请联系管理员为对应用户组开启 WebDAV。");
        }

        using JsonDocument document = JsonDocument.Parse(json);
        int code = GetInt(document.RootElement, "code");
        if (code != 0)
        {
            string message = GetString(document.RootElement, "msg") ?? "创建 WebDAV 账号失败。";
            throw new InvalidOperationException(message);
        }

        string? password = document.RootElement.GetProperty("data").GetProperty("password").GetString();
        return password ?? throw new InvalidOperationException("Cloudreve 未返回 WebDAV 密码。");
    }

    private async Task<string?> FindExistingWebDavPasswordAsync(string baseUrl, string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/api/v4/devices/dav?page_size=20");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(json);
        if (GetInt(document.RootElement, "code") != 0 ||
            !document.RootElement.TryGetProperty("data", out JsonElement data) ||
            !data.TryGetProperty("accounts", out JsonElement accounts) ||
            accounts.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (JsonElement account in accounts.EnumerateArray())
        {
            if (GetString(account, "name") == "AstraLauncher" &&
                GetString(account, "uri")?.StartsWith("cloudreve://my", StringComparison.OrdinalIgnoreCase) == true)
            {
                return GetString(account, "password");
            }
        }

        return null;
    }

    private static string NormalizeBase(string url) => url.Trim().TrimEnd('/');

    private static string? GetString(JsonElement root, string property) =>
        root.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int GetInt(JsonElement root, string property) =>
        root.TryGetProperty(property, out JsonElement value) && value.TryGetInt32(out int parsed) ? parsed : -1;
}
