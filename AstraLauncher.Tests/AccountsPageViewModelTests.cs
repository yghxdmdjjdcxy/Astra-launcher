using AstraLauncher.App.Services;
using AstraLauncher.App.ViewModels;
using AstraLauncher.Authentication;
using AstraLauncher.Core.Models;
using AstraLauncher.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AstraLauncher.Tests;

public sealed class AccountsPageViewModelTests
{
    [Fact]
    public async Task InitializeAsync_LoadsSettingsAndSelectsSavedAccount()
    {
        var offline = new AccountProfile(Guid.NewGuid(), AccountKind.Offline, "AstraTester", Guid.NewGuid().ToString("N"));
        var microsoft = new AccountProfile(Guid.NewGuid(), AccountKind.Microsoft, "AstraMojang", Guid.NewGuid().ToString("N"), "123456789", "client-id-123");
        var settings = new FakeSettingsService
        {
            Current = new LauncherSettings
            {
                MicrosoftClientId = "client-id-123",
                OfflinePlayerName = "AstraTester",
                SelectedAccountId = microsoft.Id,
            },
        };
        var accounts = new FakeAccountService();
        accounts.Accounts.AddRange([offline, microsoft]);
        var dialogs = new RecordingDialogService();
        AccountsPageViewModel viewModel = CreateViewModel(settings, accounts, dialogs);

        await viewModel.InitializeAsync();

        Assert.Equal("client-id-123", viewModel.MicrosoftClientId);
        Assert.Equal("AstraTester", viewModel.AddOfflineName);
        Assert.Equal(2, viewModel.Accounts.Count);
        Assert.Equal(microsoft.Id, viewModel.SelectedAccount?.Id);
        Assert.Equal("已加载 2 个账户。", viewModel.StatusMessage);
        Assert.False(viewModel.IsBusy);
        Assert.Empty(accounts.SelectedIds);
        Assert.Empty(dialogs.ErrorMessages);
    }

    [Fact]
    public async Task AddOfflineCommand_AddsProfilePersistsNameAndSelectsIt()
    {
        var settings = new FakeSettingsService();
        var accounts = new FakeAccountService();
        var dialogs = new RecordingDialogService();
        AccountsPageViewModel viewModel = CreateViewModel(settings, accounts, dialogs);
        await viewModel.InitializeAsync();
        viewModel.AddOfflineName = "AstraTester";

        viewModel.AddOfflineCommand.Execute(null);
        await WaitForAsync(() => settings.Saves.Count == 1 && viewModel.AddOfflineCommand.CanExecute(null), "添加离线档案完成");

        Assert.Equal("AstraTester", Assert.Single(accounts.AddedOfflineNames));
        Assert.Equal("AstraTester", Assert.Single(settings.Saves).OfflinePlayerName);
        Assert.Equal("AstraTester", viewModel.SelectedAccount?.PlayerName);
        Assert.Contains(viewModel.Accounts, account => account.Id == viewModel.SelectedAccount?.Id);
        Assert.Equal("已添加离线档案：AstraTester", viewModel.StatusMessage);
        Assert.False(viewModel.IsBusy);
        Assert.Empty(dialogs.ErrorMessages);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("12345678901234567")]
    public void AddOfflineCommand_WhenNameIsInvalid_ShowsErrorAndDoesNotCallService(string playerName)
    {
        var settings = new FakeSettingsService();
        var accounts = new FakeAccountService();
        var dialogs = new RecordingDialogService();
        AccountsPageViewModel viewModel = CreateViewModel(settings, accounts, dialogs);
        viewModel.AddOfflineName = playerName;

        viewModel.AddOfflineCommand.Execute(null);

        Assert.Equal(viewModel.StatusMessage, Assert.Single(dialogs.ErrorMessages));
        Assert.Contains("离线玩家名", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Empty(accounts.AddedOfflineNames);
        Assert.Empty(settings.Saves);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public void AddMicrosoftCommand_WhenClientIdIsMissing_ShowsErrorAndDoesNotSignIn()
    {
        var settings = new FakeSettingsService();
        var accounts = new FakeAccountService();
        var dialogs = new RecordingDialogService();
        AccountsPageViewModel viewModel = CreateViewModel(settings, accounts, dialogs);
        viewModel.MicrosoftClientId = "   ";

        viewModel.AddMicrosoftCommand.Execute(null);

        Assert.Equal(viewModel.StatusMessage, Assert.Single(dialogs.ErrorMessages));
        Assert.Contains("Client ID", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Empty(accounts.MicrosoftClientIds);
        Assert.Empty(settings.Saves);
        Assert.False(viewModel.IsBusy);
        Assert.False(viewModel.HasPendingDeviceCode);
    }

    [Fact]
    public async Task AddMicrosoftCommand_SavesClientIdReportsDeviceCodeAndRefreshesAccounts()
    {
        var settings = new FakeSettingsService();
        var accounts = new FakeAccountService
        {
            DeviceCode = new DeviceCodeInfo(
                "ABC123",
                "https://microsoft.com/devicelogin",
                null,
                "请在浏览器中登录。",
                DateTimeOffset.UtcNow.AddMinutes(15),
                TimeSpan.FromSeconds(5)),
            MicrosoftPlayerName = "AstraMojang",
        };
        var dialogs = new RecordingDialogService();
        AccountsPageViewModel viewModel = CreateViewModel(settings, accounts, dialogs);
        viewModel.MicrosoftClientId = "  client-abc  ";

        viewModel.AddMicrosoftCommand.Execute(null);
        await WaitForAsync(() => accounts.MicrosoftClientIds.Count == 1 && viewModel.AddMicrosoftCommand.CanExecute(null), "Microsoft 登录完成");
        await WaitForAsync(
            () => string.Equals(viewModel.DeviceCodeUserCode, "ABC123", StringComparison.Ordinal) && viewModel.DeviceCodeExpiresAtText.Length > 0,
            "设备码进度更新");

        Assert.Equal("client-abc", Assert.Single(settings.Saves).MicrosoftClientId);
        Assert.Equal("client-abc", Assert.Single(accounts.MicrosoftClientIds));
        Assert.Equal("AstraMojang", viewModel.SelectedAccount?.PlayerName);
        Assert.Equal("已添加 Microsoft 账户：AstraMojang", viewModel.StatusMessage);
        Assert.Equal("ABC123", viewModel.DeviceCodeUserCode);
        Assert.Equal("https://microsoft.com/devicelogin", viewModel.DeviceCodeVerificationUri);
        Assert.Equal("请在浏览器中登录。", viewModel.DeviceCodeMessage);
        Assert.Contains("设备码有效期至", viewModel.DeviceCodeExpiresAtText, StringComparison.Ordinal);
        Assert.False(viewModel.HasPendingDeviceCode);
        Assert.False(viewModel.IsBusy);
        Assert.Empty(dialogs.ErrorMessages);
    }

    [Fact]
    public async Task AddMicrosoftCommand_WhenAuthenticationFails_ShowsChineseErrorAndResetsState()
    {
        var settings = new FakeSettingsService();
        var accounts = new FakeAccountService
        {
            DeviceCode = new DeviceCodeInfo(
                "ABC123",
                "https://microsoft.com/devicelogin",
                null,
                "请在浏览器中登录。",
                DateTimeOffset.UtcNow.AddMinutes(15),
                TimeSpan.FromSeconds(5)),
            MicrosoftException = new AccountAuthenticationException(AccountErrorCode.MinecraftNotOwned, "The account does not own Minecraft."),
        };
        var dialogs = new RecordingDialogService();
        AccountsPageViewModel viewModel = CreateViewModel(settings, accounts, dialogs);
        viewModel.MicrosoftClientId = "client-abc";

        viewModel.AddMicrosoftCommand.Execute(null);
        await WaitForAsync(() => dialogs.ErrorMessages.Count == 1 && viewModel.AddMicrosoftCommand.CanExecute(null), "Microsoft 登录失败处理完成");

        Assert.Contains("Minecraft", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(viewModel.StatusMessage, Assert.Single(dialogs.ErrorMessages));
        Assert.Empty(viewModel.Accounts);
        Assert.False(viewModel.IsBusy);
        Assert.False(viewModel.HasPendingDeviceCode);
    }

    [Fact]
    public async Task RemoveAccountCommand_WhenConfirmationIsDeclined_DoesNotRemove()
    {
        var account = new AccountProfile(Guid.NewGuid(), AccountKind.Offline, "AstraTester", Guid.NewGuid().ToString("N"));
        var settings = new FakeSettingsService { Current = new LauncherSettings { SelectedAccountId = account.Id } };
        var accounts = new FakeAccountService();
        accounts.Accounts.Add(account);
        var dialogs = new RecordingDialogService { ConfirmResult = false };
        AccountsPageViewModel viewModel = CreateViewModel(settings, accounts, dialogs);
        await viewModel.InitializeAsync();

        viewModel.RemoveAccountCommand.Execute(null);
        await WaitForAsync(() => viewModel.RemoveAccountCommand.CanExecute(null), "取消删除完成");

        Assert.Empty(accounts.RemovedIds);
        Assert.Equal(account.Id, viewModel.SelectedAccount?.Id);
        Assert.Contains("AstraTester", Assert.Single(dialogs.ConfirmMessages), StringComparison.Ordinal);
        Assert.Empty(dialogs.ErrorMessages);
    }

    [Fact]
    public async Task RemoveAccountCommand_WhenConfirmed_RemovesAccountAndClearsSelection()
    {
        var account = new AccountProfile(Guid.NewGuid(), AccountKind.Offline, "AstraTester", Guid.NewGuid().ToString("N"));
        var settings = new FakeSettingsService { Current = new LauncherSettings { SelectedAccountId = account.Id } };
        var accounts = new FakeAccountService();
        accounts.Accounts.Add(account);
        var dialogs = new RecordingDialogService { ConfirmResult = true };
        AccountsPageViewModel viewModel = CreateViewModel(settings, accounts, dialogs);
        await viewModel.InitializeAsync();

        viewModel.RemoveAccountCommand.Execute(null);
        await WaitForAsync(() => accounts.RemovedIds.Count == 1 && viewModel.RemoveAccountCommand.CanExecute(null), "删除账户完成");

        Assert.Equal(account.Id, Assert.Single(accounts.RemovedIds));
        Assert.Empty(viewModel.Accounts);
        Assert.Null(viewModel.SelectedAccount);
        Assert.Equal("已删除账户：AstraTester。", viewModel.StatusMessage);
        Assert.False(viewModel.IsBusy);
        Assert.Empty(dialogs.ErrorMessages);
    }

    [Fact]
    public async Task RefreshCommand_ReloadsAccountsAndSelection()
    {
        var first = new AccountProfile(Guid.NewGuid(), AccountKind.Offline, "AstraTester", Guid.NewGuid().ToString("N"));
        var second = new AccountProfile(Guid.NewGuid(), AccountKind.Microsoft, "AstraMojang", Guid.NewGuid().ToString("N"), "987654321", "client-abc");
        var settings = new FakeSettingsService { Current = new LauncherSettings { SelectedAccountId = first.Id } };
        var accounts = new FakeAccountService();
        accounts.Accounts.Add(first);
        var dialogs = new RecordingDialogService();
        AccountsPageViewModel viewModel = CreateViewModel(settings, accounts, dialogs);
        await viewModel.InitializeAsync();
        int initialLoads = accounts.GetAccountsCalls;

        accounts.Accounts.Add(second);
        settings.Current.SelectedAccountId = second.Id;
        viewModel.RefreshCommand.Execute(null);
        await WaitForAsync(() => accounts.GetAccountsCalls > initialLoads && viewModel.RefreshCommand.CanExecute(null), "刷新账户列表完成");

        Assert.Equal(2, viewModel.Accounts.Count);
        Assert.Equal(second.Id, viewModel.SelectedAccount?.Id);
        Assert.Equal("已刷新，共 2 个账户。", viewModel.StatusMessage);
        Assert.Empty(dialogs.ErrorMessages);
    }

    [Fact]
    public async Task CancelMicrosoftCommand_CancelsPendingLoginAndResetsState()
    {
        var settings = new FakeSettingsService();
        var accounts = new FakeAccountService
        {
            DeviceCode = new DeviceCodeInfo(
                "ABC123",
                "https://microsoft.com/devicelogin",
                null,
                "请在浏览器中登录。",
                DateTimeOffset.UtcNow.AddMinutes(15),
                TimeSpan.FromSeconds(5)),
            WaitForCancellationBeforeCompletion = true,
        };
        var dialogs = new RecordingDialogService();
        AccountsPageViewModel viewModel = CreateViewModel(settings, accounts, dialogs);
        viewModel.MicrosoftClientId = "client-abc";

        viewModel.AddMicrosoftCommand.Execute(null);
        await WaitForAsync(() => accounts.MicrosoftStarted.Task.IsCompleted, "Microsoft 登录开始");
        Assert.True(viewModel.IsBusy);
        Assert.True(viewModel.HasPendingDeviceCode);

        viewModel.CancelMicrosoftCommand.Execute(null);
        await WaitForAsync(() => viewModel.AddMicrosoftCommand.CanExecute(null), "取消 Microsoft 登录完成");

        Assert.Equal("已取消 Microsoft 登录。", viewModel.StatusMessage);
        Assert.False(viewModel.IsBusy);
        Assert.False(viewModel.HasPendingDeviceCode);
        Assert.Empty(dialogs.ErrorMessages);
    }

    private static AccountsPageViewModel CreateViewModel(FakeSettingsService settings, FakeAccountService accounts, RecordingDialogService dialogs) =>
        new(settings, accounts, NullLogger<AccountsPageViewModel>.Instance, dialogs);

    private static async Task WaitForAsync(Func<bool> condition, string description)
    {
        for (int attempt = 0; attempt < 500; attempt++)
        {
            if (condition()) return;
            await Task.Delay(10);
        }

        Assert.Fail($"等待超时：{description}。");
    }

    private sealed class FakeSettingsService : ISettingsService
    {
        public LauncherSettings Current { get; set; } = new();

        public List<LauncherSettings> Saves { get; } = [];

        public event EventHandler<LauncherSettings>? Changed
        {
            add { }
            remove { }
        }

        public Task<LauncherSettings> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(Current);

        public Task SaveAsync(LauncherSettings settings, CancellationToken cancellationToken = default)
        {
            Saves.Add(settings);
            Current = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAccountService : IAccountService
    {
        public List<AccountProfile> Accounts { get; } = [];

        public List<string> AddedOfflineNames { get; } = [];

        public List<string> MicrosoftClientIds { get; } = [];

        public List<Guid?> SelectedIds { get; } = [];

        public List<Guid> RemovedIds { get; } = [];

        public int GetAccountsCalls { get; private set; }

        public DeviceCodeInfo? DeviceCode { get; set; }

        public AccountAuthenticationException? MicrosoftException { get; set; }

        public bool WaitForCancellationBeforeCompletion { get; set; }

        public string MicrosoftPlayerName { get; set; } = "MicrosoftPlayer";

        public TaskCompletionSource<bool> MicrosoftStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<AccountProfile>> GetAccountsAsync(CancellationToken cancellationToken = default)
        {
            GetAccountsCalls++;
            return Task.FromResult<IReadOnlyList<AccountProfile>>(Accounts.ToArray());
        }

        public Task<AccountProfile?> GetSelectedAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<AccountProfile?>(Accounts.Count > 0 ? Accounts[0] : null);

        public Task<AccountProfile?> SelectAsync(Guid? accountId, CancellationToken cancellationToken = default)
        {
            SelectedIds.Add(accountId);
            return Task.FromResult<AccountProfile?>(accountId is Guid id ? Accounts.FirstOrDefault(account => account.Id == id) : null);
        }

        public Task<AccountProfile> AddOfflineAsync(string playerName, CancellationToken cancellationToken = default)
        {
            AddedOfflineNames.Add(playerName);
            var account = new AccountProfile(
                Guid.NewGuid(),
                AccountKind.Offline,
                playerName,
                Guid.NewGuid().ToString("N"),
                AddedAt: DateTimeOffset.UtcNow);
            Accounts.Add(account);
            return Task.FromResult(account);
        }

        public async Task<AccountProfile> AddMicrosoftAsync(string clientId, IProgress<DeviceCodeInfo>? prompt = null, CancellationToken cancellationToken = default)
        {
            MicrosoftClientIds.Add(clientId);
            DeviceCodeInfo? deviceCode = DeviceCode;
            if (deviceCode is not null)
            {
                prompt?.Report(deviceCode);
            }

            MicrosoftStarted.TrySetResult(true);
            if (WaitForCancellationBeforeCompletion)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            AccountAuthenticationException? failure = MicrosoftException;
            if (failure is not null)
            {
                throw failure;
            }

            var account = new AccountProfile(
                Guid.NewGuid(),
                AccountKind.Microsoft,
                MicrosoftPlayerName,
                Guid.NewGuid().ToString("N"),
                Xuid: "123456789",
                ClientId: clientId,
                AddedAt: DateTimeOffset.UtcNow);
            Accounts.Add(account);
            return account;
        }

        public Task<AccountProfile> AddMicrosoftInteractiveAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            var account = new AccountProfile(
                Guid.NewGuid(),
                AccountKind.Microsoft,
                MicrosoftPlayerName,
                Guid.NewGuid().ToString("N"),
                Xuid: "123456789",
                ClientId: "00000000402b5328",
                AddedAt: DateTimeOffset.UtcNow);
            Accounts.Add(account);
            return Task.FromResult(account);
        }

        public Task RemoveAsync(Guid accountId, CancellationToken cancellationToken = default)
        {
            RemovedIds.Add(accountId);
            Accounts.RemoveAll(account => account.Id == accountId);
            return Task.CompletedTask;
        }

        public Task<MinecraftSessionInfo?> GetMinecraftSessionAsync(Guid accountId, CancellationToken cancellationToken = default) =>
            Task.FromResult<MinecraftSessionInfo?>(null);
    }

    private sealed class RecordingDialogService : IDialogService
    {
        public List<string> ErrorMessages { get; } = [];

        public List<string> ConfirmMessages { get; } = [];

        public bool ConfirmResult { get; set; } = true;

        public bool Confirm(string message, string title = "Astra Launcher")
        {
            ConfirmMessages.Add(message);
            return ConfirmResult;
        }

        public void ShowError(string message, string title = "Astra Launcher") => ErrorMessages.Add(message);
    }
}
