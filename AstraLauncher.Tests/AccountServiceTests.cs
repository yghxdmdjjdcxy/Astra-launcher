using System.IO;
using System.Net.Http;
using System.Text.Json;
using AstraLauncher.Authentication;
using AstraLauncher.Core.Models;
using AstraLauncher.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AstraLauncher.Tests;

public sealed class AccountServiceTests
{
    [Fact]
    public async Task AddOfflineAsync_TrimsName_DerivesJavaUuid_PersistsAndSelects()
    {
        ServiceFixture fixture = CreateFixture();

        AccountProfile account = await fixture.Service.AddOfflineAsync("  Notch  ");

        Assert.Equal("Notch", account.PlayerName);
        Assert.Equal("b50ad385-829d-3141-a216-7e7d7539ba7f", account.PlayerUuid);
        Assert.Equal(AccountKind.Offline, account.Kind);
        Assert.NotEqual(Guid.Empty, account.Id);
        Assert.Null(account.RefreshToken);
        Assert.Equal(account.AddedAt, account.LastLoginAt.GetValueOrDefault());
        Assert.Equal(account.Id, fixture.Settings.Current.SelectedAccountId.GetValueOrDefault());
        Assert.Equal(1, fixture.Settings.SaveCount);
        Assert.Equal(1, fixture.Store.SaveCount);
        Assert.Equal(1, fixture.Store.LoadCount);

        AccountProfile stored = Assert.Single(fixture.Store.LastSavedAccounts);
        Assert.Equal(account.Id, stored.Id);
        Assert.Null(stored.RefreshToken);
        Assert.Null(fixture.Store.GetProtectedRefreshToken(account.Id));
        Assert.Equal(0, fixture.Protector.ProtectCount);

        IReadOnlyList<AccountProfile> accounts = await fixture.Service.GetAccountsAsync();
        Assert.Equal(account.Id, Assert.Single(accounts).Id);
        Assert.Equal(1, fixture.Store.LoadCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ABCDEFGHIJKLMNOPQ")]
    public async Task AddOfflineAsync_RejectsInvalidNames(string playerName)
    {
        ServiceFixture fixture = CreateFixture();

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.AddOfflineAsync(playerName));

        Assert.Equal(0, fixture.Store.SaveCount);
        Assert.Equal(0, fixture.Settings.SaveCount);
        Assert.Empty(await fixture.Service.GetAccountsAsync());
    }

    [Fact]
    public async Task AddOfflineAsync_RejectsNullName()
    {
        ServiceFixture fixture = CreateFixture();

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.AddOfflineAsync(null!));

        Assert.Equal(0, fixture.Store.SaveCount);
    }

    [Fact]
    public async Task AddMicrosoftAsync_RunsDeviceCodeFlow_PersistsEncryptedRefreshToken_AndSelects()
    {
        ServiceFixture fixture = CreateFixture();
        var deviceCode = new DeviceCodeInfo(
            "USER-CODE",
            "https://microsoft.com/devicelogin",
            null,
            "请在浏览器中登录。",
            DateTimeOffset.UtcNow.AddMinutes(15),
            TimeSpan.FromSeconds(5));
        var tokens = new MicrosoftTokenSet(
            "msa-access-token",
            "msa-refresh-token",
            DateTimeOffset.UtcNow.AddHours(1));
        fixture.OAuth.DeviceCode = deviceCode;
        fixture.OAuth.TokenSet = tokens;
        fixture.Xbox.XboxToken = new XboxLiveToken("xbl-token", "xbox-user-hash", "xuid-from-xbox");
        fixture.Xbox.Xsts = new XstsToken("xsts-token", "xsts-user-hash", "xuid-from-xsts");
        fixture.Minecraft.Session = new MinecraftSessionInfo(
            "Steve",
            "6f8f57715090da2632453988d9a1501b",
            string.Empty,
            string.Empty,
            "minecraft-access-token",
            DateTimeOffset.UtcNow.AddHours(1));
        var prompt = new RecordingProgress<DeviceCodeInfo>();

        AccountProfile account = await fixture.Service.AddMicrosoftAsync("  client-id  ", prompt);

        Assert.Same(deviceCode, Assert.Single(prompt.Values));
        Assert.Equal(1, fixture.OAuth.RequestCount);
        Assert.Equal(1, fixture.OAuth.WaitCount);
        Assert.Equal("client-id", fixture.OAuth.LastRequestedClientId);
        Assert.Equal("client-id", fixture.OAuth.LastWaitClientId);
        Assert.Same(deviceCode, fixture.OAuth.LastWaitDeviceCode);
        Assert.Equal(1, fixture.Xbox.AuthenticateCount);
        Assert.Equal(tokens.AccessToken, fixture.Xbox.LastMicrosoftAccessToken);
        Assert.Equal(1, fixture.Xbox.AuthorizeCount);
        Assert.Equal("xbl-token", fixture.Xbox.LastXboxToken);
        Assert.Equal(1, fixture.Minecraft.LoginCount);
        Assert.Equal("xbox-user-hash", fixture.Minecraft.LastUserHash);
        Assert.Equal("xsts-token", fixture.Minecraft.LastXstsToken);
        Assert.Equal("msa-refresh-token", fixture.Minecraft.LastRefreshToken);

        Assert.Equal(AccountKind.Microsoft, account.Kind);
        Assert.Equal("Steve", account.PlayerName);
        Assert.Equal("6f8f57715090da2632453988d9a1501b", account.PlayerUuid);
        Assert.Equal("xuid-from-xbox", account.Xuid);
        Assert.Equal("msa-refresh-token", account.RefreshToken);
        Assert.True(Guid.TryParse(account.ClientId, out _));
        Assert.Equal(1, fixture.Store.SaveCount);

        AccountProfile stored = Assert.Single(fixture.Store.LastSavedAccounts);
        Assert.Equal("msa-refresh-token", stored.RefreshToken);
        Assert.Equal("enc:msa-refresh-token", fixture.Store.GetProtectedRefreshToken(account.Id));
        Assert.True(fixture.Protector.ProtectCount >= 1);
        Assert.Equal(account.Id, fixture.Settings.Current.SelectedAccountId.GetValueOrDefault());
        Assert.Equal(1, fixture.Settings.SaveCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AddMicrosoftAsync_WhenClientIdIsMissing_ThrowsClientIdMissing_WithoutCallingOAuth(string clientId)
    {
        ServiceFixture fixture = CreateFixture();

        AccountAuthenticationException exception = await Assert.ThrowsAsync<AccountAuthenticationException>(
            () => fixture.Service.AddMicrosoftAsync(clientId));

        Assert.Equal(AccountErrorCode.ClientIdMissing, exception.Code);
        Assert.Equal(0, fixture.OAuth.RequestCount);
        Assert.Equal(0, fixture.Store.SaveCount);
        Assert.Equal(0, fixture.Settings.SaveCount);
    }

    [Fact]
    public async Task AddMicrosoftAsync_WhenPlayerUuidAlreadyExists_UpdatesExistingAccount()
    {
        ServiceFixture fixture = CreateFixture();
        const string playerUuid = "6f8f57715090da2632453988d9a1501b";
        fixture.OAuth.DeviceCode = CreateDeviceCode();
        fixture.OAuth.TokenSet = new MicrosoftTokenSet(
            "access-1",
            "refresh-1",
            DateTimeOffset.UtcNow.AddHours(1));
        fixture.Xbox.XboxToken = new XboxLiveToken("xbl-1", "hash-1", "xuid-1");
        fixture.Xbox.Xsts = new XstsToken("xsts-1", "hash-1", "xuid-1");
        fixture.Minecraft.Session = new MinecraftSessionInfo(
            "First",
            playerUuid,
            "session-xuid-1",
            "session-client-1",
            "access-1",
            DateTimeOffset.UtcNow.AddHours(1));

        AccountProfile first = await fixture.Service.AddMicrosoftAsync("client-id");

        fixture.OAuth.TokenSet = new MicrosoftTokenSet(
            "access-2",
            "refresh-2",
            DateTimeOffset.UtcNow.AddHours(1));
        fixture.Xbox.XboxToken = new XboxLiveToken("xbl-2", "hash-2", "xuid-2");
        fixture.Xbox.Xsts = new XstsToken("xsts-2", "hash-2", "xuid-2");
        fixture.Minecraft.Session = new MinecraftSessionInfo(
            "Second",
            playerUuid,
            "session-xuid-2",
            "session-client-2",
            "access-2",
            DateTimeOffset.UtcNow.AddHours(1));

        AccountProfile second = await fixture.Service.AddMicrosoftAsync("client-id");

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.AddedAt, second.AddedAt);
        Assert.Equal("Second", second.PlayerName);
        Assert.Equal("refresh-2", second.RefreshToken);
        Assert.Equal(2, fixture.Store.SaveCount);
        Assert.Single(fixture.Store.LastSavedAccounts);
        Assert.Equal("enc:refresh-2", fixture.Store.GetProtectedRefreshToken(first.Id));
        Assert.Equal(first.Id, fixture.Settings.Current.SelectedAccountId.GetValueOrDefault());

        IReadOnlyList<AccountProfile> accounts = await fixture.Service.GetAccountsAsync();
        AccountProfile onlyAccount = Assert.Single(accounts);
        Assert.Equal(first.Id, onlyAccount.Id);
        Assert.Equal("refresh-2", onlyAccount.RefreshToken);
    }

    [Fact]
    public async Task GetAccountsAsync_LoadsOnce_ReturnsReadOnlyAscendingByAddedAt()
    {
        ServiceFixture fixture = CreateFixture();
        DateTimeOffset baseline = DateTimeOffset.UtcNow;
        var later = new AccountProfile(
            Guid.NewGuid(),
            AccountKind.Offline,
            "Later",
            "22222222222222222222222222222222",
            AddedAt: baseline.AddMinutes(1),
            LastLoginAt: baseline.AddMinutes(1));
        var earlier = new AccountProfile(
            Guid.NewGuid(),
            AccountKind.Offline,
            "Earlier",
            "11111111111111111111111111111111",
            AddedAt: baseline,
            LastLoginAt: baseline);
        fixture.Store.Seed(later, earlier);

        IReadOnlyList<AccountProfile> accounts = await fixture.Service.GetAccountsAsync();

        Assert.Equal(new[] { earlier.Id, later.Id }, accounts.Select(account => account.Id));
        Assert.Equal(1, fixture.Store.LoadCount);
        await fixture.Service.GetAccountsAsync();
        Assert.Equal(1, fixture.Store.LoadCount);
        Assert.Throws<NotSupportedException>(() => ((IList<AccountProfile>)accounts).Add(earlier));
    }

    [Fact]
    public async Task GetMinecraftSessionAsync_ForOfflineAccount_ReturnsNullWithoutRefreshing()
    {
        ServiceFixture fixture = CreateFixture();
        AccountProfile account = await fixture.Service.AddOfflineAsync("Steve");

        MinecraftSessionInfo? session = await fixture.Service.GetMinecraftSessionAsync(account.Id);

        Assert.Null(session);
        Assert.Equal(0, fixture.OAuth.RefreshCount);
    }

    [Fact]
    public async Task GetMinecraftSessionAsync_ForMicrosoftAccount_RefreshesPersistsAndReturnsSession()
    {
        ServiceFixture fixture = CreateFixture();
        const string playerUuid = "6f8f57715090da2632453988d9a1501b";
        fixture.OAuth.DeviceCode = CreateDeviceCode();
        fixture.OAuth.TokenSet = new MicrosoftTokenSet(
            "access-1",
            "refresh-1",
            DateTimeOffset.UtcNow.AddHours(1));
        fixture.Xbox.XboxToken = new XboxLiveToken("xbl-1", "hash-1", "xuid-1");
        fixture.Xbox.Xsts = new XstsToken("xsts-1", "hash-1", "xuid-1");
        fixture.Minecraft.Session = new MinecraftSessionInfo(
            "Steve",
            playerUuid,
            "xuid-1",
            "session-client-1",
            "access-1",
            DateTimeOffset.UtcNow.AddHours(1));
        AccountProfile account = await fixture.Service.AddMicrosoftAsync("client-id");
        fixture.Settings.Current.MicrosoftClientId = "configured-client-id";
        fixture.OAuth.RefreshedTokenSet = new MicrosoftTokenSet(
            "refreshed-access-token",
            "refresh-2",
            DateTimeOffset.UtcNow.AddHours(1));
        fixture.Xbox.XboxToken = new XboxLiveToken("xbl-2", "xbox-hash-2", "xbox-xuid-2");
        fixture.Xbox.Xsts = new XstsToken("xsts-2", "xsts-hash-2", "xsts-xuid-2");
        fixture.Minecraft.Session = new MinecraftSessionInfo(
            "Steve-Refreshed",
            playerUuid,
            string.Empty,
            string.Empty,
            "minecraft-access-2",
            DateTimeOffset.UtcNow.AddHours(1));

        MinecraftSessionInfo? session = await fixture.Service.GetMinecraftSessionAsync(account.Id);

        Assert.NotNull(session);
        MinecraftSessionInfo refreshedSession = Assert.IsType<MinecraftSessionInfo>(session);
        Assert.Equal("Steve-Refreshed", refreshedSession.PlayerName);
        Assert.Equal("minecraft-access-2", refreshedSession.AccessToken);
        Assert.Equal(account.ClientId, refreshedSession.ClientId);
        Assert.Equal("xbox-xuid-2", refreshedSession.Xuid);
        Assert.Equal(1, fixture.OAuth.RefreshCount);
        Assert.Equal("configured-client-id", fixture.OAuth.LastRefreshClientId);
        Assert.Equal("refresh-1", fixture.OAuth.LastRefreshToken);
        Assert.Equal("refresh-2", fixture.Minecraft.LastRefreshToken);
        Assert.Equal("xbox-hash-2", fixture.Minecraft.LastUserHash);
        Assert.Equal(2, fixture.Minecraft.LoginCount);
        Assert.Equal(2, fixture.Xbox.AuthenticateCount);
        Assert.Equal(2, fixture.Store.SaveCount);
        Assert.Equal("enc:refresh-2", fixture.Store.GetProtectedRefreshToken(account.Id));

        AccountProfile refreshedAccount = Assert.Single(await fixture.Service.GetAccountsAsync());
        Assert.Equal(account.Id, refreshedAccount.Id);
        Assert.Equal("refresh-2", refreshedAccount.RefreshToken);
        Assert.NotNull(refreshedAccount.LastLoginAt);
    }

    [Fact]
    public async Task GetMinecraftSessionAsync_WhenRefreshTokenIsMissing_ThrowsSessionExpired()
    {
        ServiceFixture fixture = CreateFixture();
        var account = new AccountProfile(
            Guid.NewGuid(),
            AccountKind.Microsoft,
            "Steve",
            "6f8f57715090da2632453988d9a1501b",
            Xuid: "xuid",
            ClientId: "client-id",
            RefreshToken: null,
            AddedAt: DateTimeOffset.UtcNow);
        fixture.Store.Seed(account);

        AccountAuthenticationException exception = await Assert.ThrowsAsync<AccountAuthenticationException>(
            () => fixture.Service.GetMinecraftSessionAsync(account.Id));

        Assert.Equal(AccountErrorCode.SessionExpired, exception.Code);
        Assert.Contains("重新登录", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, fixture.OAuth.RefreshCount);
        Assert.Equal(0, fixture.Store.SaveCount);
    }

    [Fact]
    public async Task GetMinecraftSessionAsync_WhenRefreshFailsWithHttpError_ThrowsNetworkError()
    {
        ServiceFixture fixture = CreateFixture();
        var account = new AccountProfile(
            Guid.NewGuid(),
            AccountKind.Microsoft,
            "Steve",
            "6f8f57715090da2632453988d9a1501b",
            Xuid: "xuid",
            ClientId: "client-id",
            RefreshToken: "refresh-1",
            AddedAt: DateTimeOffset.UtcNow);
        fixture.Store.Seed(account);
        fixture.Settings.Current.MicrosoftClientId = "configured-client-id";
        fixture.OAuth.RefreshFailure = new HttpRequestException("network down");

        AccountAuthenticationException exception = await Assert.ThrowsAsync<AccountAuthenticationException>(
            () => fixture.Service.GetMinecraftSessionAsync(account.Id));

        Assert.Equal(AccountErrorCode.Network, exception.Code);
        Assert.Equal(0, fixture.Store.SaveCount);
    }

    [Fact]
    public async Task GetMinecraftSessionAsync_WhenRefreshReturnsInvalidJson_ThrowsInvalidResponse()
    {
        ServiceFixture fixture = CreateFixture();
        var account = new AccountProfile(
            Guid.NewGuid(),
            AccountKind.Microsoft,
            "Steve",
            "6f8f57715090da2632453988d9a1501b",
            Xuid: "xuid",
            ClientId: "client-id",
            RefreshToken: "refresh-1",
            AddedAt: DateTimeOffset.UtcNow);
        fixture.Store.Seed(account);
        fixture.Settings.Current.MicrosoftClientId = "configured-client-id";
        fixture.OAuth.RefreshFailure = new JsonException("invalid response");

        AccountAuthenticationException exception = await Assert.ThrowsAsync<AccountAuthenticationException>(
            () => fixture.Service.GetMinecraftSessionAsync(account.Id));

        Assert.Equal(AccountErrorCode.InvalidResponse, exception.Code);
        Assert.Equal(0, fixture.Store.SaveCount);
    }

    [Fact]
    public async Task RemoveAsync_RemovesAccountAndClearsSelection()
    {
        ServiceFixture fixture = CreateFixture();
        AccountProfile account = await fixture.Service.AddOfflineAsync("Steve");
        Assert.Equal(account.Id, fixture.Settings.Current.SelectedAccountId.GetValueOrDefault());

        await fixture.Service.RemoveAsync(account.Id);

        Assert.Empty(await fixture.Service.GetAccountsAsync());
        Assert.Null(fixture.Settings.Current.SelectedAccountId);
        Assert.Null(await fixture.Service.GetSelectedAsync());
        Assert.Equal(2, fixture.Store.SaveCount);
        Assert.Equal(2, fixture.Settings.SaveCount);
    }

    [Fact]
    public async Task SelectAsync_WithUnknownAccount_ThrowsAndKeepsCurrentSelection()
    {
        ServiceFixture fixture = CreateFixture();
        AccountProfile account = await fixture.Service.AddOfflineAsync("Steve");
        int savesBefore = fixture.Settings.SaveCount;

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SelectAsync(Guid.NewGuid()));

        Assert.Equal(account.Id, fixture.Settings.Current.SelectedAccountId.GetValueOrDefault());
        Assert.Equal(savesBefore, fixture.Settings.SaveCount);

        AccountProfile? cleared = await fixture.Service.SelectAsync(null);

        Assert.Null(cleared);
        Assert.Null(fixture.Settings.Current.SelectedAccountId);
        Assert.Equal(savesBefore + 1, fixture.Settings.SaveCount);
    }

    [Fact]
    public async Task AddOfflineAsync_WhenPersistenceFails_PropagatesIOException()
    {
        ServiceFixture fixture = CreateFixture();
        fixture.Store.SaveFailure = new IOException("disk full");

        await Assert.ThrowsAsync<IOException>(() => fixture.Service.AddOfflineAsync("Steve"));

        Assert.Equal(0, fixture.Settings.SaveCount);
        Assert.Null(fixture.Settings.Current.SelectedAccountId);
        Assert.Empty(await fixture.Service.GetAccountsAsync());
    }

    private static ServiceFixture CreateFixture()
    {
        var protector = new FakeTokenProtector();
        var store = new FakeAccountStore(protector);
        var oauth = new FakeOAuthClient();
        var xbox = new FakeXboxAuthenticator();
        var minecraft = new FakeMinecraftAuthenticator();
        var settings = new FakeSettingsService();
        var service = new AccountService(
            store,
            oauth,
            xbox,
            minecraft,
            settings,
            protector,
            NullLogger<AccountService>.Instance);
        return new ServiceFixture(service, store, oauth, xbox, minecraft, settings, protector);
    }

    private static DeviceCodeInfo CreateDeviceCode() => new(
        "USER-CODE",
        "https://microsoft.com/devicelogin",
        null,
        "请在浏览器中登录。",
        DateTimeOffset.UtcNow.AddMinutes(15),
        TimeSpan.FromSeconds(5));

    private sealed record ServiceFixture(
        AccountService Service,
        FakeAccountStore Store,
        FakeOAuthClient OAuth,
        FakeXboxAuthenticator Xbox,
        FakeMinecraftAuthenticator Minecraft,
        FakeSettingsService Settings,
        FakeTokenProtector Protector);

    private sealed class RecordingProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];

        public void Report(T value) => Values.Add(value);
    }

    private sealed class FakeTokenProtector : ITokenProtector
    {
        private const string Prefix = "enc:";

        public int ProtectCount { get; private set; }

        public string Protect(string plaintext)
        {
            ProtectCount++;
            return Prefix + plaintext;
        }

        public string Unprotect(string protectedText)
        {
            if (!protectedText.StartsWith(Prefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The token was not protected by this protector.");
            }

            return protectedText[Prefix.Length..];
        }
    }

    private sealed class FakeAccountStore(ITokenProtector protector) : IAccountStore
    {
        private readonly List<AccountProfile> _accounts = [];
        private readonly Dictionary<Guid, string?> _protectedRefreshTokens = [];
        private List<AccountProfile> _lastSavedAccounts = [];

        public int LoadCount { get; private set; }

        public int SaveCount { get; private set; }

        public Exception? SaveFailure { get; set; }

        public IReadOnlyList<AccountProfile> LastSavedAccounts => _lastSavedAccounts;

        public void Seed(params AccountProfile[] accounts)
        {
            foreach (AccountProfile account in accounts)
            {
                string? refreshToken = account.RefreshToken;
                _accounts.Add(account with { RefreshToken = null });
                _protectedRefreshTokens[account.Id] = string.IsNullOrEmpty(refreshToken)
                    ? null
                    : protector.Protect(refreshToken);
            }
        }

        public string? GetProtectedRefreshToken(Guid accountId) =>
            _protectedRefreshTokens.GetValueOrDefault(accountId);

        public Task<IReadOnlyList<AccountProfile>> LoadAsync(CancellationToken cancellationToken = default)
        {
            LoadCount++;
            var accounts = new List<AccountProfile>(_accounts.Count);
            foreach (AccountProfile account in _accounts)
            {
                string? protectedRefreshToken = _protectedRefreshTokens.GetValueOrDefault(account.Id);
                string? refreshToken = protectedRefreshToken is null
                    ? null
                    : protector.Unprotect(protectedRefreshToken);
                accounts.Add(account with { RefreshToken = refreshToken });
            }

            return Task.FromResult<IReadOnlyList<AccountProfile>>(accounts);
        }

        public Task SaveAsync(IReadOnlyList<AccountProfile> accounts, CancellationToken cancellationToken = default)
        {
            SaveCount++;
            Exception? failure = SaveFailure;
            if (failure is not null)
            {
                return Task.FromException(failure);
            }

            _lastSavedAccounts = [.. accounts];
            _accounts.Clear();
            _protectedRefreshTokens.Clear();
            foreach (AccountProfile account in accounts)
            {
                string? refreshToken = account.RefreshToken;
                _accounts.Add(account with { RefreshToken = null });
                _protectedRefreshTokens[account.Id] = string.IsNullOrEmpty(refreshToken)
                    ? null
                    : protector.Protect(refreshToken);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FakeOAuthClient : IDeviceCodeOAuthClient
    {
        public DeviceCodeInfo DeviceCode { get; set; } = new(
            "USER-CODE",
            "https://microsoft.com/devicelogin",
            null,
            "请在浏览器中登录。",
            DateTimeOffset.UtcNow.AddMinutes(15),
            TimeSpan.FromSeconds(5));

        public MicrosoftTokenSet TokenSet { get; set; } = new(
            "access-token",
            "refresh-token",
            DateTimeOffset.UtcNow.AddHours(1));

        public MicrosoftTokenSet RefreshedTokenSet { get; set; } = new(
            "refreshed-access-token",
            "refreshed-refresh-token",
            DateTimeOffset.UtcNow.AddHours(1));

        public Exception? RequestFailure { get; set; }

        public Exception? WaitFailure { get; set; }

        public Exception? RefreshFailure { get; set; }

        public int RequestCount { get; private set; }

        public int WaitCount { get; private set; }

        public int RefreshCount { get; private set; }

        public string? LastRequestedClientId { get; private set; }

        public string? LastWaitClientId { get; private set; }

        public DeviceCodeInfo? LastWaitDeviceCode { get; private set; }

        public string? LastRefreshClientId { get; private set; }

        public string? LastRefreshToken { get; private set; }

        public Task<DeviceCodeInfo> RequestDeviceCodeAsync(string clientId, CancellationToken cancellationToken = default)
        {
            RequestCount++;
            LastRequestedClientId = clientId;
            Exception? failure = RequestFailure;
            return failure is null
                ? Task.FromResult(DeviceCode)
                : Task.FromException<DeviceCodeInfo>(failure);
        }

        public Task<MicrosoftTokenSet> WaitForTokenAsync(
            string clientId,
            DeviceCodeInfo deviceCode,
            IProgress<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            WaitCount++;
            LastWaitClientId = clientId;
            LastWaitDeviceCode = deviceCode;
            Exception? failure = WaitFailure;
            return failure is null
                ? Task.FromResult(TokenSet)
                : Task.FromException<MicrosoftTokenSet>(failure);
        }

        public Task<MicrosoftTokenSet> RefreshAsync(
            string clientId,
            string refreshToken,
            CancellationToken cancellationToken = default)
        {
            RefreshCount++;
            LastRefreshClientId = clientId;
            LastRefreshToken = refreshToken;
            Exception? failure = RefreshFailure;
            return failure is null
                ? Task.FromResult(RefreshedTokenSet)
                : Task.FromException<MicrosoftTokenSet>(failure);
        }
    }

    private sealed class FakeXboxAuthenticator : IXboxLiveAuthenticator
    {
        public XboxLiveToken XboxToken { get; set; } = new("xbl-token", "xbox-user-hash", "xbox-xuid");

        public XstsToken Xsts { get; set; } = new("xsts-token", "xsts-user-hash", "xsts-xuid");

        public Exception? AuthenticateFailure { get; set; }

        public Exception? AuthorizeFailure { get; set; }

        public int AuthenticateCount { get; private set; }

        public int AuthorizeCount { get; private set; }

        public string? LastMicrosoftAccessToken { get; private set; }

        public string? LastXboxToken { get; private set; }

        public Task<XboxLiveToken> AuthenticateAsync(
            string microsoftAccessToken,
            CancellationToken cancellationToken = default)
        {
            AuthenticateCount++;
            LastMicrosoftAccessToken = microsoftAccessToken;
            Exception? failure = AuthenticateFailure;
            return failure is null
                ? Task.FromResult(XboxToken)
                : Task.FromException<XboxLiveToken>(failure);
        }

        public Task<XstsToken> AuthorizeAsync(string xboxLiveToken, CancellationToken cancellationToken = default)
        {
            AuthorizeCount++;
            LastXboxToken = xboxLiveToken;
            Exception? failure = AuthorizeFailure;
            return failure is null
                ? Task.FromResult(Xsts)
                : Task.FromException<XstsToken>(failure);
        }
    }

    private sealed class FakeMinecraftAuthenticator : IMinecraftAuthenticator
    {
        public MinecraftSessionInfo Session { get; set; } = new(
            "Steve",
            "6f8f57715090da2632453988d9a1501b",
            "xuid",
            "minecraft-client-id",
            "minecraft-access-token",
            DateTimeOffset.UtcNow.AddHours(1));

        public Exception? LoginFailure { get; set; }

        public int LoginCount { get; private set; }

        public string? LastUserHash { get; private set; }

        public string? LastXstsToken { get; private set; }

        public string? LastRefreshToken { get; private set; }

        public Task<MinecraftSessionInfo> LoginAsync(
            string xboxUserHash,
            string xstsToken,
            string refreshToken,
            CancellationToken cancellationToken = default)
        {
            LoginCount++;
            LastUserHash = xboxUserHash;
            LastXstsToken = xstsToken;
            LastRefreshToken = refreshToken;
            Exception? failure = LoginFailure;
            return failure is null
                ? Task.FromResult(Session)
                : Task.FromException<MinecraftSessionInfo>(failure);
        }
    }

    private sealed class FakeSettingsService : ISettingsService
    {
        public LauncherSettings Current { get; set; } = new();

        public int SaveCount { get; private set; }

        public Exception? SaveFailure { get; set; }

        public event EventHandler<LauncherSettings>? Changed
        {
            add { }
            remove { }
        }

        public Task<LauncherSettings> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(Current);

        public Task SaveAsync(LauncherSettings settings, CancellationToken cancellationToken = default)
        {
            SaveCount++;
            Exception? failure = SaveFailure;
            if (failure is not null)
            {
                return Task.FromException(failure);
            }

            Current = settings;
            return Task.CompletedTask;
        }
    }
}
