using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using AstraLauncher.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AstraLauncher.Tests;

public sealed class AuthenticationFlowTests
{
    private const string ClientId = "astra-test-client";
    private const string DeviceCodeValue = "secret-device-code";
    private const string AccessTokenValue = "secret-access-token";
    private const string RefreshTokenValue = "secret-refresh-token";
    private const string MinecraftAccessTokenValue = "minecraft-access-token";

    private const string DeviceCodeJson = """{"device_code":"secret-device-code","user_code":"ABCD-EFGH","verification_uri":"https://microsoft.com/devicelogin","verification_uri_complete":"https://microsoft.com/devicelogin?code=ABCD-EFGH","expires_in":900,"interval":1,"message":"Use a browser to sign in."}""";
    private const string AuthorizationPendingJson = """{"error":"authorization_pending","error_description":"The user has not finished signing in."}""";
    private const string SlowDownJson = """{"error":"slow_down","error_description":"Poll less frequently."}""";
    private const string ExpiredTokenJson = """{"error":"expired_token","error_description":"The device code expired."}""";
    private const string TokenJson = """{"token_type":"Bearer","scope":"XboxLive.signin","expires_in":3600,"access_token":"secret-access-token","refresh_token":"secret-refresh-token"}""";
    private const string InvalidGrantJson = """{"error":"invalid_grant","error_description":"The refresh token is invalid."}""";
    private const string XboxTokenJson = """{"Token":"xbl-token","DisplayClaims":{"xui":[{"uhs":"user-hash","xid":"123456789"}]}}""";
    private const string XstsTokenJson = """{"Token":"xsts-token","DisplayClaims":{"xui":[{"uhs":"user-hash","xid":"123456789"}]}}""";
    private const string MinecraftLoginJson = """{"username":"user-hash","access_token":"minecraft-access-token","token_type":"Bearer","expires_in":86400}""";
    private const string MinecraftProfileJson = """{"id":"0123456789abcdef0123456789abcdef","name":"Steve","skins":[],"capes":[]}""";

    [Fact]
    public async Task RequestDeviceCodeAsync_PostsFormAndParsesPrompt()
    {
        using var handler = new RecordingHandler();
        handler.Enqueue(HttpStatusCode.OK, DeviceCodeJson);
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var client = new DeviceCodeOAuthClient(httpClient, NullLogger<DeviceCodeOAuthClient>.Instance);

        DateTimeOffset before = DateTimeOffset.UtcNow;
        DeviceCodeInfo info = await client.RequestDeviceCodeAsync(ClientId);
        DateTimeOffset after = DateTimeOffset.UtcNow;

        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(new Uri("https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode"), request.Uri);
        Assert.StartsWith("application/x-www-form-urlencoded", request.Headers["Content-Type"], StringComparison.Ordinal);

        Dictionary<string, string> form = ParseForm(request.Body);
        Assert.Equal(ClientId, form["client_id"]);
        Assert.Equal("XboxLive.signin offline_access", form["scope"]);

        Assert.Equal("ABCD-EFGH", info.UserCode);
        Assert.Equal("https://microsoft.com/devicelogin", info.VerificationUri);
        Assert.Equal("https://microsoft.com/devicelogin?code=ABCD-EFGH", info.VerificationUriComplete!);
        Assert.Equal("Use a browser to sign in.", info.Message);
        Assert.Equal(TimeSpan.FromSeconds(1), info.Interval);
        Assert.InRange(info.ExpiresAt, before.AddSeconds(900), after.AddSeconds(900));
    }

    [Fact]
    public async Task RequestDeviceCodeAsync_WhenIntervalIsMissing_DefaultsToFiveSeconds()
    {
        using var handler = new RecordingHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"device_code":"secret-device-code","user_code":"ABCD-EFGH","verification_uri":"https://microsoft.com/devicelogin","expires_in":900,"message":"Use a browser to sign in."}""");
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var client = new DeviceCodeOAuthClient(httpClient, NullLogger<DeviceCodeOAuthClient>.Instance);

        DeviceCodeInfo info = await client.RequestDeviceCodeAsync(ClientId);

        Assert.Equal(TimeSpan.FromSeconds(5), info.Interval);
    }

    [Fact]
    public async Task RequestDeviceCodeAsync_WhenResponseLacksUserCode_ThrowsInvalidResponse()
    {
        using var handler = new RecordingHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"device_code":"secret-device-code","verification_uri":"https://microsoft.com/devicelogin","expires_in":900}""");
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var client = new DeviceCodeOAuthClient(httpClient, NullLogger<DeviceCodeOAuthClient>.Instance);

        AccountAuthenticationException exception = await Assert.ThrowsAsync<AccountAuthenticationException>(
            () => client.RequestDeviceCodeAsync(ClientId));

        Assert.Equal(AccountErrorCode.InvalidResponse, exception.Code);
    }

    [Fact]
    public async Task RequestDeviceCodeAsync_WhenServerReturnsHttpError_ThrowsInvalidResponse()
    {
        using var handler = new RecordingHandler();
        handler.Enqueue(HttpStatusCode.InternalServerError, "not json");
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var client = new DeviceCodeOAuthClient(httpClient, NullLogger<DeviceCodeOAuthClient>.Instance);

        AccountAuthenticationException exception = await Assert.ThrowsAsync<AccountAuthenticationException>(
            () => client.RequestDeviceCodeAsync(ClientId));

        Assert.Equal(AccountErrorCode.InvalidResponse, exception.Code);
    }

    [Fact]
    public async Task WaitForTokenAsync_PollsPendingThenReturnsTokensWithoutLoggingSecrets()
    {
        using var handler = new RecordingHandler();
        handler.Enqueue(HttpStatusCode.OK, DeviceCodeJson);
        handler.Enqueue(HttpStatusCode.BadRequest, AuthorizationPendingJson);
        handler.Enqueue(HttpStatusCode.OK, TokenJson);
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var logger = new RecordingLogger<DeviceCodeOAuthClient>();
        var client = new DeviceCodeOAuthClient(httpClient, logger);

        DeviceCodeInfo info = await client.RequestDeviceCodeAsync(ClientId);
        DeviceCodeInfo fast = info with
        {
            Interval = TimeSpan.FromMilliseconds(20),
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(30),
        };
        var reports = new List<string>();

        DateTimeOffset before = DateTimeOffset.UtcNow;
        MicrosoftTokenSet tokens = await client.WaitForTokenAsync(
            ClientId,
            fast,
            new InlineProgress<string>(reports.Add));
        DateTimeOffset after = DateTimeOffset.UtcNow;

        Assert.Equal(AccessTokenValue, tokens.AccessToken);
        Assert.Equal(RefreshTokenValue, tokens.RefreshToken);
        Assert.InRange(tokens.ExpiresAt, before.AddSeconds(3600), after.AddSeconds(3600));

        string progressMessage = Assert.Single(reports);
        Assert.Contains("等待用户", progressMessage);

        Assert.Equal(3, handler.Requests.Count);
        foreach (RecordedRequest request in handler.Requests.Skip(1))
        {
            Assert.Equal(new Uri("https://login.microsoftonline.com/consumers/oauth2/v2.0/token"), request.Uri);
            Dictionary<string, string> form = ParseForm(request.Body);
            Assert.Equal("urn:ietf:params:oauth:grant-type:device_code", form["grant_type"]);
            Assert.Equal(ClientId, form["client_id"]);
            Assert.Equal(DeviceCodeValue, form["device_code"]);
        }

        string logged = string.Join(Environment.NewLine, logger.Messages);
        Assert.DoesNotContain(AccessTokenValue, logged);
        Assert.DoesNotContain(RefreshTokenValue, logged);
        Assert.DoesNotContain(DeviceCodeValue, logged);
        Assert.Contains("ABCD-EFGH", logged);
    }

    [Fact]
    public async Task WaitForTokenAsync_WhenServerSaysSlowDown_WaitsMuchLongerBeforePollingAgain()
    {
        using var handler = new RecordingHandler();
        handler.Enqueue(HttpStatusCode.OK, DeviceCodeJson);
        handler.Enqueue(HttpStatusCode.BadRequest, SlowDownJson);
        handler.Enqueue(HttpStatusCode.OK, TokenJson);
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var client = new DeviceCodeOAuthClient(httpClient, NullLogger<DeviceCodeOAuthClient>.Instance);

        DeviceCodeInfo info = await client.RequestDeviceCodeAsync(ClientId);
        DeviceCodeInfo fast = info with
        {
            Interval = TimeSpan.FromMilliseconds(20),
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(30),
        };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.WaitForTokenAsync(ClientId, fast, cancellationToken: cancellation.Token));

        // With a 20 ms interval the poll would have fired dozens of times; the +5 s slow-down leaves only the
        // device-code request and the first (slow_down) token request.
        Assert.InRange(handler.Requests.Count, 2, 3);
    }

    [Fact]
    public async Task WaitForTokenAsync_WhenServerReportsExpiredToken_ThrowsDeviceCodeExpired()
    {
        using var handler = new RecordingHandler();
        handler.Enqueue(HttpStatusCode.OK, DeviceCodeJson);
        handler.Enqueue(HttpStatusCode.BadRequest, ExpiredTokenJson);
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var client = new DeviceCodeOAuthClient(httpClient, NullLogger<DeviceCodeOAuthClient>.Instance);

        DeviceCodeInfo info = await client.RequestDeviceCodeAsync(ClientId);
        AccountAuthenticationException exception = await Assert.ThrowsAsync<AccountAuthenticationException>(
            () => client.WaitForTokenAsync(ClientId, info));

        Assert.Equal(AccountErrorCode.DeviceCodeExpired, exception.Code);
    }

    [Theory]
    [InlineData("authorization_declined")]
    [InlineData("access_denied")]
    public async Task WaitForTokenAsync_WhenUserDeclines_ThrowsAuthorizationDeclined(string errorCode)
    {
        using var handler = new RecordingHandler();
        handler.Enqueue(HttpStatusCode.OK, DeviceCodeJson);
        handler.Enqueue(HttpStatusCode.BadRequest, JsonSerializer.Serialize(new { error = errorCode }));
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var client = new DeviceCodeOAuthClient(httpClient, NullLogger<DeviceCodeOAuthClient>.Instance);

        DeviceCodeInfo info = await client.RequestDeviceCodeAsync(ClientId);
        AccountAuthenticationException exception = await Assert.ThrowsAsync<AccountAuthenticationException>(
            () => client.WaitForTokenAsync(ClientId, info));

        Assert.Equal(AccountErrorCode.AuthorizationDeclined, exception.Code);
    }

    [Fact]
    public async Task WaitForTokenAsync_WhenDeviceCodeExpiresWhilePolling_ThrowsDeviceCodeExpired()
    {
        using var handler = new RecordingHandler();
        handler.Enqueue(HttpStatusCode.OK, DeviceCodeJson);
        for (int index = 0; index < 20; index++)
        {
            handler.Enqueue(HttpStatusCode.BadRequest, AuthorizationPendingJson);
        }

        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var client = new DeviceCodeOAuthClient(httpClient, NullLogger<DeviceCodeOAuthClient>.Instance);

        DeviceCodeInfo info = await client.RequestDeviceCodeAsync(ClientId);
        DeviceCodeInfo expiring = info with
        {
            Interval = TimeSpan.FromMilliseconds(20),
            ExpiresAt = DateTimeOffset.UtcNow.AddMilliseconds(60),
        };

        AccountAuthenticationException exception = await Assert.ThrowsAsync<AccountAuthenticationException>(
            () => client.WaitForTokenAsync(ClientId, expiring));

        Assert.Equal(AccountErrorCode.DeviceCodeExpired, exception.Code);
    }

    [Fact]
    public async Task WaitForTokenAsync_WithoutAPendingDeviceCodeRequest_ThrowsInvalidResponse()
    {
        using var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var client = new DeviceCodeOAuthClient(httpClient, NullLogger<DeviceCodeOAuthClient>.Instance);
        var deviceCode = new DeviceCodeInfo(
            "ABCD-EFGH",
            "https://microsoft.com/devicelogin",
            null,
            "Use a browser to sign in.",
            DateTimeOffset.UtcNow.AddMinutes(5),
            TimeSpan.FromMilliseconds(20));

        AccountAuthenticationException exception = await Assert.ThrowsAsync<AccountAuthenticationException>(
            () => client.WaitForTokenAsync(ClientId, deviceCode));

        Assert.Equal(AccountErrorCode.InvalidResponse, exception.Code);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RefreshAsync_WhenSuccessful_PostsRefreshGrantAndReturnsTokens()
    {
        using var handler = new RecordingHandler();
        handler.Enqueue(HttpStatusCode.OK, TokenJson);
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var client = new DeviceCodeOAuthClient(httpClient, NullLogger<DeviceCodeOAuthClient>.Instance);

        DateTimeOffset before = DateTimeOffset.UtcNow;
        MicrosoftTokenSet tokens = await client.RefreshAsync(ClientId, RefreshTokenValue);
        DateTimeOffset after = DateTimeOffset.UtcNow;

        Assert.Equal(AccessTokenValue, tokens.AccessToken);
        Assert.Equal(RefreshTokenValue, tokens.RefreshToken);
        Assert.InRange(tokens.ExpiresAt, before.AddSeconds(3600), after.AddSeconds(3600));

        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(new Uri("https://login.microsoftonline.com/consumers/oauth2/v2.0/token"), request.Uri);
        Dictionary<string, string> form = ParseForm(request.Body);
        Assert.Equal("refresh_token", form["grant_type"]);
        Assert.Equal(ClientId, form["client_id"]);
        Assert.Equal(RefreshTokenValue, form["refresh_token"]);
        Assert.Equal("XboxLive.signin offline_access", form["scope"]);
    }

    [Fact]
    public async Task RefreshAsync_WhenInvalidGrant_ThrowsSessionExpired()
    {
        using var handler = new RecordingHandler();
        handler.Enqueue(HttpStatusCode.BadRequest, InvalidGrantJson);
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var client = new DeviceCodeOAuthClient(httpClient, NullLogger<DeviceCodeOAuthClient>.Instance);

        AccountAuthenticationException exception = await Assert.ThrowsAsync<AccountAuthenticationException>(
            () => client.RefreshAsync(ClientId, RefreshTokenValue));

        Assert.Equal(AccountErrorCode.SessionExpired, exception.Code);
    }

    [Fact]
    public async Task RefreshAsync_WhenNetworkFails_ThrowsNetwork()
    {
        using var handler = new RecordingHandler();
        handler.EnqueueFailure(new HttpRequestException("The network is unreachable."));
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var client = new DeviceCodeOAuthClient(httpClient, NullLogger<DeviceCodeOAuthClient>.Instance);

        AccountAuthenticationException exception = await Assert.ThrowsAsync<AccountAuthenticationException>(
            () => client.RefreshAsync(ClientId, RefreshTokenValue));

        Assert.Equal(AccountErrorCode.Network, exception.Code);
    }

    [Fact]
    public async Task AuthenticateAsync_SendsRpsTicketAndParsesUserHashAndXuid()
    {
        using var handler = new RecordingHandler();
        handler.Enqueue(HttpStatusCode.OK, XboxTokenJson);
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var client = new XboxLiveAuthenticator(httpClient, NullLogger<XboxLiveAuthenticator>.Instance);

        XboxLiveToken token = await client.AuthenticateAsync("ms-access-token");

        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(new Uri("https://user.auth.xboxlive.com/user/authenticate"), request.Uri);
        Assert.Equal("1", request.Headers["x-xbl-contract-version"]);
        Assert.StartsWith("application/json", request.Headers["Content-Type"], StringComparison.Ordinal);

        using JsonDocument document = JsonDocument.Parse(request.Body);
        JsonElement root = document.RootElement;
        Assert.Equal("http://auth.xboxlive.com", root.GetProperty("RelyingParty").GetString()!);
        Assert.Equal("JWT", root.GetProperty("TokenType").GetString()!);
        JsonElement properties = root.GetProperty("Properties");
        Assert.Equal("RPS", properties.GetProperty("AuthMethod").GetString()!);
        Assert.Equal("user.auth.xboxlive.com", properties.GetProperty("SiteName").GetString()!);
        Assert.Equal("d=ms-access-token", properties.GetProperty("RpsTicket").GetString()!);

        Assert.Equal("xbl-token", token.XboxToken);
        Assert.Equal("user-hash", token.UserHash);
        Assert.Equal("123456789", token.Xuid);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    public async Task AuthenticateAsync_WhenResponseIsMalformed_ThrowsInvalidResponse(string body)
    {
        using var handler = new RecordingHandler();
        handler.Enqueue(HttpStatusCode.OK, body);
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var client = new XboxLiveAuthenticator(httpClient, NullLogger<XboxLiveAuthenticator>.Instance);

        AccountAuthenticationException exception = await Assert.ThrowsAsync<AccountAuthenticationException>(
            () => client.AuthenticateAsync("ms-access-token"));

        Assert.Equal(AccountErrorCode.InvalidResponse, exception.Code);
    }

    [Fact]
    public async Task AuthorizeAsync_WhenSuccessful_SendsXstsPayloadAndParsesToken()
    {
        using var handler = new RecordingHandler();
        handler.Enqueue(HttpStatusCode.OK, XstsTokenJson);
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var client = new XboxLiveAuthenticator(httpClient, NullLogger<XboxLiveAuthenticator>.Instance);

        XstsToken token = await client.AuthorizeAsync("xbl-token");

        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal(new Uri("https://xsts.auth.xboxlive.com/xsts/authorize"), request.Uri);
        Assert.Equal("1", request.Headers["x-xbl-contract-version"]);

        using JsonDocument document = JsonDocument.Parse(request.Body);
        JsonElement root = document.RootElement;
        Assert.Equal("rp://api.minecraftservices.com/", root.GetProperty("RelyingParty").GetString()!);
        Assert.Equal("JWT", root.GetProperty("TokenType").GetString()!);
        JsonElement properties = root.GetProperty("Properties");
        Assert.Equal("RETAIL", properties.GetProperty("SandboxId").GetString()!);
        JsonElement userTokens = properties.GetProperty("UserTokens");
        Assert.Equal("xbl-token", userTokens[0].GetString()!);

        Assert.Equal("xsts-token", token.Token);
        Assert.Equal("user-hash", token.UserHash);
        Assert.Equal("123456789", token.Xuid);
    }

    [Theory]
    [InlineData(2148916233L, AccountErrorCode.XboxNoAccount)]
    [InlineData(2148916235L, AccountErrorCode.XboxCountryUnavailable)]
    [InlineData(2148916236L, AccountErrorCode.XboxChildAccount)]
    [InlineData(2148916237L, AccountErrorCode.XboxChildAccount)]
    [InlineData(2148916238L, AccountErrorCode.XboxBanned)]
    [InlineData(999L, AccountErrorCode.InvalidResponse)]
    public async Task AuthorizeAsync_WhenXstsReturnsXErr_MapsTheErrorCode(long xerr, AccountErrorCode expected)
    {
        using var handler = new RecordingHandler();
        handler.Enqueue(HttpStatusCode.Unauthorized, JsonSerializer.Serialize(new { XErr = xerr, Message = "Xbox Live error" }));
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var client = new XboxLiveAuthenticator(httpClient, NullLogger<XboxLiveAuthenticator>.Instance);

        AccountAuthenticationException exception = await Assert.ThrowsAsync<AccountAuthenticationException>(
            () => client.AuthorizeAsync("xbl-token"));

        Assert.Equal(expected, exception.Code);
        if (xerr == 2148916233L)
        {
            Assert.Contains("没有 Xbox 档案", exception.Message);
        }

        if (xerr == 999L)
        {
            Assert.Contains("999", exception.Message);
        }
    }

    [Fact]
    public async Task LoginAsync_WhenSuccessful_ReturnsSessionWithDashedUuid()
    {
        using var handler = new RecordingHandler();
        handler.Enqueue(HttpStatusCode.OK, MinecraftLoginJson);
        handler.Enqueue(HttpStatusCode.OK, MinecraftProfileJson);
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var client = new MinecraftAuthenticator(httpClient, NullLogger<MinecraftAuthenticator>.Instance);

        DateTimeOffset before = DateTimeOffset.UtcNow;
        MinecraftSessionInfo session = await client.LoginAsync("user-hash", "xsts-token", RefreshTokenValue);
        DateTimeOffset after = DateTimeOffset.UtcNow;

        Assert.Equal(2, handler.Requests.Count);

        RecordedRequest login = handler.Requests[0];
        Assert.Equal(HttpMethod.Post, login.Method);
        Assert.Equal(new Uri("https://api.minecraftservices.com/authentication/login_with_xbox"), login.Uri);
        using (JsonDocument loginDocument = JsonDocument.Parse(login.Body))
        {
            Assert.Equal(
                "XBL3.0 x=user-hash;xsts-token",
                loginDocument.RootElement.GetProperty("identityToken").GetString()!);
        }

        RecordedRequest profile = handler.Requests[1];
        Assert.Equal(HttpMethod.Get, profile.Method);
        Assert.Equal(new Uri("https://api.minecraftservices.com/minecraft/profile"), profile.Uri);
        Assert.Equal("Bearer " + MinecraftAccessTokenValue, profile.Headers["Authorization"]);

        Assert.Equal("Steve", session.PlayerName);
        Assert.Equal("01234567-89ab-cdef-0123-456789abcdef", session.PlayerUuid);
        Assert.Equal(string.Empty, session.Xuid);
        Assert.Equal(string.Empty, session.ClientId);
        Assert.Equal(MinecraftAccessTokenValue, session.AccessToken);
        Assert.InRange(session.ExpiresAt, before.AddSeconds(86400), after.AddSeconds(86400));
    }

    [Fact]
    public async Task LoginAsync_WhenProfileIsMissing_ThrowsMinecraftNotOwned()
    {
        using var handler = new RecordingHandler();
        handler.Enqueue(HttpStatusCode.OK, MinecraftLoginJson);
        handler.Enqueue(HttpStatusCode.NotFound, """{"path":"/minecraft/profile","errorType":"NOT_FOUND"}""");
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var client = new MinecraftAuthenticator(httpClient, NullLogger<MinecraftAuthenticator>.Instance);

        AccountAuthenticationException exception = await Assert.ThrowsAsync<AccountAuthenticationException>(
            () => client.LoginAsync("user-hash", "xsts-token", RefreshTokenValue));

        Assert.Equal(AccountErrorCode.MinecraftNotOwned, exception.Code);
        Assert.Contains("未拥有 Minecraft Java 版", exception.Message);
    }

    [Fact]
    public async Task LoginAsync_WhenLoginIsUnauthorized_ThrowsMinecraftNotOwned()
    {
        using var handler = new RecordingHandler();
        handler.Enqueue(HttpStatusCode.Unauthorized, """{"path":"/authentication/login_with_xbox","errorType":"UNAUTHORIZED"}""");
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var client = new MinecraftAuthenticator(httpClient, NullLogger<MinecraftAuthenticator>.Instance);

        AccountAuthenticationException exception = await Assert.ThrowsAsync<AccountAuthenticationException>(
            () => client.LoginAsync("user-hash", "xsts-token", RefreshTokenValue));

        Assert.Equal(AccountErrorCode.MinecraftNotOwned, exception.Code);
    }

    [Fact]
    public async Task LoginAsync_WhenProfileUuidIsInvalid_ThrowsInvalidResponse()
    {
        using var handler = new RecordingHandler();
        handler.Enqueue(HttpStatusCode.OK, MinecraftLoginJson);
        handler.Enqueue(HttpStatusCode.OK, """{"id":"not-a-uuid","name":"Steve"}""");
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var client = new MinecraftAuthenticator(httpClient, NullLogger<MinecraftAuthenticator>.Instance);

        AccountAuthenticationException exception = await Assert.ThrowsAsync<AccountAuthenticationException>(
            () => client.LoginAsync("user-hash", "xsts-token", RefreshTokenValue));

        Assert.Equal(AccountErrorCode.InvalidResponse, exception.Code);
    }

    private static Dictionary<string, string> ParseForm(string body)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = pair.IndexOf('=', StringComparison.Ordinal);
            string key = separator < 0 ? pair : pair[..separator];
            string value = separator < 0 ? string.Empty : pair[(separator + 1)..];
            values[WebUtility.UrlDecode(key)!] = WebUtility.UrlDecode(value)!;
        }

        return values;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<RecordedResponse> _responses = new();

        public List<RecordedRequest> Requests { get; } = [];

        public void Enqueue(HttpStatusCode statusCode, string body, string mediaType = "application/json") =>
            _responses.Enqueue(new RecordedResponse(statusCode, body, mediaType, null));

        public void EnqueueFailure(Exception exception) =>
            _responses.Enqueue(new RecordedResponse(default, string.Empty, "application/json", exception));

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            string body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, IEnumerable<string>> header in request.Headers)
            {
                headers[header.Key] = string.Join(", ", header.Value);
            }

            if (request.Content is not null)
            {
                foreach (KeyValuePair<string, IEnumerable<string>> header in request.Content.Headers)
                {
                    headers[header.Key] = string.Join(", ", header.Value);
                }
            }

            Requests.Add(new RecordedRequest(request.Method, request.RequestUri!, headers, body));

            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("No queued HTTP response is available.");
            }

            RecordedResponse response = _responses.Dequeue();
            if (response.Exception is not null)
            {
                throw response.Exception;
            }

            return new HttpResponseMessage(response.StatusCode)
            {
                Content = new StringContent(response.Body, Encoding.UTF8, response.MediaType),
            };
        }
    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        Uri Uri,
        IReadOnlyDictionary<string, string> Headers,
        string Body);

    private sealed record RecordedResponse(
        HttpStatusCode StatusCode,
        string Body,
        string MediaType,
        Exception? Exception);

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            Messages.Add(formatter(state, exception));
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
