using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using AstraLauncher.App.Commands;
using AstraLauncher.App.Services;
using AstraLauncher.Core.Models;
using AstraLauncher.Core.Services;
using AstraLauncher.Game.Loaders;
using Microsoft.Extensions.Logging;

namespace AstraLauncher.App.ViewModels;

/// <summary>A loader option shown in the downloads page.</summary>
public sealed record LoaderKindOption(LoaderKind Kind, string Display);

public sealed class DownloadsPageViewModel : PageViewModel
{
    private readonly IVersionManifestService _manifest;
    private readonly IVersionInstallService _install;
    private readonly ILoaderCatalog? _loaderCatalog;
    private readonly ILoaderInstallService? _loaderInstaller;
    private readonly ILogger<DownloadsPageViewModel> _logger;
    private readonly IDialogService _dialogs;
    private readonly RelayCommand _cancelCommand;
    private readonly RelayCommand _cancelLoaderCommand;
    private VersionSummary? _selectedVersion;
    private string _statusMessage = "正在加载版本列表…";
    private bool _isBusy;
    private bool _ignoreProgressCallbacks;
    private double _progressPercent;
    private string _progressText = "尚未开始下载。选择版本后点击「下载所选版本」。";
    private CancellationTokenSource? _installCancellation;
    private int _installGeneration;
    private LoaderKindOption _selectedLoaderKind;
    private LoaderVersion? _selectedLoaderVersion;
    private bool _isLoaderBusy;
    private int _loaderRequestVersion;
    private double _loaderProgressPercent;
    private string _loaderProgressText = string.Empty;
    private string _loaderStatus = "选择 Minecraft 版本后可查看可用加载器版本。";
    private CancellationTokenSource? _loaderCancellation;

    public DownloadsPageViewModel(
        IVersionManifestService manifest,
        IVersionInstallService install,
        ILogger<DownloadsPageViewModel> logger,
        IDialogService dialogs,
        ILoaderCatalog? loaderCatalog = null,
        ILoaderInstallService? loaderInstaller = null) : base("下载")
    {
        _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        _install = install ?? throw new ArgumentNullException(nameof(install));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _loaderCatalog = loaderCatalog;
        _loaderInstaller = loaderInstaller;
        LoaderKinds =
        [
            new LoaderKindOption(LoaderKind.Fabric, "Fabric"),
            new LoaderKindOption(LoaderKind.Quilt, "Quilt"),
            new LoaderKindOption(LoaderKind.Forge, "Forge"),
            new LoaderKindOption(LoaderKind.NeoForge, "NeoForge"),
        ];
        _selectedLoaderKind = LoaderKinds[0];
        RefreshVersionsCommand = new AsyncRelayCommand(() => RunAsync("刷新版本列表", RefreshVersionsAsync));
        InstallCommand = new AsyncRelayCommand(InstallAsync);
        RefreshLoaderVersionsCommand = new AsyncRelayCommand(RefreshLoaderVersionsAsync);
        InstallLoaderCommand = new AsyncRelayCommand(InstallLoaderAsync);
        _cancelCommand = new RelayCommand(_ => CancelInstall(), _ => IsBusy);
        _cancelLoaderCommand = new RelayCommand(_ => CancelLoader(), _ => IsLoaderBusy);
        CancelCommand = _cancelCommand;
        CancelLoaderCommand = _cancelLoaderCommand;
        if (_loaderCatalog is null || _loaderInstaller is null)
        {
            _loaderStatus = "当前未启用加载器安装服务。";
        }
    }

    /// <summary>Raised after a loader version was installed, with the new version id.</summary>
    public event Action<string>? LoaderInstalled;

    public ObservableCollection<VersionSummary> Versions { get; } = [];

    public VersionSummary? SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            if (SetProperty(ref _selectedVersion, value))
            {
                Interlocked.Increment(ref _loaderRequestVersion);
                LoaderVersions.Clear();
                SelectedLoaderVersion = null;
                _ = RefreshLoaderVersionsAsync();
            }
        }
    }

    public IReadOnlyList<LoaderKindOption> LoaderKinds { get; }

    public LoaderKindOption SelectedLoaderKind
    {
        get => _selectedLoaderKind;
        set
        {
            if (SetProperty(ref _selectedLoaderKind, value))
            {
                Interlocked.Increment(ref _loaderRequestVersion);
                LoaderVersions.Clear();
                SelectedLoaderVersion = null;
                _ = RefreshLoaderVersionsAsync();
            }
        }
    }

    public ObservableCollection<LoaderVersion> LoaderVersions { get; } = [];

    public LoaderVersion? SelectedLoaderVersion { get => _selectedLoaderVersion; set => SetProperty(ref _selectedLoaderVersion, value); }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value)) _cancelCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsLoaderBusy
    {
        get => _isLoaderBusy;
        private set
        {
            if (SetProperty(ref _isLoaderBusy, value)) _cancelLoaderCommand.RaiseCanExecuteChanged();
        }
    }

    public double ProgressPercent { get => _progressPercent; private set => SetProperty(ref _progressPercent, value); }

    public string ProgressText { get => _progressText; private set => SetProperty(ref _progressText, value); }

    public double LoaderProgressPercent { get => _loaderProgressPercent; private set => SetProperty(ref _loaderProgressPercent, value); }

    public string LoaderProgressText { get => _loaderProgressText; private set => SetProperty(ref _loaderProgressText, value); }

    public string LoaderStatus { get => _loaderStatus; private set => SetProperty(ref _loaderStatus, value); }

    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }

    public ICommand RefreshVersionsCommand { get; }

    public ICommand InstallCommand { get; }

    public ICommand CancelCommand { get; }

    public ICommand RefreshLoaderVersionsCommand { get; }

    public ICommand InstallLoaderCommand { get; }

    public ICommand CancelLoaderCommand { get; }

    public async Task InitializeAsync()
    {
        if (Versions.Count == 0) await RunAsync("加载版本列表", RefreshVersionsAsync);
        if (_loaderCatalog is not null) await RefreshLoaderVersionsAsync();
    }

    private async Task RefreshVersionsAsync()
    {
        IReadOnlyList<VersionSummary> versions = await _manifest.GetVersionsAsync(includeSnapshots: false);
        string? previousId = SelectedVersion?.Id;
        _selectedVersion = null;
        Versions.Clear();
        foreach (VersionSummary version in versions) Versions.Add(version);
        SelectedVersion = Versions.FirstOrDefault(version => string.Equals(version.Id, previousId, StringComparison.Ordinal)) ?? Versions.FirstOrDefault();
        StatusMessage = Versions.Count == 0 ? "未获取到可用版本列表。" : $"已加载 {Versions.Count} 个正式版本。";
    }

    private async Task RefreshLoaderVersionsAsync()
    {
        int requestVersion = Interlocked.Increment(ref _loaderRequestVersion);
        if (_loaderCatalog is null || SelectedVersion is null)
        {
            if (LoaderVersions.Count == 0) LoaderStatus = "选择 Minecraft 版本后可查看可用加载器版本。";
            return;
        }

        string minecraftVersion = SelectedVersion.Id;
        LoaderKindOption kindOption = SelectedLoaderKind;
        LoaderStatus = $"正在获取 {kindOption.Display} 加载器版本…";
        try
        {
            IReadOnlyList<LoaderVersion> versions = await _loaderCatalog.GetLoaderVersionsAsync(kindOption.Kind, minecraftVersion);
            if (requestVersion != Volatile.Read(ref _loaderRequestVersion))
            {
                // Another refresh (the user changed the Minecraft version or loader kind) started while we waited.
                return;
            }

            LoaderVersions.Clear();
            foreach (LoaderVersion version in versions) LoaderVersions.Add(version);
            SelectedLoaderVersion = LoaderVersions.FirstOrDefault(version => version.IsRecommended) ?? LoaderVersions.FirstOrDefault();
            LoaderStatus = LoaderVersions.Count == 0
                ? $"{kindOption.Display} 暂无适配 Minecraft {minecraftVersion} 的版本。"
                : $"已加载 {LoaderVersions.Count} 个 {kindOption.Display} 版本。";
        }
        catch (Exception exception)
        {
            if (requestVersion != Volatile.Read(ref _loaderRequestVersion))
            {
                return;
            }

            _logger.LogWarning(exception, "加载器版本列表获取失败：{LoaderKind} {MinecraftVersion}", kindOption.Kind, minecraftVersion);
            LoaderStatus = $"加载器版本获取失败：{exception.Message}";
        }
    }

    private async Task InstallLoaderAsync()
    {
        if (_loaderInstaller is null || _loaderCatalog is null)
        {
            LoaderStatus = "当前未启用加载器安装服务。";
            return;
        }

        if (SelectedVersion is null)
        {
            LoaderStatus = "请先在列表中选择 Minecraft 版本。";
            return;
        }

        if (SelectedLoaderVersion is null)
        {
            LoaderStatus = "请选择要安装的加载器版本。";
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _loaderCancellation = cancellation;
        IsLoaderBusy = true;
        LoaderProgressPercent = 0;
        LoaderProgressText = "准备安装加载器…";
        try
        {
            var progress = new Progress<LoaderInstallProgress>(OnLoaderProgress);
            LoaderInstallResult result = await _loaderInstaller.InstallAsync(
                SelectedLoaderKind.Kind,
                SelectedVersion.Id,
                SelectedLoaderVersion.Version,
                progress,
                cancellation.Token);
            LoaderProgressPercent = 100;
            LoaderStatus = $"已安装 {SelectedLoaderKind.Display}：版本 ID “{result.VersionId}”。可在「游戏实例」的版本列表中选择它启动。";
            LoaderInstalled?.Invoke(result.VersionId);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            LoaderStatus = "已取消加载器安装。";
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "安装加载器失败：{LoaderKind} {LoaderVersion} for {MinecraftVersion}", SelectedLoaderKind.Kind, SelectedLoaderVersion.Version, SelectedVersion.Id);
            LoaderStatus = $"安装加载器失败：{exception.Message}";
            _dialogs.ShowError(LoaderStatus);
        }
        finally
        {
            _loaderCancellation = null;
            IsLoaderBusy = false;
        }
    }

    private void OnLoaderProgress(LoaderInstallProgress progress)
    {
        if (!IsLoaderBusy) return;
        LoaderProgressText = string.IsNullOrWhiteSpace(progress.Message) ? progress.Stage : $"{progress.Stage}：{progress.Message}";
        if (progress.Percent is double percent) LoaderProgressPercent = Math.Clamp(percent, 0, 100);
    }

    private void CancelLoader()
    {
        if (_loaderCancellation is null) return;
        LoaderStatus = "正在取消加载器安装…";
        _loaderCancellation.Cancel();
    }

    private async Task InstallAsync() => await RunAsync("下载版本", async () =>
    {
        VersionSummary version = SelectedVersion ?? throw new InvalidOperationException("请先在列表中选择要下载的版本。");
        using var cancellation = new CancellationTokenSource();
        _installCancellation = cancellation;
        int generation = ++_installGeneration;
        IsBusy = true;
        _ignoreProgressCallbacks = false;
        ProgressPercent = 0;
        ProgressText = "准备下载计划…";
        try
        {
            var progress = new Progress<VersionInstallProgress>(value => UpdateProgress(generation, value));
            await _install.InstallAsync(version.Id, progress, cancellation.Token);
            _ignoreProgressCallbacks = true;
            ProgressPercent = 100;
            StatusMessage = $"版本 {version.Id} 已下载并校验完成。";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            StatusMessage = $"已取消下载 {version.Id}；再次下载会从断点继续。";
        }
        finally
        {
            _installGeneration++;
            _installCancellation = null;
            IsBusy = false;
        }
    });

    private void UpdateProgress(int generation, VersionInstallProgress progress)
    {
        // Late callbacks can arrive after the install task completed (Progress<T> marshals asynchronously);
        // never let them overwrite the final 100% state.
        if (generation != _installGeneration || !IsBusy || _ignoreProgressCallbacks) return;
        ProgressPercent = CalculatePercent(progress);
        string downloaded = FormatBytes(progress.DownloadedBytes);
        string total = progress.TotalBytes > 0 ? FormatBytes(progress.TotalBytes) : "未知";
        ProgressText = $"{progress.CompletedFiles}/{progress.TotalFiles} 个文件 · {downloaded}/{total} · {progress.Stage}";
    }

    private static double CalculatePercent(VersionInstallProgress progress)
    {
        if (progress.TotalBytes > 0) return Math.Clamp(progress.DownloadedBytes * 100.0 / progress.TotalBytes, 0, 100);
        if (progress.TotalFiles > 0) return Math.Clamp(progress.CompletedFiles * 100.0 / progress.TotalFiles, 0, 100);
        return 0;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return string.Create(CultureInfo.InvariantCulture, $"{bytes} B");
        if (bytes < 1024 * 1024) return string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:F1} KiB");
        if (bytes < 1024L * 1024 * 1024) return string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024):F1} MiB");
        return string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024 * 1024):F2} GiB");
    }

    private void CancelInstall()
    {
        if (_installCancellation is null) return;
        StatusMessage = "正在取消下载…";
        _installCancellation.Cancel();
    }

    private async Task RunAsync(string operation, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "下载页操作失败：{Operation}", operation);
            StatusMessage = $"操作失败：{exception.Message}";
            _dialogs.ShowError(StatusMessage);
        }
    }
}
