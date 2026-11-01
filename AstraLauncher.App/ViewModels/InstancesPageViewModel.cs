using System.IO;
using System.Collections.ObjectModel;
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
    private readonly IVersionInstallService? _installService;
    private readonly IExternalInstanceScanner? _externalScanner;
    private readonly RelayCommand _stopCommand;
    private GameInstance? _launchingInstance;
    private DateTimeOffset? _launchStartedAt;
    private bool _crashDialogShown;
    private bool _crashDetectionQueued;
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
        ICrashRepairService? crashRepair = null,
        IVersionInstallService? installService = null,
        IExternalInstanceScanner? externalScanner = null) : base("游戏实例")
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
        _installService = installService;
        _externalScanner = externalScanner;
        _logger.LogInformation(
            "Crash analysis services: analyzer={Analyzer}, dialog={Dialog}, repair={Repair}.",
            crashAnalyzer is not null,
            crashDialog is not null,
            crashRepair is not null);
        RefreshCommand = new AsyncRelayCommand(() => RunAsync("刷新实例列表", () => RefreshAsync(_selectedInstanceId)));
        CreateCommand = new AsyncRelayCommand(CreateAsync);
        ImportCommand = new AsyncRelayCommand(ImportAsync);
        ScanExternalCommand = new AsyncRelayCommand(ScanExternalAsync);
        ImportSavesCommand = new AsyncRelayCommand(ImportSavesAsync);
        RenameCommand = new AsyncRelayCommand(RenameAsync);
        DuplicateCommand = new AsyncRelayCommand(DuplicateAsync);
        DeleteCommand = new AsyncRelayCommand(DeleteAsync);
        DetectJavaCommand = new AsyncRelayCommand(DetectJavaAsync);
        RepairFilesCommand = new AsyncRelayCommand(RepairFilesAsync);
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

    public ICommand ScanExternalCommand { get; }

    public ICommand ImportSavesCommand { get; }
    public ICommand RenameCommand { get; }
    public ICommand DuplicateCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand DetectJavaCommand { get; }

    public ICommand RepairFilesCommand { get; }
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

    private async Task ScanExternalAsync() => await RunAsync("扫描其他启动器", async () =>
    {
        if (_externalScanner is null)
        {
            StatusMessage = "外部实例扫描服务不可用。";
            return;
        }

        ExternalScanResult scan = _externalScanner.Scan();
        IReadOnlyList<GameInstance> existing = await _instances.ListAsync();
        var existingPaths = existing
            .Select(instance => Path.GetFullPath(instance.DirectoryPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<ExternalInstanceCandidate> candidates = scan.Candidates
            .Where(candidate => !existingPaths.Contains(Path.GetFullPath(candidate.DirectoryPath)))
            .ToList();
        if (candidates.Count == 0)
        {
            string savesHint = scan.GlobalSaveCount > 0
                ? $"\n\n另外发现全局存档 {scan.GlobalSaveCount} 个，可用「导入全局存档」复制到选中实例。"
                : string.Empty;
            StatusMessage = "未发现新的外部版本。";
            _dialogs.ShowInfo("没有发现其他启动器的新版本目录。" + savesHint, "扫描其他启动器");
            return;
        }

        string preview = string.Join(
            "\n",
            candidates.Take(12).Select(candidate => $"• {candidate.Name}（MOD {candidate.ModCount}，存档 {candidate.SaveCount}）"));
        if (candidates.Count > 12)
        {
            preview += $"\n… 其余 {candidates.Count - 12} 个";
        }

        bool accepted = _dialogs.Confirm(
            $"发现 {candidates.Count} 个其他启动器版本：\n\n{preview}\n\n"
            + "是否全部导入为实例？导入只登记目录，不移动任何文件。",
            "扫描其他启动器");
        if (!accepted)
        {
            return;
        }

        int imported = 0;
        foreach (ExternalInstanceCandidate candidate in candidates)
        {
            try
            {
                await _instances.ImportAsync(candidate.Name, candidate.VersionId, candidate.DirectoryPath);
                imported++;
                AppendLog($"[launcher] 已导入外部版本：{candidate.Name} → {candidate.DirectoryPath}");
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                _logger.LogWarning(exception, "导入外部版本失败：{Name}", candidate.Name);
            }
        }

        await RefreshAsync(_selectedInstanceId);
        StatusMessage = $"已导入 {imported} 个外部版本实例。";
    });

    private async Task ImportSavesAsync() => await RunAsync("导入存档", async () =>
    {
        GameInstance? instance = ResolveSelectedInstanceForLaunch();
        if (instance is null)
        {
            StatusMessage = "请先选择目标实例。";
            return;
        }

        ExternalScanResult scan = _externalScanner?.Scan() ?? new ExternalScanResult([], null, 0);
        if (scan.GlobalSavesDirectory is null || scan.GlobalSaveCount == 0)
        {
            StatusMessage = "没有找到其他启动器的全局存档。";
            _dialogs.ShowInfo("没有在 .minecraft\\saves 找到可导入的存档。", "导入存档");
            return;
        }

        bool accepted = _dialogs.Confirm(
            $"发现 {scan.GlobalSaveCount} 个全局存档：\n{scan.GlobalSavesDirectory}\n\n"
            + $"是否复制到实例「{instance.Name}」的 saves 目录？不会删除原存档。",
            "导入全局存档");
        if (!accepted)
        {
            return;
        }

        string targetDirectory = Path.Combine(instance.DirectoryPath, "saves");
        Directory.CreateDirectory(targetDirectory);
        int copied = 0;
        foreach (string source in Directory.EnumerateDirectories(scan.GlobalSavesDirectory))
        {
            string name = Path.GetFileName(source);
            string destination = Path.Combine(targetDirectory, name);
            if (Directory.Exists(destination))
            {
                destination += "-imported";
            }

            try
            {
                CopyDirectory(source, destination);
                copied++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(exception, "复制存档失败：{Name}", name);
            }
        }

        StatusMessage = $"已导入 {copied} 个存档到「{instance.Name}」。";
        _dialogs.ShowInfo($"已复制 {copied} 个存档到实例「{instance.Name}」的 saves 目录。", "导入存档");
    });

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
        }

        foreach (string directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

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

    /// <summary>
    /// Re-runs the install plan for the selected instance: every missing/corrupt game file (client jar, libraries,
    /// assets, logging config) is verified and downloaded automatically. This is the manual counterpart of the
    /// pre-launch check and the crash-repair path.
    /// </summary>
    private async Task RepairFilesAsync()
    {
        GameInstance? instance = ResolveSelectedInstanceForLaunch();
        if (instance is null)
        {
            StatusMessage = "请先选择要补全的实例。";
            return;
        }

        if (_installService is null)
        {
            StatusMessage = "文件补全服务不可用。";
            return;
        }

        StatusMessage = $"正在校验并补全「{instance.Name}」的游戏文件…";
        AppendLog($"[launcher] 开始校验并补全版本 {instance.VersionId} 的游戏文件");
        try
        {
            var progress = new Progress<VersionInstallProgress>(value =>
            {
                StatusMessage = string.IsNullOrWhiteSpace(value.CurrentFile)
                    ? value.Stage
                    : $"{value.Stage}：{value.CurrentFile}";
            });
            await _installService.InstallAsync(instance.VersionId, progress, CancellationToken.None);
            StatusMessage = $"「{instance.Name}」的游戏文件已校验并补全。";
            AppendLog("[launcher] 游戏文件校验/补全完成");
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            _logger.LogError(exception, "补全游戏文件失败：{Instance}", instance.Name);
            StatusMessage = "补全游戏文件失败：" + exception.Message;
            AppendLog("[launcher] 补全游戏文件失败：" + exception.Message);
        }
    }

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
        _launchingInstance = selected;
        _launchStartedAt = DateTimeOffset.Now;
        _crashDialogShown = false;
        _crashDetectionQueued = false;
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
            using var crashPollCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
            _ = PollCrashLogAsync(selected, crashPollCancellation.Token);
            StatusMessage = $"游戏已启动（PID {process.Id}），正在等待退出…";
            AppendLog($"[launcher] 游戏进程已启动，PID {process.Id}");
            GameExitInfo exitInfo = await process.Exited;
            crashPollCancellation.Cancel();
            AppendLog($"[launcher] 游戏进程已退出，退出码 {exitInfo.ExitCode}");
            StatusMessage = $"游戏已退出（退出码 {exitInfo.ExitCode}）。";
            if (_crashAnalyzer is not null && !_crashDialogShown)
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
            CrashAnalysisResult? analysis = await _crashAnalyzer.AnalyzeAsync(instance, exitCode, _launchStartedAt, cancellationToken);
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

    /// <summary>
    /// Polls the instance's latest.log while the game runs, because the Forge/Minecraft crash screen can stay open for a
    /// long time and its console output is not always forwarded to the launcher. Detection is delegated to the UI thread.
    /// </summary>
    private async Task PollCrashLogAsync(GameInstance instance, CancellationToken cancellationToken)
    {
        string logPath = Path.Combine(instance.DirectoryPath, "logs", "latest.log");
        try
        {
            while (!cancellationToken.IsCancellationRequested && !_crashDialogShown)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                if (_crashDialogShown || _crashDetectionQueued)
                {
                    continue;
                }

                if (!File.Exists(logPath))
                {
                    continue;
                }

                string tail;
                try
                {
                    using FileStream stream = new(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 8192, FileOptions.SequentialScan);
                    long start = Math.Max(0, stream.Length - 16384);
                    stream.Seek(start, SeekOrigin.Begin);
                    using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                    tail = reader.ReadToEnd();
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                if (!IsCrashMarker(tail))
                {
                    continue;
                }

                _crashDetectionQueued = true;
                _uiContext?.Post(_ => _ = AnalyzeLiveCrashAsync(), null);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal end of the launch (game exited or launcher closed).
        }
    }

    private void OnGameOutput(object? sender, string line)
    {
        SynchronizationContext? context = _uiContext;
        if (context is null)
        {
            return;
        }

        context.Post(_ => AppendLog(line), null);
        if (!_crashDialogShown && !_crashDetectionQueued && IsCrashMarker(line))
        {
            _crashDetectionQueued = true;
            context.Post(_ => _ = AnalyzeLiveCrashAsync(), null);
        }
    }

    /// <summary>
    /// Forge/Minecraft keeps its own crash screen open, so users may not close the game window immediately. As soon as the
    /// game output contains a crash marker, analyze in the background and show the same PCL-style dialog above the game.
    /// </summary>
    private async Task AnalyzeLiveCrashAsync()
    {
        GameInstance? instance = _launchingInstance;
        if (instance is null || _crashDialogShown || _crashAnalyzer is null || _crashDialog is null)
        {
            _crashDetectionQueued = false;
            return;
        }

        try
        {
            // Give the crash handler a moment to flush latest.log/crash-reports.
            await Task.Delay(TimeSpan.FromSeconds(3));
            if (_crashDialogShown)
            {
                return;
            }

            CrashAnalysisResult? analysis = await _crashAnalyzer.AnalyzeAsync(instance, 1, _launchStartedAt, CancellationToken.None);
            if (analysis is null)
            {
                _crashDetectionQueued = false;
                return;
            }

            _crashDialogShown = true;
            StatusMessage = $"崩溃分析：{analysis.Summary}";
            AppendLog($"[launcher] 检测到游戏崩溃：{analysis.Summary}");
            _logger.LogInformation("Live crash analysis for {Instance}: {Summary}.", instance.Name, analysis.Summary);
            CrashRepairChoice choice = _crashDialog.Show(analysis);
            _logger.LogInformation("Live crash dialog choice for {Instance}: {Choice}.", instance.Name, choice);
            if (choice != CrashRepairChoice.AutoRepair)
            {
                if (choice == CrashRepairChoice.Manual)
                {
                    StatusMessage = "已保留崩溃现场，请关闭游戏后按分析建议手动处理。";
                    AppendLog("[launcher] 用户选择手动处理崩溃。");
                }

                return;
            }

            if (_crashRepair is null)
            {
                StatusMessage = "自动修复服务不可用，请按建议手动处理。";
                return;
            }

            // Stop the crashed game first so mod jars are no longer locked, then apply the reversible repairs.
            try
            {
                _runningProcess?.Kill();
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException)
            {
                _logger.LogWarning(exception, "结束崩溃中的游戏进程失败。");
            }

            StatusMessage = "正在应用自动修复…";
            AppendLog("[launcher] 用户选择自动修复，正在应用可逆修复…");
            var repairProgress = new Progress<string>(message => AppendLog($"[launcher] {message}"));
            CrashRepairResult repair = await _crashRepair.RepairAsync(instance, analysis, repairProgress, CancellationToken.None);
            foreach (CrashRepairAction action in repair.Actions)
            {
                AppendLog($"[launcher] 修复：{action.Description} — {(action.Success ? "成功" : "失败")}{(action.Detail is null ? string.Empty : "：" + action.Detail)}");
            }

            StatusMessage = repair.Summary;
            AppendLog($"[launcher] {repair.Summary}");
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "实时崩溃分析失败：{Instance}", instance.Name);
        }
    }

    private static bool IsCrashMarker(string line) =>
        line.Contains("Crash report saved to", StringComparison.OrdinalIgnoreCase)
        || line.Contains("Missing or unsupported mandatory dependencies", StringComparison.OrdinalIgnoreCase)
        || line.Contains("Mod Loading has failed", StringComparison.OrdinalIgnoreCase)
        || line.Contains("Minecraft has crashed", StringComparison.OrdinalIgnoreCase)
        || line.Contains("Error loading mods", StringComparison.OrdinalIgnoreCase)
        || line.Contains("errors have occurred during loading", StringComparison.OrdinalIgnoreCase)
        || line.Contains("Actual version: '[MISSING]'", StringComparison.OrdinalIgnoreCase)
        || line.Contains("A fatal error has been detected by the Java Runtime Environment", StringComparison.OrdinalIgnoreCase)
        || line.Contains("OutOfMemoryError", StringComparison.OrdinalIgnoreCase);

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
