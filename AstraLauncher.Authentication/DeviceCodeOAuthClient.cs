using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AstraLauncher.Authentication;

/// <summary>
/// Microsoft identity platform device-code and refresh-token client for the consumer authority. The
/// <c>device_code</c> returned by the device authorization endpoint is cached per client id because the frozen
/// <see cref="DeviceCodeInfo"/> contract exposes only the user-facing prompt.
/// </summary>
public sealed class DeviceCodeOAuthClient : IDeviceCodeOAuthClient
{
    private const string AuthorityBaseUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/";
    private const string Scope = "XboxLive.signin offline_access";
    private const string DeviceCodeGrantType = "urn:ietf:params:oauth:grant-type:device_code";
    private const string RefreshTokenGrantType = "refresh_token";
    private const string WaitingForUserMessage = "等待用户在浏览器中完成登录…";

    private static readonly Uri DeviceCodeEndpoint = new(string.Concat(AuthorityBaseUrl, "devicecode"), UriKind.Absolute);
    private static readonly Uri TokenEndpoint = new(string.Concat(AuthorityBaseUrl, "token"), UriKind.Absolute);
    private static readonly TimeSpan DefaultPollingInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SlowDownIncrement = TimeSpan.FromSeconds(5);

    private readonly HttpClient _httpClient;
    private readonly ILogger<DeviceCodeOAuthClient> _logger;
    private readonly ConcurrentDictionary<string, PendingDeviceCode> _pendingDeviceCodes = new(StringComparer.Ordinal);

    /// <summary>Creates the client. The caller keeps ownership of <paramref name="httpClient"/>.</summary>
    public DeviceCodeOAuthClient(HttpClient httpClient, ILogger<DeviceCodeOAuthClient> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<DeviceCodeInfo> RequestDeviceCodeAsync(string clientId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);

        using var request = new HttpRequestMessage(HttpMethod.Post, DeviceCodeEndpoint)
        {
            Content = CreateFormContent(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["client_id"] = clientId,
                ["scope"] = Scope,
            }),
        };

        (HttpStatusCode statusCode, string json) = await SendAndReadAsync(request, cancellationToken).ConfigureAwait(false);
        if (!IsSuccessStatusCode(statusCode))
        {
            _logger.LogWarning("Microsoft device-code request failed with HTTP {StatusCode}.", (int)statusCode);
            throw CreateHttpFailure("Microsoft 设备代码请求", statusCode);
        }

        (DeviceCodeInfo info, string deviceCode) = ParseDeviceCode(json);
        _pendingDeviceCodes[clientId] = new PendingDeviceCode(deviceCode, info.UserCode);
        _logger.LogInformation(
            "Microsoft device code issued for client {ClientId} with user code {UserCode}.",
            clientId,
            info.UserCode);
        return info;
    }

    /// <inheritdoc />
    public async Task<MicrosoftTokenSet> WaitForTokenAsync(
        string clientId,
        DeviceCodeInfo deviceCode,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentNullException.ThrowIfNull(deviceCode);

        // The frozen DeviceCodeInfo record does not carry the secret device_code, so it must come from the request
        // that produced this prompt. Polling an externally constructed prompt cannot work and fails fast.
        if (!_pendingDeviceCodes.TryGetValue(clientId, out PendingDeviceCode? pending))
        {
            throw CreateInvalidResponse("设备代码登录会话已失效，请重新发起 Microsoft 登录。");
        }

        if (!string.Equals(pending.UserCode, deviceCode.UserCode, StringComparison.Ordinal))
        {
            throw CreateInvalidResponse("设备代码登录会话与当前请求不匹配，请重新发起 Microsoft 登录。");
        }

        TimeSpan interval = deviceCode.Interval > TimeSpan.Zero ? deviceCode.Interval : DefaultPollingInterval;
        progress?.Report(WaitingForUserMessage);
        _logger.LogDebug("Polling the Microsoft token endpoint for user code {UserCode}.", deviceCode.UserCode);

        while (DateTimeOffset.UtcNow < deviceCode.ExpiresAt)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string json;
            HttpStatusCode statusCode;
            using (var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint))
            {
                request.Content = CreateFormContent(new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["grant_type"] = DeviceCodeGrantType,
                    ["client_id"] = clientId,
                    ["device_code"] = pending.DeviceCode,
                });
                (statusCode, json) = await SendAndReadAsync(request, cancellationToken).ConfigureAwait(false);
            }

            TokenPayload? payload = ParseTokenPayload(json);
            if (payload is null)
            {
                throw CreateInvalidResponse("Microsoft 登录服务返回了无法解析的响应。");
            }

            if (!string.IsNullOrEmpty(payload.Error))
            {
                switch (payload.Error)
                {
                    case "authorization_pending":
                        _logger.LogDebug("Microsoft device-code authorization is still pending.");
                        break;
                    case "slow_down":
                        interval += SlowDownIncrement;
                        _logger.LogDebug(
                            "Microsoft requested a slower device-code poll; the interval is now {Interval}.",
                            interval);
                        break;
                    case "expired_token":
                        throw CreateExpiredException(clientId);
                    case "authorization_declined":
                    case "access_denied":
                        _ = _pendingDeviceCodes.TryRemove(clientId, out _);
                        throw new AccountAuthenticationException(
                            AccountErrorCode.AuthorizationDeclined,
                            "用户拒绝了 Microsoft 登录请求。");
                    default:
                        _logger.LogWarning(
                            "Microsoft device-code flow failed with OAuth error {OAuthError}.",
                            payload.Error);
                        throw CreateInvalidResponse(string.Concat("Microsoft 登录失败（", payload.Error, "）。"));
                }
            }
            else if (IsSuccessStatusCode(statusCode))
            {
                MicrosoftTokenSet tokens = CreateTokenSet(
                    payload,
                    "Microsoft 登录响应缺少 access_token、refresh_token 或 expires_in。");
                _ = _pendingDeviceCodes.TryRemove(clientId, out _);
                _logger.LogInformation("Microsoft access token acquired through the device-code flow.");
                return tokens;
            }
            else
            {
                _logger.LogWarning("Microsoft token polling failed with HTTP {StatusCode}.", (int)statusCode);
                throw CreateHttpFailure("Microsoft 令牌请求", statusCode);
            }

            if (DateTimeOffset.UtcNow >= deviceCode.ExpiresAt)
            {
                break;
            }

            TimeSpan remaining = deviceCode.ExpiresAt - DateTimeOffset.UtcNow;
            TimeSpan delay = interval < remaining ? interval : remaining;
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        throw CreateExpiredException(clientId);
    }

    /// <inheritdoc />
    public async Task<MicrosoftTokenSet> RefreshAsync(
        string clientId,
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);

        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = CreateFormContent(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["grant_type"] = RefreshTokenGrantType,
                ["client_id"] = clientId,
                ["refresh_token"] = refreshToken,
                ["scope"] = Scope,
            }),
        };

        (HttpStatusCode statusCode, string json) = await SendAndReadAsync(request, cancellationToken).ConfigureAwait(false);
        TokenPayload? payload = ParseTokenPayload(json);
        if (payload is null)
        {
            throw CreateInvalidResponse("Microsoft 令牌刷新服务返回了无法解析的响应。");
        }

        if (!string.IsNullOrEmpty(payload.Error))
        {
            if (string.Equals(payload.Error, "invalid_grant", StringComparison.Ordinal))
            {
                _logger.LogWarning("The Microsoft refresh token is no longer valid.");
                throw new AccountAuthenticationException(
                    AccountErrorCode.SessionExpired,
                    "Microsoft 登录会话已过期，请重新登录。");
            }

            _logger.LogWarning(
                "Microsoft token refresh failed with OAuth error {OAuthError}.",
                payload.Error);
            throw CreateInvalidResponse(string.Concat("Microsoft 令牌刷新失败（", payload.Error, "）。"));
        }

        if (!IsSuccessStatusCode(statusCode))
        {
            _logger.LogWarning("Microsoft token refresh failed with HTTP {StatusCode}.", (int)statusCode);
            throw CreateHttpFailure("Microsoft 令牌刷新", statusCode);
        }

        MicrosoftTokenSet tokens = CreateTokenSet(
            payload,
            "Microsoft 令牌刷新响应缺少 access_token、refresh_token 或 expires_in。");
        _logger.LogInformation("Microsoft access token refreshed.");
        return tokens;
    }

    private async Task<(HttpStatusCode StatusCode, string Json)> SendAndReadAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await _httpClient
                .SendAsync(request, cancellationToken)
                .ConfigureAwait(false);
            string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return (response.StatusCode, json);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AccountAuthenticationException(
                AccountErrorCode.Network,
                "连接 Microsoft 登录服务超时。",
                exception);
        }
        catch (HttpRequestException exception)
        {
            throw new AccountAuthenticationException(
                AccountErrorCode.Network,
                "无法连接 Microsoft 登录服务。",
                exception);
        }
        catch (IOException exception)
        {
            throw new AccountAuthenticationException(
                AccountErrorCode.Network,
                "读取 Microsoft 登录服务响应失败。",
                exception);
        }
    }

    private static (DeviceCodeInfo Info, string DeviceCode) ParseDeviceCode(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw CreateInvalidResponse("Microsoft 设备代码响应不是 JSON 对象。");
            }

            string? deviceCode = ReadString(root, "device_code");
            string? userCode = ReadString(root, "user_code");
            string? verificationUri = ReadString(root, "verification_uri");
            int? expiresIn = ReadInt32(root, "expires_in");
            if (string.IsNullOrWhiteSpace(deviceCode) ||
                string.IsNullOrWhiteSpace(userCode) ||
                string.IsNullOrWhiteSpace(verificationUri) ||
                expiresIn is null or <= 0)
            {
                throw CreateInvalidResponse("Microsoft 设备代码响应缺少必要字段。");
            }

            string? verificationUriComplete = ReadString(root, "verification_uri_complete");
            string? message = ReadString(root, "message");
            int? intervalSeconds = ReadInt32(root, "interval");
            TimeSpan interval = intervalSeconds is > 0
                ? TimeSpan.FromSeconds(intervalSeconds.Value)
                : DefaultPollingInterval;
            string prompt = string.IsNullOrWhiteSpace(message)
                ? string.Concat("请访问 ", verificationUri, " 并输入代码 ", userCode, "。")
                : message!;
            var info = new DeviceCodeInfo(
                userCode!,
                verificationUri!,
                verificationUriComplete,
                prompt,
                DateTimeOffset.UtcNow.AddSeconds(expiresIn.Value),
                interval);
            return (info, deviceCode!);
        }
        catch (JsonException exception)
        {
            throw CreateInvalidResponse("Microsoft 设备代码响应不是有效的 JSON。", exception);
        }
    }

    private static TokenPayload? ParseTokenPayload(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return new TokenPayload(
                ReadString(root, "error"),
                ReadString(root, "access_token"),
                ReadString(root, "refresh_token"),
                ReadInt32(root, "expires_in"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static MicrosoftTokenSet CreateTokenSet(TokenPayload payload, string missingFieldsMessage)
    {
        string? accessToken = payload.AccessToken;
        string? refreshToken = payload.RefreshToken;
        int? expiresIn = payload.ExpiresIn;
        if (string.IsNullOrWhiteSpace(accessToken) ||
            string.IsNullOrWhiteSpace(refreshToken) ||
            expiresIn is null or <= 0)
        {
            throw CreateInvalidResponse(missingFieldsMessage);
        }

        return new MicrosoftTokenSet(
            accessToken!,
            refreshToken!,
            DateTimeOffset.UtcNow.AddSeconds(expiresIn.Value));
    }

    private static FormUrlEncodedContent CreateFormContent(Dictionary<string, string> values) => new(values);

    private static AccountAuthenticationException CreateHttpFailure(string operation, HttpStatusCode statusCode)
    {
        int numericStatus = (int)statusCode;
        return new AccountAuthenticationException(
            AccountErrorCode.InvalidResponse,
            string.Create(CultureInfo.InvariantCulture, $"{operation}失败（HTTP {numericStatus}）。"));
    }

    private static AccountAuthenticationException CreateInvalidResponse(string message, Exception? innerException = null) =>
        new(AccountErrorCode.InvalidResponse, message, innerException);

    private static bool IsSuccessStatusCode(HttpStatusCode statusCode) =>
        (int)statusCode is >= 200 and <= 299;

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out JsonElement property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int? ReadInt32(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out JsonElement property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out int number))
        {
            return number;
        }

        return property.ValueKind == JsonValueKind.String &&
               int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : null;
    }

    private AccountAuthenticationException CreateExpiredException(string clientId)
    {
        _ = _pendingDeviceCodes.TryRemove(clientId, out _);
        _logger.LogWarning("The Microsoft device code expired before the user completed sign-in.");
        return new AccountAuthenticationException(
            AccountErrorCode.DeviceCodeExpired,
            "设备代码已过期，请重新发起 Microsoft 登录。");
    }

    private sealed record PendingDeviceCode(string DeviceCode, string UserCode);

    private sealed record TokenPayload(string? Error, string? AccessToken, string? RefreshToken, int? ExpiresIn);
}
