using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AstraLauncher.Core.Services;
using Microsoft.Extensions.Logging;

namespace AstraLauncher.Authentication;

/// <summary>
/// Persists accounts in <c>&lt;ApplicationDataDirectory&gt;/accounts.json</c>.
/// Refresh tokens are DPAPI-protected before serialization; access tokens are never persisted.
/// Writes are atomic and serialized through a single gate.
/// </summary>
public sealed class AccountStore : IAccountStore
{
    private const string AccountsFileName = "accounts.json";
    private const string CorruptFilePrefix = "accounts.corrupt-";
    private const string JsonFileExtension = ".json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ILogger<AccountStore> _logger;
    private readonly ITokenProtector _protector;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _accountsDirectory;
    private readonly string _accountsFilePath;

    /// <summary>Initializes a store rooted at the launcher-private application data directory.</summary>
    public AccountStore(ILauncherPaths paths, ITokenProtector protector, ILogger<AccountStore> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(logger);
        _protector = protector;
        _logger = logger;
        _accountsDirectory = paths.ApplicationDataDirectory;
        _accountsFilePath = Path.Combine(_accountsDirectory, AccountsFileName);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AccountProfile>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(IReadOnlyList<AccountProfile> accounts, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accounts);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var records = new List<AccountRecord>(accounts.Count);
            foreach (AccountProfile account in accounts)
            {
                ArgumentNullException.ThrowIfNull(account);
                string? refreshToken = account.RefreshToken;
                string? protectedRefreshToken = string.IsNullOrEmpty(refreshToken)
                    ? null
                    : _protector.Protect(refreshToken);
                records.Add(new AccountRecord(
                    account.Id,
                    account.Kind,
                    account.PlayerName,
                    account.PlayerUuid,
                    account.Xuid,
                    account.ClientId,
                    protectedRefreshToken,
                    account.AddedAt,
                    account.LastLoginAt));
            }

            var document = new AccountsDocument(records);
            Directory.CreateDirectory(_accountsDirectory);
            string temporaryPath = $"{_accountsFilePath}.{Guid.NewGuid():N}.tmp";
            try
            {
                await using (FileStream stream = new(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                File.Move(temporaryPath, _accountsFilePath, overwrite: true);
            }
            finally
            {
                TryDelete(temporaryPath);
            }
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new IOException($"无法写入账户文件：{_accountsFilePath}", exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<AccountProfile>> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_accountsFilePath))
        {
            return Array.Empty<AccountProfile>();
        }

        try
        {
            AccountsDocument? document;
            await using (FileStream stream = new(
                _accountsFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                document = await JsonSerializer.DeserializeAsync<AccountsDocument>(
                    stream,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);
            }

            if (document is null)
            {
                QuarantineCorruptFile(new JsonException("账户文件缺少有效的根对象。"));
                return Array.Empty<AccountProfile>();
            }

            List<AccountRecord>? records = document.Accounts;
            if (records is null || records.Count == 0)
            {
                return Array.Empty<AccountProfile>();
            }

            var accounts = new List<AccountProfile>(records.Count);
            foreach (AccountRecord record in records)
            {
                if (record.Id == Guid.Empty
                    || string.IsNullOrWhiteSpace(record.PlayerName)
                    || string.IsNullOrWhiteSpace(record.PlayerUuid)
                    || !Enum.IsDefined(record.Kind))
                {
                    _logger.LogWarning(
                        "Skipping an invalid account entry in {AccountsPath} (id: {AccountId}).",
                        _accountsFilePath,
                        record.Id);
                    continue;
                }

                accounts.Add(ToProfile(record));
            }

            return accounts.AsReadOnly();
        }
        catch (JsonException exception)
        {
            QuarantineCorruptFile(exception);
            return Array.Empty<AccountProfile>();
        }
        catch (IOException exception)
        {
            _logger.LogWarning(
                exception,
                "Failed to read accounts from {AccountsPath}; no accounts are available for this session.",
                _accountsFilePath);
            return Array.Empty<AccountProfile>();
        }
        catch (UnauthorizedAccessException exception)
        {
            _logger.LogWarning(
                exception,
                "Access to accounts file {AccountsPath} was denied; no accounts are available for this session.",
                _accountsFilePath);
            return Array.Empty<AccountProfile>();
        }
    }

    private AccountProfile ToProfile(AccountRecord record)
    {
        string? refreshToken = null;
        string? protectedRefreshToken = record.ProtectedRefreshToken;
        if (!string.IsNullOrEmpty(protectedRefreshToken))
        {
            try
            {
                refreshToken = _protector.Unprotect(protectedRefreshToken);
            }
            catch (InvalidOperationException exception)
            {
                _logger.LogWarning(
                    exception,
                    "The stored refresh token for account {AccountId} could not be decrypted; the account requires a new sign-in.",
                    record.Id);
            }
        }

        return new AccountProfile(
            record.Id,
            record.Kind,
            record.PlayerName,
            record.PlayerUuid,
            record.Xuid,
            record.ClientId,
            refreshToken,
            record.AddedAt,
            record.LastLoginAt);
    }

    /// <summary>Moves a corrupt accounts document aside so the evidence is preserved instead of silently overwritten.</summary>
    private void QuarantineCorruptFile(JsonException exception)
    {
        string timestamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        string corruptPath = Path.Combine(
            _accountsDirectory,
            $"{CorruptFilePrefix}{timestamp}{JsonFileExtension}");

        try
        {
            File.Move(_accountsFilePath, corruptPath, overwrite: true);
            _logger.LogWarning(
                exception,
                "Accounts file {AccountsPath} was corrupt; it was moved to {CorruptPath} and no accounts are loaded.",
                _accountsFilePath,
                corruptPath);
        }
        catch (IOException quarantineException)
        {
            _logger.LogWarning(
                quarantineException,
                "Accounts file {AccountsPath} is corrupt and could not be quarantined; no accounts are loaded.",
                _accountsFilePath);
        }
        catch (UnauthorizedAccessException quarantineException)
        {
            _logger.LogWarning(
                quarantineException,
                "Accounts file {AccountsPath} is corrupt and could not be quarantined; no accounts are loaded.",
                _accountsFilePath);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Cleanup is best-effort; a stale temporary file is ignored by the next atomic save.
        }
        catch (UnauthorizedAccessException)
        {
            // Same as above: a locked temporary file must not turn a successful save into a failure.
        }
    }

    private sealed record AccountsDocument(List<AccountRecord>? Accounts);

    private sealed record AccountRecord(
        Guid Id,
        AccountKind Kind,
        string PlayerName,
        string PlayerUuid,
        string? Xuid,
        string? ClientId,
        string? ProtectedRefreshToken,
        DateTimeOffset AddedAt,
        DateTimeOffset? LastLoginAt);
}
