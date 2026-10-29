using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using AstraLauncher.App.Commands;
using AstraLauncher.App.Services;
using AstraLauncher.Core.Models;
using AstraLauncher.Core.Services;
using AstraLauncher.Game.Instances;
using AstraLauncher.Game.Java;
using AstraLauncher.Game.Launch;
using AstraLauncher.Game.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AstraLauncher.App.ViewModels;

public sealed class InstancesPageViewModel : PageViewModel
{
    private const int MaximumLogLines = 500;

    private readonly IGameInstanceRepository _instances;
    private readonly IJavaRuntimeDetector _java;
    private readonly ILogger<InstancesPageViewModel> _logger;
    private readonly IDialogService _dialogs;
    private readonly ISettingsService? _settings;
    private readonly IGameLaunchService? _launchService;
    private readonly AstraLauncher.Authentication.IAccountService? _accounts;
    private readonly ICrashAnalyzer? _crashAnalyzer;
    private readonly ICrashDialogService? _crashDialog;
    private readonly ICrashRepairService? _crashRepair;
    private readonly RelayCommand _stopCommand;
    private GameInstance? _selectedInstance;
    private Guid? _selectedInstanceId;
    private string _name = "新实例";
    private string _versionId = "1.21.1";
    private string _importPath = "";
    private string _statusMessage = "正在加载实例…";
    private bool _isLaunching;
    private double _launchPercent;
    private string _launchStage = string.Empty;
    private IGameProcess? _runningProcess;
    private CancellationTokenSource? _launchCancellation;
    private SynchronizationContext? _uiContext;

    public InstancesPageViewModel(
        IGameInstanceRepository instances,
        IJavaRuntimeDetector java,
        ILogger<InstancesPageViewModel> logger,
        IDialogService dialogs,
        ISettingsService? settings = null,
        IGameLaunchService? launchService = null,
        AstraLauncher.Authentication.IAccountService? accounts = null,
        ICrashAnalyzer? crashAnalyzer = null,
        ICrashDialogService? crashDialog = null,
        ICrashRepairService? crashRepair = null) : base("游戏实例")
    {
        _instances = instances;
        _java = java;
        _logger = logger;
        _dialogs = dialogs;
        _settings = settings;
        _launchService = launchService;
        _accounts = accounts;
        _crashAnalyzer = crashAnalyzer;
        _crashDialog = crashDialog;
        _crashRepair = crashRepair;
        _logger.LogInformation(
            "Crash analysis services: analyzer={Analyzer}, dialog={Dialog}, repair={Repair}.",
            crashAnalyzer is not null,
            crashDialog is not null,
            crashRepair is not null);
        RefreshCommand = new AsyncRelayCommand(() => RunAsync("刷新实例列表", () => RefreshAsync(_selectedInstanceId)));
        CreateCommand = new AsyncRelayCommand(CreateAsync);
        ImportCommand = new AsyncRelayCommand(ImportAsync);
        RenameCommand = new AsyncRelayCommand(RenameAsync);
        DuplicateCommand = new AsyncRelayCommand(DuplicateAsync);
        DeleteCommand = new AsyncRelayCommand(DeleteAsync);
        DetectJavaCommand = new AsyncRelayCommand(DetectJavaAsync);
        LaunchCommand = new AsyncRelayCommand(() => LaunchAsync());
        _stopCommand = new RelayCommand(_ => StopGame(), _ => IsLaunching);
        StopCommand = _stopCommand;
    }

    public ObservableCollection<GameInstance> Instances { get; } = [];

    public ObservableCollection<JavaRuntimeInfo> JavaRuntimes { get; } = [];

    /// <summary>Version ids offered by the version picker; the field stays editable for offline/unknown versions.</summary>
    public ObservableCollection<string> AvailableVersions { get; } = [];

    /// <summary>Lines written by the running game process (stdout/stderr), capped to 500 entries.</summary>
    public ObservableCollection<string> GameLogs { get; } = [];

    public GameInstance? SelectedInstance
    {
        get => _selectedInstance;
        set
        {
            if (SetProperty(ref _selectedInstance, value) && value is not null) _selectedInstanceId = value.Id;
        }
    }

    /// <summary>
    /// Resolves the instance to launch even when the ListBox has pushed a null selection after page navigation.
    /// </summary>
    public GameInstance? ResolveSelectedInstanceForLaunch() =>
        SelectedInstance
        ?? (_selectedInstanceId is Guid id ? Instances.FirstOrDefault(instance => instance.Id == id) : null)
        ?? Instances.FirstOrDefault();

    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string VersionId { get => _versionId; set => SetProperty(ref _versionId, value); }
    public string ImportPath { get => _importPath; set => SetProperty(ref _importPath, value); }
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public string LaunchStage { get => _launchStage; private set => SetProperty(ref _launchStage, value); }

    public bool IsLaunching
    {
        get => _isLaunching;
        private set
        {
            if (SetProperty(ref _isLaunching, value)) _stopCommand.RaiseCanExecuteChanged();
        }
    }

    public double LaunchPercent { get => _launchPercent; private set => SetProperty(ref _launchPercent, value); }

    public ICommand RefreshCommand { get; }
    public ICommand CreateCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand RenameCommand { get; }
    public ICommand DuplicateCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand DetectJavaCommand { get; }
    public ICommand LaunchCommand { get; }
    public ICommand StopCommand { get; }

    public Task InitializeAsync() => RunAsync("加载实例列表", () => RefreshAsync(_selectedInstanceId));

    /// <summary>Fills the version picker from the version manifest; the value is still free text when offline.</summary>
    public void SetAvailableVersions(IEnumerable<string> versions)
    {
        ArgumentNullException.ThrowIfNull(versions);
        AvailableVersions.Clear();
        foreach (string version in versions)
        {
            if (!string.IsNullOrWhiteSpace(version) && !AvailableVersions.Contains(version)) AvailableVersions.Add(version);
        }
    }

    /// <summary>Adds a locally installed version (for example a freshly installed loader) to the version picker.</summary>
    public void AddAvailableVersion(string versionId)
    {
        if (string.IsNullOrWhiteSpace(versionId) || AvailableVersions.Contains(versionId)) return;
        AvailableVersions.Add(versionId);
    }

    private async Task RefreshAsync(Guid? keepId)
    {
        IReadOnlyList<GameInstance> values = await _instances.ListAsync();
        Instances.Clear();
        foreach (GameInstance value in values) Instances.Add(value);
        ForceSelect(keepId is Guid instanceId ? Instances.FirstOrDefault(value => value.Id == instanceId) : null);
        StatusMessage = $"已加载 {Instances.Count} 个实例。";
    }

    /// <summary>
    /// Re-applies the selection after the collection was rebuilt. <see cref="GameInstance"/> is a record, so
    /// value equality would otherwise suppress the change notification and leave WPF bound to a stale object
    /// that no longer exists in the list (which clears the ListBox selection).
    /// </summary>
    private void ForceSelect(GameInstance? instance)
    {
        _selectedInstance = null;
        _selectedInstanceId = null;
        SelectedInstance = instance;
    }

    private async Task CreateAsync() => await RunAsync("创建实例", async () =>
    {
        GameInstance created = await _instances.CreateAsync(Name, VersionId);
        await RefreshAsync(created.Id);
        StatusMessage = $"已创建实例：{created.Name}";
    });

    private async Task ImportAsync() => await RunAsync("导入实例", async () =>
    {
        string importPath = ImportPath?.Trim() ?? string.Empty;
        if (importPath.Length == 0)
        {
            StatusMessage = "请先填写「导入路径」（已有游戏目录），再点击「导入目录」。";
            return;
        }

        GameInstance imported = await _instances.ImportAsync(Name, VersionId, importPath);
        await RefreshAsync(imported.Id);
        StatusMessage = $"已导入实例：{imported.Name}";
    });

    private async Task RenameAsync() => await RunAsync("重命名实例", async () =>
    {
        GameInstance selected = SelectedInstance ?? throw new InvalidOperationException("请先选择实例。");
        await _instances.RenameAsync(selected.Id, Name);
        await RefreshAsync(selected.Id);
        StatusMessage = "实例已重命名。";
    });

    private async Task DuplicateAsync() => await RunAsync("复制实例", async () =>
    {
        GameInstance selected = SelectedInstance ?? throw new InvalidOperationException("请先选择实例。");
        GameInstance duplicated = await _instances.DuplicateAsync(selected.Id, Name);
        await RefreshAsync(duplicated.Id);
        StatusMessage = "实例已复制。";
    });

    private async Task DeleteAsync() => await RunAsync("删除实例", async () =>
    {
        GameInstance selected = SelectedInstance ?? throw new InvalidOperationException("请先选择实例。");
        bool managed = selected.IsManagedDirectory;
        string message = managed
            ? $"将删除实例「{selected.Name}」及其目录（包含其中的存档/配置），此操作不可恢复。确认删除？"
            : $"将仅删除实例「{selected.Name}」的登记，不会修改原目录。确认删除？";
        if (!_dialogs.Confirm(message)) return;
        await _instances.DeleteAsync(selected.Id, managed);
        await RefreshAsync(null);
        StatusMessage = managed ? "实例及其受管理目录已删除。" : "实例登记已删除；导入目录未被修改。";
    });

    private async Task DetectJavaAsync() => await RunAsync("检测 Java 运行时", async () =>
    {
        IReadOnlyList<JavaRuntimeInfo> values = await _java.DetectAsync();
        JavaRuntimes.Clear();
        foreach (JavaRuntimeInfo value in values) JavaRuntimes.Add(value);
        StatusMessage = values.Count == 0 ? "未检测到 Java。" : $"检测到 {values.Count} 个 Java 运行时。";
    });

    /// <summary>Starts the selected instance and connects directly to a saved server when the game supports --server/--port.</summary>
    public Task StartWithServerAsync(SavedServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return LaunchAsync(server);
    }

    private async Task LaunchAsync(SavedServer? joinServer = null)
    {
        if (IsLaunching) return;
        GameInstance? selected = ResolveSelectedInstanceForLaunch();
        if (selected is null)
        {
            StatusMessage = "请先选择要启动的实例。";
            return;
        }

        SelectedInstance = selected;

        if (_launchService is null || _settings is null)
        {
            StatusMessage = "启动服务不可用（缺少设置或启动服务）。";
            return;
        }

        _uiContext = SynchronizationContext.Current;
        using var cancellation = new CancellationTokenSource();
        _launchCancellation = cancellation;
        IsLaunching = true;
        LaunchPercent = 0;
        LaunchStage = "准备启动";
        GameLogs.Clear();
        AppendLog($"[launcher] 启动实例：{selected.Name}（版本 {selected.VersionId}）");
        if (joinServer is not null)
        {
            AppendLog($"[launcher] 加入服务器：{joinServer.DisplayAddress}");
        }
        try
        {
            LauncherSettings settings = await _settings.GetAsync(cancellation.Token);
            GameSession session = await BuildSessionAsync(settings, cancellation.Token);
            var options = new GameLaunchOptions(
                settings.MemoryMiB,
                settings.JavaExecutablePath,
                settings.GameArguments,
                settings.JvmArguments,
                ServerAddress: joinServer?.Address,
                ServerPort: joinServer?.Port);
            var progress = new Progress<LaunchProgress>(OnLaunchProgress);
            AppendLog(session.Kind == GameSessionKind.Authenticated
                ? $"[launcher] 官方账户：{session.PlayerName}（{session.PlayerUuid}）"
                : $"[launcher] 离线玩家：{session.PlayerName}（{session.PlayerUuid}）");
            IGameProcess process = await _launchService.LaunchAsync(selected, session, options, progress, cancellation.Token);
            _runningProcess = process;
            process.OutputReceived += OnGameOutput;
            StatusMessage = $"游戏已启动（PID {process.Id}），正在等待退出…";
            AppendLog($"[launcher] 游戏进程已启动，PID {process.Id}");
            GameExitInfo exitInfo = await process.Exited;
            AppendLog($"[launcher] 游戏进程已退出，退出码 {exitInfo.ExitCode}");
            StatusMessage = $"游戏已退出（退出码 {exitInfo.ExitCode}）。";
            if (_crashAnalyzer is not null)
            {
                await AnalyzeAndOfferRepairAsync(selected, exitInfo.ExitCode, cancellation.Token);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            StatusMessage = "已取消启动。";
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "启动实例失败：{Instance}", selected.Name);
            StatusMessage = FriendlyError(exception);
            _dialogs.ShowError(StatusMessage);
        }
        finally
        {
            if (_runningProcess is not null) _runningProcess.OutputReceived -= OnGameOutput;
            _runningProcess = null;
            _launchCancellation = null;
            IsLaunching = false;
            LaunchStage = string.Empty;
        }
    }

    /// <summary>
    /// After a non-zero exit code, analyzes latest.log/crash-reports, shows the PCL-style explanation dialog and applies
    /// reversible repairs only when the user explicitly chooses automatic repair.
    /// </summary>
    private async Task AnalyzeAndOfferRepairAsync(GameInstance instance, int exitCode, CancellationToken cancellationToken)
    {
        if (_crashAnalyzer is null || _crashDialog is null)
        {
            return;
        }

        StatusMessage = "正在检查游戏退出原因…";
        _logger.LogInformation("Starting crash analysis for {Instance} (exit code {ExitCode}).", instance.Name, exitCode);
        try
        {
            CrashAnalysisResult? analysis = await _crashAnalyzer.AnalyzeAsync(instance, exitCode, cancellationToken);
            if (analysis is null)
            {
                StatusMessage = $"游戏已退出（退出码 {exitCode}）。";
                return;
            }

            _logger.LogInformation(
                "Crash analysis for {Instance}: {Summary}; issues={IssueCount}.",
                instance.Name,
                analysis.Summary,
                analysis.Issues.Count);
            AppendLog($"[launcher] 崩溃分析：{analysis.Summary}");
            foreach (CrashIssue issue in analysis.Issues)
            {
                AppendLog($"[launcher] 崩溃原因：{issue.Title} — {issue.Details}");
            }

            StatusMessage = $"崩溃分析：{analysis.Summary}";
            CrashRepairChoice choice = _crashDialog.Show(analysis);
            _logger.LogInformation("Crash dialog choice for {Instance}: {Choice}.", instance.Name, choice);
            if (choice != CrashRepairChoice.AutoRepair)
            {
                if (choice == CrashRepairChoice.Manual)
                {
                    StatusMessage = "已保留崩溃现场，请按分析建议手动处理。";
                    AppendLog("[launcher] 用户选择手动处理崩溃。");
                }

                return;
            }

            if (_crashRepair is null)
            {
                StatusMessage = "自动修复服务不可用，请按建议手动处理。";
                return;
            }

            StatusMessage = "正在应用自动修复…";
            AppendLog("[launcher] 用户选择自动修复，正在应用可逆修复…");
            var repairProgress = new Progress<string>(message => AppendLog($"[launcher] {message}"));
            CrashRepairResult repair = await _crashRepair.RepairAsync(instance, analysis, repairProgress, cancellationToken);
            foreach (CrashRepairAction action in repair.Actions)
            {
                AppendLog($"[launcher] 修复：{action.Description} — {(action.Success ? "成功" : "失败")}{(action.Detail is null ? string.Empty : "：" + action.Detail)}");
            }

            StatusMessage = repair.Summary;
            AppendLog($"[launcher] {repair.Summary}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The user closed the launcher while the dialog/repair was running.
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "崩溃分析/自动修复失败：{Instance}", instance.Name);
            StatusMessage = "崩溃分析失败：" + exception.Message;
        }
    }

    private async Task<GameSession> BuildSessionAsync(LauncherSettings settings, CancellationToken cancellationToken)
    {
        if (_accounts is not null)
        {
            try
            {
                AstraLauncher.Authentication.AccountProfile? selected = await _accounts.GetSelectedAsync(cancellationToken);
                if (selected is { IsMicrosoft: true })
                {
                    AstraLauncher.Authentication.MinecraftSessionInfo info = await _accounts.GetMinecraftSessionAsync(selected.Id, cancellationToken)
                        ?? throw new InvalidOperationException("Microsoft 账户会话不可用，请在账户页重新登录。");
                    return new GameSession(GameSessionKind.Authenticated, info.PlayerName, info.PlayerUuid, info.AccessToken, "msa", info.Xuid, info.ClientId);
                }

                if (selected is { Kind: AstraLauncher.Authentication.AccountKind.Offline })
                {
                    return OfflineProfile.Create(selected.PlayerName);
                }
            }
            catch (AstraLauncher.Authentication.AccountAuthenticationException exception)
            {
                throw new InvalidOperationException($"Microsoft 登录失败：{exception.Message} 请在账户页重新登录。", exception);
            }
        }

        return OfflineProfile.Create(settings.OfflinePlayerName ?? "Player");
    }

    private void OnLaunchProgress(LaunchProgress progress)
    {
        LaunchStage = string.IsNullOrWhiteSpace(progress.Message) ? progress.Stage : $"{progress.Stage}：{progress.Message}";
        StatusMessage = LaunchStage;
        if (progress.Percent is double percent) LaunchPercent = Math.Clamp(percent, 0, 100);
    }

    private void OnGameOutput(object? sender, string line)
    {
        SynchronizationContext? context = _uiContext;
        if (context is null)
        {
            return;
        }

        context.Post(_ => AppendLog(line), null);
    }

    private void AppendLog(string line)
    {
        // Newest first so the visible top of the list always shows the latest game output without scrolling.
        GameLogs.Insert(0, line);
        while (GameLogs.Count > MaximumLogLines) GameLogs.RemoveAt(GameLogs.Count - 1);
    }

    private void StopGame()
    {
        if (_runningProcess is null) return;
        StatusMessage = "正在结束游戏进程…";
        AppendLog("[launcher] 正在结束游戏进程…");
        try
        {
            _runningProcess.Kill();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "结束游戏进程失败。");
        }
    }

    private async Task RunAsync(string operation, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "游戏实例操作失败：{Operation}", operation);
            StatusMessage = FriendlyError(exception);
        }
    }

    private static string FriendlyError(Exception exception) => exception switch
    {
        ArgumentException argumentException => $"输入无效：{argumentException.Message}",
        InvalidOperationException invalidOperationException => invalidOperationException.Message,
        DirectoryNotFoundException directoryNotFoundException => $"目录不存在：{directoryNotFoundException.Message}",
        UnauthorizedAccessException => "没有访问权限，请检查目录权限。",
        IOException ioException => $"文件操作失败：{ioException.Message}",
        KeyNotFoundException => "实例不存在，可能已被删除，请刷新后重试。",
        _ => $"操作失败：{exception.Message}"
    };
}
