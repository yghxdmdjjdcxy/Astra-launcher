using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AstraLauncher.Authentication;

/// <summary>Exchanges an XSTS token for a Minecraft access token and reads the player's Minecraft profile.</summary>
public sealed class MinecraftAuthenticator : IMinecraftAuthenticator
{
    private const string LoginWithXboxUrl = "https://api.minecraftservices.com/authentication/login_with_xbox";
    private const string ProfileUrl = "https://api.minecraftservices.com/minecraft/profile";

    private static readonly Uri LoginWithXboxEndpoint = new(LoginWithXboxUrl, UriKind.Absolute);
    private static readonly Uri ProfileEndpoint = new(ProfileUrl, UriKind.Absolute);

    private readonly HttpClient _httpClient;
    private readonly ILogger<MinecraftAuthenticator> _logger;

    /// <summary>Creates the authenticator. The caller keeps ownership of <paramref name="httpClient"/>.</summary>
    public MinecraftAuthenticator(HttpClient httpClient, ILogger<MinecraftAuthenticator> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<MinecraftSessionInfo> LoginAsync(
        string xboxUserHash,
        string xstsToken,
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xboxUserHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(xstsToken);
        ArgumentNullException.ThrowIfNull(refreshToken);

        // The frozen interface passes the refresh token for token bookkeeping, but the Minecraft services exchange
        // only needs the XSTS token; the caller still owns and persists the refresh token.
        _ = refreshToken;

        string identityToken = string.Concat("XBL3.0 x=", xboxUserHash, ";", xstsToken);
        string payload = JsonSerializer.Serialize(new { identityToken });

        using var loginRequest = CreateJsonRequest(LoginWithXboxEndpoint, payload);
        (HttpStatusCode loginStatus, string loginBody) = await SendAndReadAsync(loginRequest, cancellationToken).ConfigureAwait(false);
        if (loginStatus is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            _logger.LogWarning("Minecraft login was rejected with HTTP {StatusCode}.", (int)loginStatus);
            throw new AccountAuthenticationException(
                AccountErrorCode.MinecraftNotOwned,
                "Minecraft 登录被拒绝，该账户可能未拥有 Minecraft Java 版。");
        }

        if (!IsSuccessStatusCode(loginStatus))
        {
            _logger.LogWarning("Minecraft login failed with HTTP {StatusCode}.", (int)loginStatus);
            throw CreateHttpFailure("Minecraft 登录", loginStatus);
        }

        using JsonDocument? loginDocument = TryParseJson(loginBody);
        if (loginDocument is null)
        {
            throw CreateInvalidResponse("Minecraft 登录响应不是有效的 JSON。");
        }

        string? accessToken = ReadString(loginDocument.RootElement, "access_token");
        int? expiresIn = ReadInt32(loginDocument.RootElement, "expires_in");
        if (string.IsNullOrWhiteSpace(accessToken) || expiresIn is null or <= 0)
        {
            throw CreateInvalidResponse("Minecraft 登录响应缺少 access_token 或 expires_in。");
        }

        DateTimeOffset expiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn.Value);
        using var profileRequest = new HttpRequestMessage(HttpMethod.Get, ProfileEndpoint);
        profileRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken!);
        (HttpStatusCode profileStatus, string profileBody) = await SendAndReadAsync(profileRequest, cancellationToken).ConfigureAwait(false);
        if (profileStatus == HttpStatusCode.NotFound)
        {
            throw new AccountAuthenticationException(
                AccountErrorCode.MinecraftNotOwned,
                "该账户未拥有 Minecraft Java 版。");
        }

        if (!IsSuccessStatusCode(profileStatus))
        {
            _logger.LogWarning("Minecraft profile request failed with HTTP {StatusCode}.", (int)profileStatus);
            throw CreateHttpFailure("Minecraft 档案请求", profileStatus);
        }

        using JsonDocument? profileDocument = TryParseJson(profileBody);
        if (profileDocument is null)
        {
            throw CreateInvalidResponse("Minecraft 档案响应不是有效的 JSON。");
        }

        string? playerName = ReadString(profileDocument.RootElement, "name");
        string? playerId = ReadString(profileDocument.RootElement, "id");
        if (string.IsNullOrWhiteSpace(playerName) || string.IsNullOrWhiteSpace(playerId))
        {
            throw CreateInvalidResponse("Minecraft 档案响应缺少 name 或 id。");
        }

        string playerUuid = FormatPlayerUuid(playerId!);
        _logger.LogInformation("Minecraft session established for player {PlayerName}.", playerName);

        // The profile response carries neither the XUID nor the launcher client id; AccountService fills both in
        // when it composes the persisted account and the launch arguments.
        return new MinecraftSessionInfo(
            playerName!,
            playerUuid,
            string.Empty,
            string.Empty,
            accessToken!,
            expiresAt);
    }

    private async Task<(HttpStatusCode StatusCode, string Body)> SendAndReadAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await _httpClient
                .SendAsync(request, cancellationToken)
                .ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return (response.StatusCode, body);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AccountAuthenticationException(
                AccountErrorCode.Network,
                "连接 Minecraft 服务超时。",
                exception);
        }
        catch (HttpRequestException exception)
        {
            throw new AccountAuthenticationException(
                AccountErrorCode.Network,
                "无法连接 Minecraft 服务。",
                exception);
        }
        catch (IOException exception)
        {
            throw new AccountAuthenticationException(
                AccountErrorCode.Network,
                "读取 Minecraft 服务响应失败。",
                exception);
        }
    }

    private static HttpRequestMessage CreateJsonRequest(Uri endpoint, string payload) =>
        new(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

    private static JsonDocument? TryParseJson(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string FormatPlayerUuid(string rawId)
    {
        string hex = rawId.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        if (hex.Length != 32 || !IsHex(hex))
        {
            throw CreateInvalidResponse("Minecraft 档案响应中的玩家 UUID 格式无效。");
        }

        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }

    private static bool IsHex(string value)
    {
        foreach (char character in value)
        {
            if (!char.IsAsciiHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }

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
}
