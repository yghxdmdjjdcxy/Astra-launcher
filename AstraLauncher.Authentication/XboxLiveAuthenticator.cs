using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AstraLauncher.Authentication;

/// <summary>Authenticates a Microsoft account with Xbox Live and authorizes it for the Minecraft relying party.</summary>
public sealed class XboxLiveAuthenticator : IXboxLiveAuthenticator
{
    private const string UserAuthenticateUrl = "https://user.auth.xboxlive.com/user/authenticate";
    private const string XstsAuthorizeUrl = "https://xsts.auth.xboxlive.com/xsts/authorize";
    private const string XboxLiveContractVersionHeader = "x-xbl-contract-version";
    private const string XboxLiveContractVersion = "1";
    private const string RpsSiteName = "user.auth.xboxlive.com";
    private const string UserAuthenticateRelyingParty = "http://auth.xboxlive.com";
    private const string MinecraftRelyingParty = "rp://api.minecraftservices.com/";
    private const long XboxNoAccountError = 2148916233;
    private const long XboxCountryUnavailableError = 2148916235;
    private const long XboxChildAccountError = 2148916236;
    private const long XboxChildAccountErrorVariant = 2148916237;
    private const long XboxBannedError = 2148916238;

    private static readonly Uri UserAuthenticateEndpoint = new(UserAuthenticateUrl, UriKind.Absolute);
    private static readonly Uri XstsAuthorizeEndpoint = new(XstsAuthorizeUrl, UriKind.Absolute);

    private readonly HttpClient _httpClient;
    private readonly ILogger<XboxLiveAuthenticator> _logger;

    /// <summary>Creates the authenticator. The caller keeps ownership of <paramref name="httpClient"/>.</summary>
    public XboxLiveAuthenticator(HttpClient httpClient, ILogger<XboxLiveAuthenticator> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<XboxLiveToken> AuthenticateAsync(
        string microsoftAccessToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(microsoftAccessToken);

        string payload = JsonSerializer.Serialize(new
        {
            Properties = new
            {
                AuthMethod = "RPS",
                SiteName = RpsSiteName,
                RpsTicket = string.Concat("d=", microsoftAccessToken),
            },
            RelyingParty = UserAuthenticateRelyingParty,
            TokenType = "JWT",
        });

        using var request = CreateJsonRequest(UserAuthenticateEndpoint, payload);
        (HttpStatusCode statusCode, string body) = await SendAndReadAsync(request, cancellationToken).ConfigureAwait(false);
        using JsonDocument? document = TryParseJson(body);
        if (document is null)
        {
            throw CreateInvalidResponse("Xbox Live 身份验证响应不是有效的 JSON。");
        }

        if (TryGetXErr(document.RootElement, out long xerr))
        {
            throw CreateXErrException(xerr);
        }

        if (!IsSuccessStatusCode(statusCode))
        {
            _logger.LogWarning("Xbox Live user authentication failed with HTTP {StatusCode}.", (int)statusCode);
            throw CreateHttpFailure("Xbox Live 身份验证", statusCode);
        }

        XboxLiveToken token = ParseUserToken(document.RootElement);
        _logger.LogInformation("Xbox Live user authentication succeeded.");
        return token;
    }

    /// <inheritdoc />
    public async Task<XstsToken> AuthorizeAsync(string xboxLiveToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xboxLiveToken);

        string payload = JsonSerializer.Serialize(new
        {
            Properties = new
            {
                SandboxId = "RETAIL",
                UserTokens = new[] { xboxLiveToken },
            },
            RelyingParty = MinecraftRelyingParty,
            TokenType = "JWT",
        });

        using var request = CreateJsonRequest(XstsAuthorizeEndpoint, payload);
        (HttpStatusCode statusCode, string body) = await SendAndReadAsync(request, cancellationToken).ConfigureAwait(false);
        using JsonDocument? document = TryParseJson(body);
        if (document is null)
        {
            throw CreateInvalidResponse("Xbox Live 授权响应不是有效的 JSON。");
        }

        if (TryGetXErr(document.RootElement, out long xerr))
        {
            throw CreateXErrException(xerr);
        }

        if (!IsSuccessStatusCode(statusCode))
        {
            _logger.LogWarning("Xbox Live XSTS authorization failed with HTTP {StatusCode}.", (int)statusCode);
            throw CreateHttpFailure("Xbox Live 授权", statusCode);
        }

        XstsToken token = ParseXstsToken(document.RootElement);
        _logger.LogInformation("Xbox Live XSTS authorization succeeded.");
        return token;
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
                "连接 Xbox Live 服务超时。",
                exception);
        }
        catch (HttpRequestException exception)
        {
            throw new AccountAuthenticationException(
                AccountErrorCode.Network,
                "无法连接 Xbox Live 服务。",
                exception);
        }
        catch (IOException exception)
        {
            throw new AccountAuthenticationException(
                AccountErrorCode.Network,
                "读取 Xbox Live 服务响应失败。",
                exception);
        }
    }

    private static HttpRequestMessage CreateJsonRequest(Uri endpoint, string payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(XboxLiveContractVersionHeader, XboxLiveContractVersion);
        return request;
    }

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

    private static XboxLiveToken ParseUserToken(JsonElement root)
    {
        (string token, string userHash, string xuid) = ParseToken(
            root,
            "Xbox Live 身份验证响应缺少 Token。",
            "Xbox Live 身份验证响应缺少 DisplayClaims.xui 用户信息。");
        return new XboxLiveToken(token, userHash, xuid);
    }

    private static XstsToken ParseXstsToken(JsonElement root)
    {
        (string token, string userHash, string xuid) = ParseToken(
            root,
            "Xbox Live 授权响应缺少 Token。",
            "Xbox Live 授权响应缺少 DisplayClaims.xui 用户信息。");
        return new XstsToken(token, userHash, xuid);
    }

    private static (string Token, string UserHash, string Xuid) ParseToken(
        JsonElement root,
        string missingTokenMessage,
        string missingClaimsMessage)
    {
        string? token = ReadString(root, "Token");
        if (string.IsNullOrWhiteSpace(token))
        {
            throw CreateInvalidResponse(missingTokenMessage);
        }

        (string userHash, string xuid) = ReadDisplayClaims(root, missingClaimsMessage);
        return (token!, userHash, xuid);
    }

    private static (string UserHash, string Xuid) ReadDisplayClaims(JsonElement root, string missingClaimsMessage)
    {
        if (!root.TryGetProperty("DisplayClaims", out JsonElement displayClaims) ||
            displayClaims.ValueKind != JsonValueKind.Object ||
            !displayClaims.TryGetProperty("xui", out JsonElement xui) ||
            xui.ValueKind != JsonValueKind.Array ||
            xui.GetArrayLength() == 0)
        {
            throw CreateInvalidResponse(missingClaimsMessage);
        }

        JsonElement first = xui[0];
        if (first.ValueKind != JsonValueKind.Object)
        {
            throw CreateInvalidResponse(missingClaimsMessage);
        }

        string? userHash = ReadString(first, "uhs");
        if (string.IsNullOrWhiteSpace(userHash))
        {
            throw CreateInvalidResponse(missingClaimsMessage);
        }

        return (userHash!, ReadString(first, "xid") ?? string.Empty);
    }

    private static bool TryGetXErr(JsonElement root, out long xerr)
    {
        xerr = 0;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("XErr", out JsonElement element))
        {
            return false;
        }

        if (element.ValueKind == JsonValueKind.Number)
        {
            return element.TryGetInt64(out xerr);
        }

        return element.ValueKind == JsonValueKind.String &&
               long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out xerr);
    }

    private static AccountAuthenticationException CreateXErrException(long xerr)
    {
        return xerr switch
        {
            XboxNoAccountError => new AccountAuthenticationException(
                AccountErrorCode.XboxNoAccount,
                "该 Microsoft 账户没有 Xbox 档案。"),
            XboxCountryUnavailableError => new AccountAuthenticationException(
                AccountErrorCode.XboxCountryUnavailable,
                "Xbox Live 在账户所在的国家或地区不可用。"),
            XboxChildAccountError or XboxChildAccountErrorVariant => new AccountAuthenticationException(
                AccountErrorCode.XboxChildAccount,
                "该账户是儿童账户，需要家长账户同意。"),
            XboxBannedError => new AccountAuthenticationException(
                AccountErrorCode.XboxBanned,
                "该 Xbox Live 账户已被封禁。"),
            _ => new AccountAuthenticationException(
                AccountErrorCode.InvalidResponse,
                string.Create(CultureInfo.InvariantCulture, $"Xbox Live 授权失败（XErr {xerr}）。")),
        };
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
}
