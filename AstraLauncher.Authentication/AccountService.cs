using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AstraLauncher.Core.Models;
using AstraLauncher.Core.Services;
using Microsoft.Extensions.Logging;

namespace AstraLauncher.Authentication;

/// <summary>
/// Manages offline and Microsoft accounts: loads them once from <see cref="IAccountStore"/>, keeps a cached list,
/// persists every mutation and runs the Microsoft device-code/Xbox/Minecraft sign-in chain.
/// Tokens are never written to logs; refresh tokens are only handed to the store, which encrypts them.
/// </summary>
public sealed class AccountService : IAccountService
{
    private const int MaximumPlayerNameLength = 16;
    private const string OfflineUuidPrefix = "OfflinePlayer:";
    private const string SessionExpiredMessage = "Microsoft 登录已过期，请在账户页重新登录。";
    private const string ClientIdMissingMessage = "未配置 Microsoft Client ID，请在账户页重新登录。";

    private readonly IAccountStore _store;
    private readonly IDeviceCodeOAuthClient _oauth;
    private readonly IXboxLiveAuthenticator _xbox;
    private readonly IMinecraftAuthenticator _minecraft;
    private readonly ISettingsService _settings;
    private readonly ILogger<AccountService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<AccountProfile>? _accounts;

    /// <summary>Initializes the account service.</summary>
    public AccountService(
        IAccountStore store,
        IDeviceCodeOAuthClient oauth,
        IXboxLiveAuthenticator xbox,
        IMinecraftAuthenticator minecraft,
        ISettingsService settings,
        ITokenProtector protector,
        ILogger<AccountService> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(oauth);
        ArgumentNullException.ThrowIfNull(xbox);
        ArgumentNullException.ThrowIfNull(minecraft);
        ArgumentNullException.ThrowIfNull(settings);
        // The store owns encryption at rest; the protector is part of the composition contract so token
        // protection cannot accidentally be omitted from the account stack.
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _oauth = oauth;
        _xbox = xbox;
        _minecraft = minecraft;
        _settings = settings;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AccountProfile>> GetAccountsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<AccountProfile> accounts = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            return accounts
                .OrderBy(account => account.AddedAt)
                .ToList()
                .AsReadOnly();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<AccountProfile?> GetSelectedAsync(CancellationToken cancellationToken = default)
    {
        LauncherSettings settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        Guid? selectedAccountId = settings.SelectedAccountId;
        if (selectedAccountId is null)
        {
            return null;
        }

        IReadOnlyList<AccountProfile> accounts = await GetAccountsAsync(cancellationToken).ConfigureAwait(false);
        return accounts.FirstOrDefault(account => account.Id == selectedAccountId.Value);
    }

    /// <inheritdoc />
    public async Task<AccountProfile?> SelectAsync(Guid? accountId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<AccountProfile> accounts = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            AccountProfile? selected = null;
            if (accountId is Guid id)
            {
                selected = accounts.FirstOrDefault(account => account.Id == id)
                    ?? throw new InvalidOperationException("无法选择账户：该账户不存在。");
            }

            LauncherSettings settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
            settings.SelectedAccountId = accountId;
            await _settings.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
            return selected;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<AccountProfile> AddOfflineAsync(string playerName, CancellationToken cancellationToken = default)
    {
        if (playerName is null)
        {
            throw new ArgumentException("离线玩家名不能为空。", nameof(playerName));
        }

        string normalizedName = playerName.Trim();
        if (normalizedName.Length == 0)
        {
            throw new ArgumentException("离线玩家名不能为空。", nameof(playerName));
        }

        if (normalizedName.Length > MaximumPlayerNameLength)
        {
            throw new ArgumentException($"离线玩家名不能超过 {MaximumPlayerNameLength} 个字符。", nameof(playerName));
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        var account = new AccountProfile(
            Guid.NewGuid(),
            AccountKind.Offline,
            normalizedName,
            CreateOfflineUuid(normalizedName),
            AddedAt: now,
            LastLoginAt: now);

        await AddAccountAsync(account, cancellationToken).ConfigureAwait(false);
        await SelectAsync(account.Id, cancellationToken).ConfigureAwait(false);
        return account;
    }

    /// <inheritdoc />
    public async Task<AccountProfile> AddMicrosoftAsync(
        string clientId,
        IProgress<DeviceCodeInfo>? prompt = null,
        CancellationToken cancellationToken = default)
    {
        string normalizedClientId = clientId?.Trim() ?? string.Empty;
        if (normalizedClientId.Length == 0)
        {
            throw new AccountAuthenticationException(
                AccountErrorCode.ClientIdMissing,
                "未配置 Microsoft Client ID，请在设置中填写后重试。");
        }

        DeviceCodeInfo deviceCode = await RunAuthenticationStepAsync(
            () => _oauth.RequestDeviceCodeAsync(normalizedClientId, cancellationToken),
            "device-code request",
            cancellationToken).ConfigureAwait(false);
        prompt?.Report(deviceCode);

        MicrosoftTokenSet tokens = await RunAuthenticationStepAsync(
            () => _oauth.WaitForTokenAsync(
                normalizedClientId,
                deviceCode,
                progress: null,
                cancellationToken: cancellationToken),
            "device-code polling",
            cancellationToken).ConfigureAwait(false);

        XboxLiveToken xboxToken = await RunAuthenticationStepAsync(
            () => _xbox.AuthenticateAsync(tokens.AccessToken, cancellationToken),
            "Xbox Live authentication",
            cancellationToken).ConfigureAwait(false);

        XstsToken xsts = await RunAuthenticationStepAsync(
            () => _xbox.AuthorizeAsync(xboxToken.XboxToken, cancellationToken),
            "XSTS authorization",
            cancellationToken).ConfigureAwait(false);

        string userHash = ResolveUserHash(xboxToken.UserHash, xsts.UserHash);
        MinecraftSessionInfo session = await RunAuthenticationStepAsync(
            () => _minecraft.LoginAsync(userHash, xsts.Token, tokens.RefreshToken, cancellationToken),
            "Minecraft login",
            cancellationToken).ConfigureAwait(false);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        var account = new AccountProfile(
            Guid.NewGuid(),
            AccountKind.Microsoft,
            session.PlayerName,
            session.PlayerUuid,
            string.IsNullOrWhiteSpace(session.Xuid) ? xboxToken.Xuid : session.Xuid,
            Guid.NewGuid().ToString(),
            tokens.RefreshToken,
            now,
            now);

        AccountProfile savedAccount = await AddOrUpdateMicrosoftAccountAsync(account, cancellationToken).ConfigureAwait(false);
        await SelectAsync(savedAccount.Id, cancellationToken).ConfigureAwait(false);
        return savedAccount;
    }

    /// <inheritdoc />
    public async Task RemoveAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<AccountProfile> accounts = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            int index = accounts.FindIndex(account => account.Id == accountId);
            if (index >= 0)
            {
                var updated = new List<AccountProfile>(accounts);
                updated.RemoveAt(index);
                await _store.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
                _accounts = updated;
            }
        }
        finally
        {
            _gate.Release();
        }

        LauncherSettings settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        if (settings.SelectedAccountId == accountId)
        {
            settings.SelectedAccountId = null;
            await _settings.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<MinecraftSessionInfo?> GetMinecraftSessionAsync(
        Guid accountId,
        CancellationToken cancellationToken = default)
    {
        AccountProfile account = await FindAccountAsync(accountId, cancellationToken).ConfigureAwait(false);
        if (account.Kind == AccountKind.Offline)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(account.RefreshToken))
        {
            throw new AccountAuthenticationException(AccountErrorCode.SessionExpired, SessionExpiredMessage);
        }

        string storedRefreshToken = account.RefreshToken!;
        LauncherSettings settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        string oauthClientId = settings.MicrosoftClientId?.Trim() ?? string.Empty;
        if (oauthClientId.Length == 0)
        {
            oauthClientId = account.ClientId?.Trim() ?? string.Empty;
        }

        if (oauthClientId.Length == 0)
        {
            throw new AccountAuthenticationException(AccountErrorCode.ClientIdMissing, ClientIdMissingMessage);
        }

        MicrosoftTokenSet tokens = await RunAuthenticationStepAsync(
            () => _oauth.RefreshAsync(oauthClientId, storedRefreshToken, cancellationToken),
            "token refresh",
            cancellationToken).ConfigureAwait(false);

        string refreshToken = string.IsNullOrWhiteSpace(tokens.RefreshToken)
            ? storedRefreshToken
            : tokens.RefreshToken;
        await PersistRefreshedTokenAsync(account.Id, refreshToken, cancellationToken).ConfigureAwait(false);

        XboxLiveToken xboxToken = await RunAuthenticationStepAsync(
            () => _xbox.AuthenticateAsync(tokens.AccessToken, cancellationToken),
            "Xbox Live authentication",
            cancellationToken).ConfigureAwait(false);

        XstsToken xsts = await RunAuthenticationStepAsync(
            () => _xbox.AuthorizeAsync(xboxToken.XboxToken, cancellationToken),
            "XSTS authorization",
            cancellationToken).ConfigureAwait(false);

        string userHash = ResolveUserHash(xboxToken.UserHash, xsts.UserHash);
        MinecraftSessionInfo session = await RunAuthenticationStepAsync(
            () => _minecraft.LoginAsync(userHash, xsts.Token, refreshToken, cancellationToken),
            "Minecraft login",
            cancellationToken).ConfigureAwait(false);

        string sessionClientId = string.IsNullOrWhiteSpace(session.ClientId)
            ? account.ClientId ?? string.Empty
            : session.ClientId;
        string sessionXuid = string.IsNullOrWhiteSpace(session.Xuid) ? xboxToken.Xuid : session.Xuid;
        return session with
        {
            ClientId = sessionClientId,
            Xuid = sessionXuid,
        };
    }

    /// <summary>Creates the offline UUID exactly like <c>java.util.UUID.nameUUIDFromBytes</c> (version 3, IETF variant).</summary>
    private static string CreateOfflineUuid(string playerName)
    {
        byte[] digest = MD5.HashData(Encoding.UTF8.GetBytes(OfflineUuidPrefix + playerName));
        digest[6] = (byte)((digest[6] & 0x0F) | 0x30);
        digest[8] = (byte)((digest[8] & 0x3F) | 0x80);
        string hex = Convert.ToHexString(digest).ToLowerInvariant();
        // Java prints the 16 digest bytes in order, unlike .NET's little-endian Guid formatting.
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }

    private async Task<AccountProfile> AddOrUpdateMicrosoftAccountAsync(
        AccountProfile account,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<AccountProfile> accounts = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            var updated = new List<AccountProfile>(accounts);
            int existingIndex = accounts.FindIndex(existing => UuidsMatch(existing.PlayerUuid, account.PlayerUuid));
            AccountProfile savedAccount;
            if (existingIndex >= 0)
            {
                AccountProfile existing = accounts[existingIndex];
                savedAccount = account with
                {
                    Id = existing.Id,
                    AddedAt = existing.AddedAt,
                };
                updated[existingIndex] = savedAccount;
            }
            else
            {
                savedAccount = account;
                updated.Add(account);
            }

            await _store.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            _accounts = updated;
            return savedAccount;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task AddAccountAsync(AccountProfile account, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<AccountProfile> accounts = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            var updated = new List<AccountProfile>(accounts) { account };
            await _store.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            _accounts = updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<AccountProfile> FindAccountAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<AccountProfile> accounts = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            return accounts.FirstOrDefault(account => account.Id == accountId)
                ?? throw new InvalidOperationException("未找到指定账户，无法获取 Minecraft 会话。");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task PersistRefreshedTokenAsync(
        Guid accountId,
        string refreshToken,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<AccountProfile> accounts = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            int index = accounts.FindIndex(account => account.Id == accountId);
            if (index < 0)
            {
                return;
            }

            var updated = new List<AccountProfile>(accounts);
            updated[index] = updated[index] with
            {
                RefreshToken = refreshToken,
                LastLoginAt = DateTimeOffset.UtcNow,
            };
            await _store.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            _accounts = updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Loads the account list on first use. The caller must hold <see cref="_gate"/>.</summary>
    private async Task<List<AccountProfile>> EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        List<AccountProfile>? cached = _accounts;
        if (cached is not null)
        {
            return cached;
        }

        IReadOnlyList<AccountProfile> loaded = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        var accounts = new List<AccountProfile>(loaded.Count);
        accounts.AddRange(loaded);
        _accounts = accounts;
        return accounts;
    }

    private async Task<T> RunAuthenticationStepAsync<T>(
        Func<Task<T>> action,
        string operation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (AccountAuthenticationException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(
                exception,
                "The {Operation} failed because the authentication service could not be reached.",
                operation);
            throw new AccountAuthenticationException(
                AccountErrorCode.Network,
                "无法连接 Microsoft 认证服务，请检查网络后重试。",
                exception);
        }
        catch (InvalidDataException exception)
        {
            _logger.LogWarning(exception, "The {Operation} returned invalid data.", operation);
            throw new AccountAuthenticationException(
                AccountErrorCode.InvalidResponse,
                "Microsoft 认证服务返回了无法识别的响应，请稍后重试。",
                exception);
        }
        catch (IOException exception)
        {
            _logger.LogWarning(exception, "The {Operation} failed because of an I/O error.", operation);
            throw new AccountAuthenticationException(
                AccountErrorCode.Network,
                "无法连接 Microsoft 认证服务，请检查网络后重试。",
                exception);
        }
        catch (OperationCanceledException exception)
        {
            _logger.LogWarning(exception, "The {Operation} timed out.", operation);
            throw new AccountAuthenticationException(
                AccountErrorCode.Network,
                "Microsoft 认证服务响应超时，请检查网络后重试。",
                exception);
        }
        catch (TimeoutException exception)
        {
            _logger.LogWarning(exception, "The {Operation} timed out.", operation);
            throw new AccountAuthenticationException(
                AccountErrorCode.Network,
                "Microsoft 认证服务响应超时，请检查网络后重试。",
                exception);
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "The {Operation} returned a response that could not be parsed.", operation);
            throw new AccountAuthenticationException(
                AccountErrorCode.InvalidResponse,
                "Microsoft 认证服务返回了无法识别的响应，请稍后重试。",
                exception);
        }
    }

    private static string ResolveUserHash(string preferred, string fallback) =>
        string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;

    private static bool UuidsMatch(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        return string.Equals(
            left.Replace("-", string.Empty, StringComparison.Ordinal),
            right.Replace("-", string.Empty, StringComparison.Ordinal),
            StringComparison.OrdinalIgnoreCase);
    }
}
