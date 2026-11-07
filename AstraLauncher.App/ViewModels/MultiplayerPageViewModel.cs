using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Input;
using AstraLauncher.App.Commands;
using AstraLauncher.App.Services;
using AstraLauncher.Core.Models;
using AstraLauncher.Core.Services;
using AstraLauncher.Multiplayer;
using Microsoft.Extensions.Logging;

namespace AstraLauncher.App.ViewModels;

/// <summary>
/// EasyTier room page: install/locate the binaries, create or join a room, inspect peers and launch
/// the selected game instance straight into the room. The shell owns the actual game launch.
/// </summary>
public sealed class MultiplayerPageViewModel : PageViewModel
{
    private readonly IEasyTierService _service;
    private readonly ISettingsService _settings;
    private readonly ILogger<MultiplayerPageViewModel> _logger;
    private readonly IDialogService? _dialogs;

    private string _roomName = LauncherSettings.DefaultEasyTierRoomName;
    private string _roomSecret = GenerateSecret();
    private string _hostVirtualIp = LauncherSettings.DefaultEasyTierHostVirtualIp;
    private string _gamePortText = LauncherSettings.DefaultEasyTierGamePort.ToString(CultureInfo.InvariantCulture);
    private string _localPortText = LauncherSettings.DefaultEasyTierLocalPort.ToString(CultureInfo.InvariantCulture);
    private string _sharedNode = LauncherSettings.DefaultEasyTierSharedNode;
    private string _easyTierDirectory = string.Empty;
    private string _inviteCode = string.Empty;
    private string _statusMessage = "准备就绪。首次使用请先点击「安装 / 检测 EasyTier」。";
    private string _connectionSummary = "未连接";
    private string _entryAddress = "—";
    private string _binaryStatus = "正在检测 EasyTier …";
    private bool _useFullTun;
    private bool _autoJoin = true;
    private bool _isBusy;
    private bool _isConnected;

    public MultiplayerPageViewModel(
        IEasyTierService service,
        ISettingsService settings,
        ILogger<MultiplayerPageViewModel> logger,
        IDialogService? dialogs = null) : base("联机")
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        _service = service;
        _settings = settings;
        _logger = logger;
        _dialogs = dialogs;

        InstallCommand = new AsyncRelayCommand(InstallAsync);
        CreateRoomCommand = new AsyncRelayCommand(CreateRoomAsync);
        JoinRoomCommand = new AsyncRelayCommand(JoinRoomAsync);
        StopCommand = new AsyncRelayCommand(StopAsync);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        CopyInviteCommand = new RelayCommand(_ => CopyInvite());
        LaunchGameCommand = new RelayCommand(_ => LaunchGame());

        _service.StatusChanged += OnStatusChanged;
        _ = InitializeAsync();
    }

    /// <summary>Raised when the user wants to launch the selected instance into the connected room.</summary>
    public event Action<SavedServer>? JoinRequested;

    public ObservableCollection<PeerInfo> Peers { get; } = [];

    public ICommand InstallCommand { get; }
    public ICommand CreateRoomCommand { get; }
    public ICommand JoinRoomCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand CopyInviteCommand { get; }
    public ICommand LaunchGameCommand { get; }

    public string RoomName { get => _roomName; set => SetProperty(ref _roomName, value); }
    public string RoomSecret { get => _roomSecret; set => SetProperty(ref _roomSecret, value); }
    public string HostVirtualIp { get => _hostVirtualIp; set => SetProperty(ref _hostVirtualIp, value); }
    public string GamePortText { get => _gamePortText; set => SetProperty(ref _gamePortText, value); }
    public string LocalPortText { get => _localPortText; set => SetProperty(ref _localPortText, value); }
    public string SharedNode { get => _sharedNode; set => SetProperty(ref _sharedNode, value); }
    public string EasyTierDirectory { get => _easyTierDirectory; set => SetProperty(ref _easyTierDirectory, value); }
    public string InviteCode { get => _inviteCode; set => SetProperty(ref _inviteCode, value); }
    public string StatusMessage { get => _statusMessage; set => SetProperty(ref _statusMessage, value); }
    public string ConnectionSummary { get => _connectionSummary; set => SetProperty(ref _connectionSummary, value); }
    public string EntryAddress { get => _entryAddress; set => SetProperty(ref _entryAddress, value); }
    public string BinaryStatus { get => _binaryStatus; set => SetProperty(ref _binaryStatus, value); }
    public bool IsBusy { get => _isBusy; set => SetProperty(ref _isBusy, value); }
    public bool IsConnected { get => _isConnected; set => SetProperty(ref _isConnected, value); }

    public bool UseFullTun
    {
        get => _useFullTun;
        set => SetProperty(ref _useFullTun, value);
    }

    public bool AutoJoin
    {
        get => _autoJoin;
        set
        {
            if (SetProperty(ref _autoJoin, value))
            {
                _service.AutoJoinEnabled = value;
            }
        }
    }

    /// <summary>Lets the shell report a missing selected instance back onto this page.</summary>
    public void ReportStatus(string message) => StatusMessage = message;

    private async Task InitializeAsync()
    {
        try
        {
            LauncherSettings settings = await _settings.GetAsync().ConfigureAwait(true);
            EasyTierDirectory = settings.EasyTierDirectory ?? string.Empty;
            SharedNode = settings.EasyTierSharedNode;
            HostVirtualIp = settings.EasyTierHostVirtualIp;
            GamePortText = settings.EasyTierGamePort.ToString(CultureInfo.InvariantCulture);
            LocalPortText = settings.EasyTierLocalPort.ToString(CultureInfo.InvariantCulture);
            UseFullTun = false; // Full TUN requires an elevated child process; disabled for the public beta until lifecycle cleanup is hardened.
            AutoJoin = settings.EasyTierAutoJoin;
            RoomName = settings.EasyTierLastRoomName;
            RefreshBinaryStatus();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to load multiplayer settings.");
            BinaryStatus = "无法读取设置，仍可手动填写。";
        }
    }

    private async Task InstallAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            StatusMessage = "正在准备 EasyTier …";
            var progress = new Progress<string>(message => StatusMessage = message);
            EasyTierBinaries binaries = await _service
                .EnsureInstalledAsync(ConfiguredDirectoryOrNull(), progress)
                .ConfigureAwait(true);
            BinaryStatus = $"已就绪：{binaries.RootDirectory}";
            StatusMessage = "EasyTier 已就绪。现在可以创建或加入房间了。";
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task CreateRoomAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            if (string.IsNullOrWhiteSpace(RoomSecret)) RoomSecret = GenerateSecret();
            if (string.IsNullOrWhiteSpace(RoomName)) RoomName = "astra-" + GenerateSecret()[..6].ToLowerInvariant();
            await PersistAsync().ConfigureAwait(true);

            EasyTierOptions options = BuildOptions(RoomRole.Host, LauncherSettings.EasyTierHostRpcPort);
            var progress = new Progress<string>(message => StatusMessage = message);
            await _service.StartAsync(options, ConfiguredDirectoryOrNull(), progress).ConfigureAwait(true);

            InviteCode = RoomInviteCodec.Encode(new RoomInvite(
                RoomName.Trim(), RoomSecret.Trim(), HostVirtualIp.Trim(),
                options.GamePort, SharedNode.Trim(), "Astra 联机房间"));
            StatusMessage = "房间已创建。复制邀请码发给朋友，对方在「加入房间」里粘贴即可。房主请在本机启动 Minecraft 服务端（默认 25565）。";
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task JoinRoomAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            if (RoomInviteCodec.TryDecode(InviteCode, out RoomInvite? invite) && invite is not null)
            {
                RoomName = invite.NetworkName;
                RoomSecret = invite.NetworkSecret;
                HostVirtualIp = invite.HostVirtualIp;
                GamePortText = invite.GamePort.ToString(CultureInfo.InvariantCulture);
                SharedNode = invite.SharedNode;
            }

            if (string.IsNullOrWhiteSpace(RoomSecret))
            {
                StatusMessage = "请粘贴邀请码，或手动填写房间名称与密钥。";
                return;
            }

            await PersistAsync().ConfigureAwait(true);
            EasyTierOptions options = BuildOptions(RoomRole.Guest, LauncherSettings.EasyTierGuestRpcPort);
            var progress = new Progress<string>(message => StatusMessage = message);
            await _service.StartAsync(options, ConfiguredDirectoryOrNull(), progress).ConfigureAwait(true);
            StatusMessage = $"已加入房间。点「启动游戏并加入」即可；或在多人游戏里手动添加 {EntryAddress}。";
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task StopAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            await _service.StopAsync().ConfigureAwait(true);
            StatusMessage = "已断开联机，easytier-core 已停止。";
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshAsync()
    {
        try
        {
            await _service.RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to refresh EasyTier peers.");
        }
    }

    private void CopyInvite()
    {
        if (string.IsNullOrWhiteSpace(InviteCode))
        {
            StatusMessage = "还没有邀请码，请先创建房间。";
            return;
        }

        try
        {
            Clipboard.SetText(InviteCode);
            StatusMessage = "邀请码已复制到剪贴板。";
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Clipboard copy failed.");
            StatusMessage = "复制失败，请手动选中邀请码复制。";
        }
    }

    private void LaunchGame()
    {
        if (!_service.IsRunning)
        {
            StatusMessage = "请先创建或加入房间，再启动游戏。";
            return;
        }

        if (!EasyTierLaunchHook.TrySplitAddress(_service.Status.EntryAddress, out string host, out int port))
        {
            StatusMessage = "无法解析联机入口地址，请重新连接。";
            return;
        }

        JoinRequested?.Invoke(new SavedServer(Guid.NewGuid(), "EasyTier 联机", host, port));
    }

    private async Task PersistAsync()
    {
        LauncherSettings settings = await _settings.GetAsync().ConfigureAwait(true);
        settings.EasyTierDirectory = ConfiguredDirectoryOrNull();
        settings.EasyTierSharedNode = SharedNode.Trim();
        settings.EasyTierHostVirtualIp = HostVirtualIp.Trim();
        if (int.TryParse(GamePortText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int gamePort))
        {
            settings.EasyTierGamePort = gamePort;
        }

        if (int.TryParse(LocalPortText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int localPort))
        {
            settings.EasyTierLocalPort = localPort;
        }

        settings.EasyTierUseFullTun = false;
        settings.EasyTierAutoJoin = AutoJoin;
        settings.EasyTierLastRoomName = RoomName.Trim();
        settings.Normalize();
        await _settings.SaveAsync(settings).ConfigureAwait(true);
    }

    private EasyTierOptions BuildOptions(RoomRole role, int rpcPort)
    {
        if (!int.TryParse(GamePortText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int gamePort))
        {
            throw new EasyTierException("游戏端口必须是 1-65535 的数字。");
        }

        if (!int.TryParse(LocalPortText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int localPort))
        {
            throw new EasyTierException("本地转发端口必须是 1-65535 的数字。");
        }

        return new EasyTierOptions(
            RoomName.Trim(),
            RoomSecret.Trim(),
            role,
            EasyTierMode.NoTun,
            SharedNode.Trim(),
            HostVirtualIp.Trim(),
            gamePort,
            localPort,
            rpcPort,
            Environment.MachineName);
    }

    private void RefreshBinaryStatus()
    {
        EasyTierBinaries? binaries = _service.TryResolveBinaries(ConfiguredDirectoryOrNull());
        BinaryStatus = binaries is null
            ? "未检测到 EasyTier。点「安装 / 检测 EasyTier」会自动下载官方 2.6.4。"
            : $"已就绪：{binaries.RootDirectory}";
    }

    private void OnStatusChanged(EasyTierStatus status)
    {
        try
        {
            if (Application.Current?.Dispatcher is { } dispatcher)
            {
                _ = dispatcher.InvokeAsync(() => ApplyStatus(status));
            }
            else
            {
                ApplyStatus(status);
            }
        }
        catch (Exception exception)
        {
            // The dispatcher may already be shutting down when the service is disposed on exit.
            _logger.LogDebug(exception, "Cannot deliver EasyTier status to the UI.");
        }
    }

    private void ApplyStatus(EasyTierStatus status)
    {
        IsConnected = status.IsRunning;
        EntryAddress = status.EntryAddress ?? "—";
        ConnectionSummary = status.Message;
        Peers.Clear();
        foreach (PeerInfo peer in status.Peers)
        {
            Peers.Add(peer);
        }
    }

    private void Fail(Exception exception)
    {
        _logger.LogError(exception, "Multiplayer operation failed.");
        StatusMessage = exception is OperationCanceledException
            ? "操作已取消。"
            : $"操作失败：{exception.Message}";
        _dialogs?.ShowError(StatusMessage);
    }

    private string? ConfiguredDirectoryOrNull() =>
        string.IsNullOrWhiteSpace(EasyTierDirectory) ? null : EasyTierDirectory.Trim();

    private static string GenerateSecret() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
}
