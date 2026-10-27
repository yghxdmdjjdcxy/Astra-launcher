using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using AstraLauncher.App.Commands;
using AstraLauncher.App.Services;
using AstraLauncher.Authentication;
using AstraLauncher.Core.Models;
using AstraLauncher.Core.Services;
using Microsoft.Extensions.Logging;

namespace AstraLauncher.App.ViewModels;

/// <summary>账户页：Microsoft 设备码登录、离线档案以及当前账户选择。</summary>
public sealed class AccountsPageViewModel : PageViewModel
{
    private const int MaximumOfflineNameLength = 16;
    private const string DefaultOfflineName = "Player";
    private const string ClientIdRequiredMessage = "请先填写 Microsoft Client ID（Azure 应用注册的公共客户端 ID）。";

    private readonly ISettingsService _settings;
    private readonly IAccountService _accounts;
    private readonly ILogger<AccountsPageViewModel> _logger;
    private readonly IDialogService _dialogs;
    private CancellationTokenSource? _microsoftLoginCts;
    private AccountProfile? _selectedAccount;
    private string _microsoftClientId = string.Empty;
    private string _addOfflineName = DefaultOfflineName;
    private bool _isBusy;
    private string _statusMessage = "正在加载账户…";
    private bool _hasPendingDeviceCode;
    private string _deviceCodeUserCode = string.Empty;
    private string _deviceCodeMessage = string.Empty;
    private string _deviceCodeVerificationUri = string.Empty;
    private string _deviceCodeExpiresAtText = string.Empty;
    private bool _suppressSelectionPersistence;
    private volatile bool _acceptDeviceCodeProgress;

    public AccountsPageViewModel(
        ISettingsService settings,
        IAccountService accounts,
        ILogger<AccountsPageViewModel> logger,
        IDialogService dialogs) : base("账户")
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(dialogs);
        _settings = settings;
        _accounts = accounts;
        _logger = logger;
        _dialogs = dialogs;
        AddOfflineCommand = new AsyncRelayCommand(AddOfflineAsync);
        AddMicrosoftCommand = new AsyncRelayCommand(AddMicrosoftAsync);
        CancelMicrosoftCommand = new RelayCommand(_ => CancelMicrosoftLogin());
        RemoveAccountCommand = new AsyncRelayCommand(RemoveAccountAsync);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
    }

    public ObservableCollection<AccountProfile> Accounts { get; } = [];

    /// <summary>
    /// Gets or sets the account selected in the list. Changing the selection persists it through
    /// <see cref="IAccountService"/>; list reloads suppress that call.
    /// </summary>
    public AccountProfile? SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            if (!SetProperty(ref _selectedAccount, value)) return;
            if (!_suppressSelectionPersistence)
            {
                _ = PersistSelectionAsync(value);
            }
        }
    }

    public string MicrosoftClientId { get => _microsoftClientId; set => SetProperty(ref _microsoftClientId, value); }

    public string AddOfflineName { get => _addOfflineName; set => SetProperty(ref _addOfflineName, value); }

    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }

    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }

    public bool HasPendingDeviceCode { get => _hasPendingDeviceCode; private set => SetProperty(ref _hasPendingDeviceCode, value); }

    public string DeviceCodeUserCode { get => _deviceCodeUserCode; private set => SetProperty(ref _deviceCodeUserCode, value); }

    public string DeviceCodeMessage { get => _deviceCodeMessage; private set => SetProperty(ref _deviceCodeMessage, value); }

    public string DeviceCodeVerificationUri { get => _deviceCodeVerificationUri; private set => SetProperty(ref _deviceCodeVerificationUri, value); }

    /// <summary>Gets the localized expiry hint for the currently displayed device code.</summary>
    public string DeviceCodeExpiresAtText { get => _deviceCodeExpiresAtText; private set => SetProperty(ref _deviceCodeExpiresAtText, value); }

    public ICommand AddOfflineCommand { get; }

    public ICommand AddMicrosoftCommand { get; }

    public ICommand CancelMicrosoftCommand { get; }

    public ICommand RemoveAccountCommand { get; }

    public ICommand RefreshCommand { get; }

    /// <summary>Loads the saved client id/offline name and the persisted account list.</summary>
    public async Task InitializeAsync()
    {
        IsBusy = true;
        try
        {
            LauncherSettings settings = await _settings.GetAsync();
            MicrosoftClientId = settings.MicrosoftClientId ?? string.Empty;
            AddOfflineName = string.IsNullOrWhiteSpace(settings.OfflinePlayerName) ? DefaultOfflineName : settings.OfflinePlayerName;
            await ReloadAccountsAsync(settings.SelectedAccountId);
            StatusMessage = $"已加载 {Accounts.Count} 个账户。";
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "加载账户页失败。");
            StatusMessage = FriendlyError(exception, "加载账户");
            _dialogs.ShowError(StatusMessage);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task AddOfflineAsync()
    {
        string playerName = AddOfflineName.Trim();
        if (playerName.Length == 0)
        {
            ShowValidationError("离线玩家名不能为空。");
            return;
        }

        if (playerName.Length > MaximumOfflineNameLength)
        {
            ShowValidationError($"离线玩家名不能超过 {MaximumOfflineNameLength} 个字符。");
            return;
        }

        IsBusy = true;
        try
        {
            AccountProfile account = await _accounts.AddOfflineAsync(playerName);
            LauncherSettings settings = await _settings.GetAsync();
            settings.OfflinePlayerName = playerName;
            await _settings.SaveAsync(settings);
            await ReloadAccountsAsync(account.Id);
            AddOfflineName = account.PlayerName;
            StatusMessage = $"已添加离线档案：{playerName}";
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "添加离线档案失败。");
            StatusMessage = FriendlyError(exception, "添加离线档案");
            _dialogs.ShowError(StatusMessage);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task AddMicrosoftAsync()
    {
        string clientId = MicrosoftClientId.Trim();
        if (clientId.Length == 0)
        {
            ShowValidationError(ClientIdRequiredMessage);
            return;
        }

        MicrosoftClientId = clientId;
        IsBusy = true;
        HasPendingDeviceCode = true;
        _acceptDeviceCodeProgress = true;
        ResetDeviceCodeDetails();
        DeviceCodeMessage = "正在请求 Microsoft 设备代码…";
        CancellationTokenSource? cancellation = null;
        try
        {
            LauncherSettings settings = await _settings.GetAsync();
            settings.MicrosoftClientId = clientId;
            await _settings.SaveAsync(settings);

            cancellation = new CancellationTokenSource();
            _microsoftLoginCts = cancellation;
            var progress = new Progress<DeviceCodeInfo>(OnDeviceCodeReceived);
            AccountProfile account = await _accounts.AddMicrosoftAsync(clientId, progress, cancellation.Token);
            await ReloadAccountsAsync(account.Id);
            await PersistSelectionAsync(account);
            HasPendingDeviceCode = false;
            StatusMessage = $"已添加 Microsoft 账户：{account.PlayerName}";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "已取消 Microsoft 登录。";
        }
        catch (AccountAuthenticationException exception)
        {
            // Never log tokens: the exception carries only the categorized failure and a safe message.
            _logger.LogError(exception, "Microsoft 登录失败：{ErrorCode}", exception.Code);
            StatusMessage = FriendlyError(exception);
            _dialogs.ShowError(StatusMessage);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Microsoft 登录失败。");
            StatusMessage = FriendlyError(exception, "Microsoft 登录");
            _dialogs.ShowError(StatusMessage);
        }
        finally
        {
            _acceptDeviceCodeProgress = false;
            _microsoftLoginCts = null;
            cancellation?.Dispose();
            IsBusy = false;
            HasPendingDeviceCode = false;
        }
    }

    private void CancelMicrosoftLogin()
    {
        CancellationTokenSource? cancellation = _microsoftLoginCts;
        if (cancellation is null) return;
        StatusMessage = "正在取消 Microsoft 登录…";
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The login finished between reading the field and cancelling; there is nothing left to cancel.
        }
    }

    private async Task RemoveAccountAsync()
    {
        AccountProfile? account = SelectedAccount;
        if (account is null)
        {
            StatusMessage = "请先选择要删除的账户。";
            return;
        }

        if (!_dialogs.Confirm($"确定要删除账户 {account.PlayerName} 吗？")) return;

        IsBusy = true;
        try
        {
            await _accounts.RemoveAsync(account.Id);
            await ReloadAccountsAsync(null);
            StatusMessage = $"已删除账户：{account.PlayerName}。";
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "删除账户失败：{PlayerName}", account.PlayerName);
            StatusMessage = FriendlyError(exception, "删除账户");
            _dialogs.ShowError(StatusMessage);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            LauncherSettings settings = await _settings.GetAsync();
            await ReloadAccountsAsync(settings.SelectedAccountId);
            StatusMessage = $"已刷新，共 {Accounts.Count} 个账户。";
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "刷新账户列表失败。");
            StatusMessage = FriendlyError(exception, "刷新账户列表");
            _dialogs.ShowError(StatusMessage);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task PersistSelectionAsync(AccountProfile? account)
    {
        try
        {
            await _accounts.SelectAsync(account?.Id);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "保存账户选择失败。");
            StatusMessage = FriendlyError(exception, "选择账户");
            _dialogs.ShowError(StatusMessage);
        }
    }

    private async Task ReloadAccountsAsync(Guid? selectedAccountId)
    {
        IReadOnlyList<AccountProfile> accounts = await _accounts.GetAccountsAsync();
        AccountProfile? selected = selectedAccountId is Guid id
            ? accounts.FirstOrDefault(account => account.Id == id)
            : null;
        ApplyAccounts(accounts, selected);
    }

    private void ApplyAccounts(IReadOnlyList<AccountProfile> accounts, AccountProfile? selected)
    {
        _suppressSelectionPersistence = true;
        try
        {
            Accounts.Clear();
            foreach (AccountProfile account in accounts)
            {
                Accounts.Add(account);
            }

            // AccountProfile is a record; clear the selection first so value equality cannot suppress the
            // notification and leave WPF bound to an object that is no longer in Accounts.
            SetProperty(ref _selectedAccount, null, nameof(SelectedAccount));
            SelectedAccount = selected;
        }
        finally
        {
            _suppressSelectionPersistence = false;
        }
    }

    private void OnDeviceCodeReceived(DeviceCodeInfo info)
    {
        DeviceCodeUserCode = info.UserCode;
        DeviceCodeVerificationUri = info.VerificationUri;
        DeviceCodeMessage = info.Message;
        DeviceCodeExpiresAtText = $"设备码有效期至 {info.ExpiresAt.ToLocalTime():HH:mm:ss}";
        if (_acceptDeviceCodeProgress)
        {
            // A late Progress callback must not reopen the panel after the login already finished.
            HasPendingDeviceCode = true;
        }
    }

    private void ResetDeviceCodeDetails()
    {
        DeviceCodeUserCode = string.Empty;
        DeviceCodeVerificationUri = string.Empty;
        DeviceCodeMessage = string.Empty;
        DeviceCodeExpiresAtText = string.Empty;
    }

    private void ShowValidationError(string message)
    {
        StatusMessage = message;
        _dialogs.ShowError(message);
    }

    private static string FriendlyError(AccountAuthenticationException exception) => exception.Code switch
    {
        AccountErrorCode.ClientIdMissing => "未配置 Microsoft Client ID。请在 Azure 门户创建公共客户端应用并填写应用程序(客户端) ID。",
        AccountErrorCode.DeviceCodeExpired => "Microsoft 设备码已过期，请重新点击“添加 Microsoft 账户”获取新代码。",
        AccountErrorCode.AuthorizationDeclined => "Microsoft 登录授权被拒绝，请重新登录并允许 Astra Launcher 访问该账户。",
        AccountErrorCode.XboxNoAccount => "该 Microsoft 账户尚未创建 Xbox 档案，无法登录 Minecraft。",
        AccountErrorCode.XboxChildAccount => "该账户受家庭安全限制（儿童账户），无法登录 Minecraft；请调整家庭设置或使用其他账户。",
        AccountErrorCode.XboxCountryUnavailable => "Xbox Live 在当前国家或地区不可用，无法登录 Minecraft。",
        AccountErrorCode.XboxBanned => "该 Xbox 账户已被封禁，无法登录 Minecraft。",
        AccountErrorCode.MinecraftNotOwned => "该 Microsoft 账户未购买 Minecraft Java 版，无法作为官方账户登录。",
        AccountErrorCode.SessionExpired => "Microsoft 登录会话已过期，请重新添加该账户。",
        AccountErrorCode.Network => "网络连接失败，请检查网络或代理设置后重试。",
        AccountErrorCode.InvalidResponse => "Microsoft 或 Minecraft 服务返回了无效响应，请稍后重试。",
        _ => $"Microsoft 登录失败：{exception.Message}"
    };

    private static string FriendlyError(Exception exception, string operation) => exception switch
    {
        UnauthorizedAccessException => $"{operation}失败：没有访问权限，请检查账户数据目录的权限。",
        IOException ioException => $"{operation}失败：{ioException.Message}",
        InvalidOperationException invalidOperationException => $"{operation}失败：{invalidOperationException.Message}",
        ArgumentException argumentException => $"{operation}失败：参数无效（{argumentException.Message}）。",
        _ => $"{operation}失败，请稍后重试。"
    };
}
