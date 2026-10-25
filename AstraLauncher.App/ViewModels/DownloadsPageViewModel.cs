using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using AstraLauncher.App.Commands;
using AstraLauncher.App.Services;
using AstraLauncher.Core.Models;
using AstraLauncher.Core.Services;
using Microsoft.Extensions.Logging;

namespace AstraLauncher.App.ViewModels;

public sealed class DownloadsPageViewModel : PageViewModel
{
    private readonly IVersionManifestService _manifest;
    private readonly IVersionInstallService _install;
    private readonly ILogger<DownloadsPageViewModel> _logger;
    private readonly IDialogService _dialogs;
    private readonly RelayCommand _cancelCommand;
    private VersionSummary? _selectedVersion;
    private string _statusMessage = "正在加载版本列表…";
    private bool _isBusy;
    private bool _ignoreProgressCallbacks;
    private double _progressPercent;
    private string _progressText = string.Empty;
    private CancellationTokenSource? _installCancellation;

    public DownloadsPageViewModel(IVersionManifestService manifest, IVersionInstallService install, ILogger<DownloadsPageViewModel> logger, IDialogService dialogs) : base("下载")
    {
        _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        _install = install ?? throw new ArgumentNullException(nameof(install));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        RefreshVersionsCommand = new AsyncRelayCommand(() => RunAsync("刷新版本列表", RefreshVersionsAsync));
        InstallCommand = new AsyncRelayCommand(InstallAsync);
        _cancelCommand = new RelayCommand(_ => CancelInstall(), _ => IsBusy);
        CancelCommand = _cancelCommand;
    }

    public ObservableCollection<VersionSummary> Versions { get; } = [];

    public VersionSummary? SelectedVersion { get => _selectedVersion; set => SetProperty(ref _selectedVersion, value); }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value)) _cancelCommand.RaiseCanExecuteChanged();
        }
    }

    public double ProgressPercent { get => _progressPercent; private set => SetProperty(ref _progressPercent, value); }

    public string ProgressText { get => _progressText; private set => SetProperty(ref _progressText, value); }

    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }

    public ICommand RefreshVersionsCommand { get; }

    public ICommand InstallCommand { get; }

    public ICommand CancelCommand { get; }

    public async Task InitializeAsync()
    {
        if (Versions.Count == 0) await RunAsync("加载版本列表", RefreshVersionsAsync);
    }

    private async Task RefreshVersionsAsync()
    {
        IReadOnlyList<VersionSummary> versions = await _manifest.GetVersionsAsync(includeSnapshots: false);
        string? previousId = SelectedVersion?.Id;
        Versions.Clear();
        foreach (VersionSummary version in versions) Versions.Add(version);
        SelectedVersion = Versions.FirstOrDefault(version => string.Equals(version.Id, previousId, StringComparison.Ordinal)) ?? Versions.FirstOrDefault();
        StatusMessage = Versions.Count == 0 ? "未获取到可用版本列表。" : $"已加载 {Versions.Count} 个正式版本。";
    }

    private async Task InstallAsync() => await RunAsync("下载版本", async () =>
    {
        VersionSummary version = SelectedVersion ?? throw new InvalidOperationException("请先在列表中选择要下载的版本。");
        using var cancellation = new CancellationTokenSource();
        _installCancellation = cancellation;
        IsBusy = true;
        _ignoreProgressCallbacks = false;
        ProgressPercent = 0;
        ProgressText = "准备下载计划…";
        try
        {
            var progress = new Progress<VersionInstallProgress>(UpdateProgress);
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
            _installCancellation = null;
            IsBusy = false;
        }
    });

    private void UpdateProgress(VersionInstallProgress progress)
    {
        // Late callbacks can arrive after the install task completed (Progress<T> marshals asynchronously);
        // never let them overwrite the final 100% state.
        if (!IsBusy || _ignoreProgressCallbacks) return;
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
