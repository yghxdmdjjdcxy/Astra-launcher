namespace AstraLauncher.Authentication;

/// <summary>How an account proves its identity.</summary>
public enum AccountKind
{
    /// <summary>Local offline profile; the UUID is derived from the player name.</summary>
    Offline,

    /// <summary>Official Microsoft account (Microsoft OAuth → Xbox Live → XSTS → Minecraft).</summary>
    Microsoft,
}

/// <summary>Error categories surfaced by the official authentication chain; the UI maps them to Chinese messages.</summary>
public enum AccountErrorCode
{
    Unknown,
    ClientIdMissing,
    Network,
    InvalidResponse,
    DeviceCodeExpired,
    AuthorizationDeclined,
    XboxNoAccount,
    XboxChildAccount,
    XboxCountryUnavailable,
    XboxBanned,
    MinecraftNotOwned,
    SessionExpired,
}

/// <summary>An account known to the launcher. Refresh/access tokens never leave this object unencrypted except in memory.</summary>
public sealed record AccountProfile(
    Guid Id,
    AccountKind Kind,
    string PlayerName,
    string PlayerUuid,
    string? Xuid = null,
    string? ClientId = null,
    string? RefreshToken = null,
    DateTimeOffset AddedAt = default,
    DateTimeOffset? LastLoginAt = null)
{
    /// <summary>Gets a value indicating whether the account uses the official authentication chain.</summary>
    public bool IsMicrosoft => Kind == AccountKind.Microsoft;
}

/// <summary>Device-code flow prompt returned by Microsoft identity platform.</summary>
public sealed record DeviceCodeInfo(
    string UserCode,
    string VerificationUri,
    string? VerificationUriComplete,
    string Message,
    DateTimeOffset ExpiresAt,
    TimeSpan Interval);

/// <summary>Microsoft OAuth token set for the <c>XboxLive.signin offline_access</c> scopes.</summary>
public sealed record MicrosoftTokenSet(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);

/// <summary>Xbox Live user token and user hash.</summary>
public sealed record XboxLiveToken(string XboxToken, string UserHash, string Xuid);

/// <summary>XSTS authorization token for the Minecraft relying party.</summary>
public sealed record XstsToken(string Token, string UserHash, string Xuid);

/// <summary>Everything needed to launch Minecraft as an authenticated player.</summary>
public sealed record MinecraftSessionInfo(
    string PlayerName,
    string PlayerUuid,
    string Xuid,
    string ClientId,
    string AccessToken,
    DateTimeOffset ExpiresAt);

/// <summary>An expected failure of the official authentication chain.</summary>
public sealed class AccountAuthenticationException : Exception
{
    public AccountAuthenticationException(AccountErrorCode code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public AccountErrorCode Code { get; }
}

/// <summary>Device-code OAuth client for Microsoft identity platform (public client, no secret).</summary>
public interface IDeviceCodeOAuthClient
{
    /// <summary>Requests a device code; the caller shows the code/verification URI to the user.</summary>
    Task<DeviceCodeInfo> RequestDeviceCodeAsync(string clientId, CancellationToken cancellationToken = default);

    /// <summary>Polls the token endpoint until the user finishes signing in, the device code expires or the flow is cancelled.</summary>
    Task<MicrosoftTokenSet> WaitForTokenAsync(string clientId, DeviceCodeInfo deviceCode, IProgress<string>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Refreshes an expired access token using a stored refresh token.</summary>
    Task<MicrosoftTokenSet> RefreshAsync(string clientId, string refreshToken, CancellationToken cancellationToken = default);
}

/// <summary>Interactive (Xbox-style popup) Microsoft account login using the built-in public client.</summary>
public interface IMicrosoftInteractiveLoginClient
{
    /// <summary>Gets the public client id used by the interactive login/refresh flow.</summary>
    string ClientId { get; }

    /// <summary>Opens the Microsoft sign-in window and returns the issued token set.</summary>
    Task<MicrosoftTokenSet> SignInAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Refreshes an expired access token using a stored refresh token (legacy Live endpoint).</summary>
    Task<MicrosoftTokenSet> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default);
}

/// <summary>Xbox Live user authentication and XSTS authorization for the Minecraft relying party.</summary>
public interface IXboxLiveAuthenticator
{
    Task<XboxLiveToken> AuthenticateAsync(string microsoftAccessToken, CancellationToken cancellationToken = default);

    Task<XstsToken> AuthorizeAsync(string xboxLiveToken, CancellationToken cancellationToken = default);
}

/// <summary>Minecraft services authentication (login_with_xbox + profile).</summary>
public interface IMinecraftAuthenticator
{
    Task<MinecraftSessionInfo> LoginAsync(string xboxUserHash, string xstsToken, string refreshToken, CancellationToken cancellationToken = default);
}

/// <summary>Protects sensitive strings (refresh tokens) with Windows DPAPI for the current user.</summary>
public interface ITokenProtector
{
    /// <summary>Encrypts <paramref name="plaintext"/> and returns a safe-to-store string.</summary>
    string Protect(string plaintext);

    /// <summary>Decrypts a value produced by <see cref="Protect"/>.</summary>
    string Unprotect(string protectedText);
}

/// <summary>Persists accounts (metadata plus DPAPI-protected refresh tokens) under the launcher data directory.</summary>
public interface IAccountStore
{
    Task<IReadOnlyList<AccountProfile>> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(IReadOnlyList<AccountProfile> accounts, CancellationToken cancellationToken = default);
}

/// <summary>High-level account management used by the UI and the launch pipeline.</summary>
public interface IAccountService
{
    Task<IReadOnlyList<AccountProfile>> GetAccountsAsync(CancellationToken cancellationToken = default);

    /// <summary>Gets the selected account (persisted through launcher settings) or <see langword="null"/> when none is selected.</summary>
    Task<AccountProfile?> GetSelectedAsync(CancellationToken cancellationToken = default);

    /// <summary>Selects an account (or clears the selection with <see langword="null"/>).</summary>
    Task<AccountProfile?> SelectAsync(Guid? accountId, CancellationToken cancellationToken = default);

    /// <summary>Creates and selects an offline profile.</summary>
    Task<AccountProfile> AddOfflineAsync(string playerName, CancellationToken cancellationToken = default);

    /// <summary>Runs the full Microsoft device-code flow and stores the resulting account.</summary>
    Task<AccountProfile> AddMicrosoftAsync(string clientId, IProgress<DeviceCodeInfo>? prompt = null, CancellationToken cancellationToken = default);

    /// <summary>Runs the Xbox-style interactive popup login (built-in public client) and stores the resulting account.</summary>
    Task<AccountProfile> AddMicrosoftInteractiveAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default);

    Task RemoveAsync(Guid accountId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a fresh Minecraft session for a Microsoft account (refreshing and persisting tokens as needed).
    /// Returns <see langword="null"/> for offline accounts; throws <see cref="AccountAuthenticationException"/> when the account must be re-authenticated.
    /// </summary>
    Task<MinecraftSessionInfo?> GetMinecraftSessionAsync(Guid accountId, CancellationToken cancellationToken = default);
}
