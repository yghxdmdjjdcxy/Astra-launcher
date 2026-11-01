using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using AstraLauncher.App.Commands;
using AstraLauncher.App.Services;
using AstraLauncher.Core.Models;
using AstraLauncher.Core.Services;
using AstraLauncher.Game.Java;
using AstraLauncher.Infrastructure.FileSystem;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace AstraLauncher.App.ViewModels;

/// <summary>A selectable Java runtime entry; a <see langword="null"/> path means automatic selection.</summary>
public sealed record JavaRuntimeOption(string Display, string? ExecutablePath);

/// <summary>Editable launcher settings with explicit save and Java re-detection.</summary>
public sealed class SettingsPageViewModel : PageViewModel
{
    private const string AutomaticJavaDisplay = "自动选择（按版本要求）";
    private static readonly JavaRuntimeOption AutomaticJavaOption = new(AutomaticJavaDisplay, null);

    private readonly ISettingsService _settings;
    private readonly IJavaRuntimeDetector _java;
    private readonly ILauncherPaths _paths;
    private readonly ILogger<SettingsPageViewModel> _logger;
    private readonly IDialogService _dialogs;
    private readonly IUpdatePrompter? _updatePrompter;
    private readonly IJavaInstallerService? _javaInstaller;
    private int _memoryMiB = LauncherSettings.DefaultMemoryMiB;
    private int _maxConcurrentDownloads = LauncherSettings.DefaultMaxConcurrentDownloads;
    private DownloadSourceMode _sourceMode = DownloadSourceMode.OfficialWithFallback;
    private string _mirrorBaseUrl = LauncherSettings.DefaultMirrorBaseUrl;
    private string _minecraftDirectory = string.Empty;
    private JavaRuntimeOption? _selectedJavaChoice = AutomaticJavaOption;
    private string? _selectedJavaPath;
    private bool _showSnapshots;
    private bool _autoCheckUpdates = true;
    private JavaDownloadOption? _selectedJavaDownload;
    private bool _showBetaNotice = true;
    private string _offlinePlayerName = "Player";
    private string? _gameArguments;
    private string? _jvmArguments;
    private bool _isBusy;
    private string _statusMessage = "正在加载设置…";

    public SettingsPageViewModel(ISettingsService settings, IJavaRuntimeDetector java, ILauncherPaths paths, ILogger<SettingsPageViewModel> logger, IDialogService dialogs, IUpdatePrompter? updatePrompter = null, IJavaInstallerService? javaInstaller = null) : base("设置")
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(java);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(dialogs);
        _settings = settings;
        _java = java;
        _paths = paths;
        _logger = logger;
        _dialogs = dialogs;
        _updatePrompter = updatePrompter;
        _javaInstaller = javaInstaller;
        if (javaInstaller is not null)
        {
            JavaDownloads = javaInstaller.Options;
            _selectedJavaDownload = JavaDownloads.FirstOrDefault();
        }
        MinecraftDirectory = paths.MinecraftDirectory;
        JavaOptions.Add(AutomaticJavaOption);
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        RefreshJavaCommand = new AsyncRelayCommand(RefreshJavaAsync);
        BrowseMinecraftDirectoryCommand = new RelayCommand(_ => BrowseMinecraftDirectory());
        CheckUpdateCommand = new AsyncRelayCommand(CheckUpdateAsync);
        ShowChangelogCommand = new RelayCommand(_ => new ChangelogWindow().ShowDialog());
        DownloadJavaCommand = new AsyncRelayCommand(DownloadJavaAsync);
    }

    public IReadOnlyList<DownloadSourceMode> AvailableSourceModes { get; } = Enum.GetValues<DownloadSourceMode>();

    public ObservableCollection<JavaRuntimeOption> JavaOptions { get; } = [];

    public int MemoryMiB { get => _memoryMiB; set => SetProperty(ref _memoryMiB, value); }

    public int MaxConcurrentDownloads { get => _maxConcurrentDownloads; set => SetProperty(ref _maxConcurrentDownloads, value); }

    public DownloadSourceMode SourceMode { get => _sourceMode; set => SetProperty(ref _sourceMode, value); }

    public string MirrorBaseUrl { get => _mirrorBaseUrl; set => SetProperty(ref _mirrorBaseUrl, value); }

    /// <summary>
    /// Gets or sets the Minecraft root directory (normally a <c>.minecraft</c> folder) that holds versions,
    /// libraries and assets. Saving applies it immediately; files already in that folder are reused.
    /// </summary>
    public string MinecraftDirectory { get => _minecraftDirectory; set => SetProperty(ref _minecraftDirectory, value); }

    /// <summary>Gets the directory that stores launcher settings, accounts, logs and instances.</summary>
    public string LauncherDataDirectory => _paths.ApplicationDataDirectory;

    public JavaRuntimeOption? SelectedJavaChoice
    {
        get => _selectedJavaChoice;
        set
        {
            if (SetProperty(ref _selectedJavaChoice, value))
            {
                SetProperty(ref _selectedJavaPath, value?.ExecutablePath, nameof(SelectedJavaPath));
            }
        }
    }

    /// <summary>Gets or sets the saved Java executable path; <see langword="null"/> means automatic selection.</summary>
    public string? SelectedJavaPath
    {
        get => _selectedJavaPath;
        set => SelectedJavaChoice = FindJavaOption(value);
    }

    public bool ShowSnapshots { get => _showSnapshots; set => SetProperty(ref _showSnapshots, value); }

    public bool AutoCheckUpdates { get => _autoCheckUpdates; set => SetProperty(ref _autoCheckUpdates, value); }

    public bool ShowBetaNotice { get => _showBetaNotice; set => SetProperty(ref _showBetaNotice, value); }

    public IReadOnlyList<JavaDownloadOption> JavaDownloads { get; } = [];

    public JavaDownloadOption? SelectedJavaDownload { get => _selectedJavaDownload; set => SetProperty(ref _selectedJavaDownload, value); }

    public string CurrentVersionText => _updatePrompter?.CurrentVersion ?? "1.0.0";

    public string OfflinePlayerName { get => _offlinePlayerName; set => SetProperty(ref _offlinePlayerName, value); }

    public string? GameArguments { get => _gameArguments; set => SetProperty(ref _gameArguments, value); }

    public string? JvmArguments { get => _jvmArguments; set => SetProperty(ref _jvmArguments, value); }

    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }

    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }

    public ICommand SaveCommand { get; }

    public ICommand RefreshJavaCommand { get; }

    public ICommand BrowseMinecraftDirectoryCommand { get; }

    public ICommand CheckUpdateCommand { get; }

    public ICommand ShowChangelogCommand { get; }

    public ICommand DownloadJavaCommand { get; }

    /// <summary>Loads persisted settings and refreshes the detected Java runtime list.</summary>
    public async Task InitializeAsync()
    {
        IsBusy = true;
        try
        {
            LauncherSettings settings = await _settings.GetAsync();
            ApplySettings(settings);
            MinecraftDirectory = _paths.MinecraftDirectory;
            IReadOnlyList<JavaRuntimeInfo> runtimes = await _java.DetectAsync();
            RebuildJavaOptions(runtimes, settings.JavaExecutablePath);
            StatusMessage = runtimes.Count == 0
                ? "设置已加载；未检测到 Java，启动时将使用自动选择。"
                : $"设置已加载；检测到 {runtimes.Count} 个 Java 运行时。";
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "加载设置页失败。");
            StatusMessage = FriendlyError(exception, "加载设置");
            _dialogs.ShowError(StatusMessage);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SaveAsync()
    {
        IsBusy = true;
        try
        {
            string? requestedMinecraftDirectory = string.IsNullOrWhiteSpace(MinecraftDirectory) ? null : MinecraftDirectory.Trim();
            if (requestedMinecraftDirectory is not null)
            {
                // Validate before persisting so an invalid path cannot break the next startup.
                _ = Path.GetFullPath(requestedMinecraftDirectory);
            }

            LauncherSettings settings = await _settings.GetAsync();
            settings.MemoryMiB = MemoryMiB;
            settings.MaxConcurrentDownloads = MaxConcurrentDownloads;
            settings.DownloadSourceMode = SourceMode;
            settings.MirrorBaseUrl = string.IsNullOrWhiteSpace(MirrorBaseUrl) ? LauncherSettings.DefaultMirrorBaseUrl : MirrorBaseUrl;
            settings.MinecraftDirectory = requestedMinecraftDirectory;
            settings.JavaExecutablePath = SelectedJavaChoice?.ExecutablePath;
            settings.ShowSnapshots = ShowSnapshots;
            settings.AutoCheckUpdates = AutoCheckUpdates;
            settings.ShowBetaNotice = ShowBetaNotice;
            settings.OfflinePlayerName = string.IsNullOrWhiteSpace(OfflinePlayerName) ? "Player" : OfflinePlayerName;
            settings.GameArguments = GameArguments;
            settings.JvmArguments = JvmArguments;
            settings.Normalize();
            await _settings.SaveAsync(settings);
            ApplySettings(settings);
            SelectedJavaPath = settings.JavaExecutablePath;
            _paths.SetMinecraftDirectory(settings.MinecraftDirectory ?? LauncherPaths.ResolveDefaultMinecraftDirectory());
            _paths.EnsureCreated();
            MinecraftDirectory = _paths.MinecraftDirectory;
            StatusMessage = $"设置已保存。Minecraft 目录：{_paths.MinecraftDirectory}（版本文件立即生效；启动器数据将在下次启动时迁移到该目录下的 AstraLauncher）。";
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "保存设置失败。");
            StatusMessage = FriendlyError(exception, "保存设置");
            _dialogs.ShowError(StatusMessage);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DownloadJavaAsync()
    {
        if (_javaInstaller is null || SelectedJavaDownload is null)
        {
            StatusMessage = "Java 下载服务不可用。";
            return;
        }

        IsBusy = true;
        StatusMessage = $"正在下载 Java {SelectedJavaDownload.Display}…";
        try
        {
            var progress = new Progress<string>(message => StatusMessage = message);
            string executable = await _javaInstaller.InstallAsync(SelectedJavaDownload.Major, progress).ConfigureAwait(true);
            LauncherSettings settings = await _settings.GetAsync().ConfigureAwait(true);
            settings.JavaExecutablePath = executable;
            await _settings.SaveAsync(settings).ConfigureAwait(true);
            SelectedJavaPath = executable;
            IReadOnlyList<JavaRuntimeInfo> runtimes = await _java.DetectAsync().ConfigureAwait(true);
            RebuildJavaOptions(runtimes, executable);
            StatusMessage = $"Java 已安装：{executable}";
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "下载 Java 失败。");
            StatusMessage = "下载 Java 失败：" + exception.Message;
            _dialogs.ShowError(StatusMessage);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task CheckUpdateAsync()
    {
        if (_updatePrompter is null)
        {
            StatusMessage = "更新服务不可用。";
            return;
        }

        StatusMessage = "正在检查更新…";
        try
        {
            await _updatePrompter.CheckAsync(silent: false).ConfigureAwait(true);
            StatusMessage = "检查更新完成。";
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "检查更新失败。");
            StatusMessage = "检查更新失败：" + exception.Message;
        }
    }

    private async Task RefreshJavaAsync()
    {
        string? previousPath = SelectedJavaPath;
        IsBusy = true;
        try
        {
            IReadOnlyList<JavaRuntimeInfo> runtimes = await _java.DetectAsync();
            RebuildJavaOptions(runtimes, previousPath);
            StatusMessage = runtimes.Count == 0
                ? "未检测到 Java；已切换为自动选择。"
                : $"已重新检测到 {runtimes.Count} 个 Java 运行时。";
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "重新检测 Java 运行时失败。");
            StatusMessage = FriendlyError(exception, "重新检测 Java");
            _dialogs.ShowError(StatusMessage);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplySettings(LauncherSettings settings)
    {
        MemoryMiB = settings.MemoryMiB;
        MaxConcurrentDownloads = settings.MaxConcurrentDownloads;
        SourceMode = settings.DownloadSourceMode;
        MirrorBaseUrl = settings.MirrorBaseUrl ?? LauncherSettings.DefaultMirrorBaseUrl;
        ShowSnapshots = settings.ShowSnapshots;
        AutoCheckUpdates = settings.AutoCheckUpdates;
        ShowBetaNotice = settings.ShowBetaNotice;
        OfflinePlayerName = string.IsNullOrWhiteSpace(settings.OfflinePlayerName) ? "Player" : settings.OfflinePlayerName;
        GameArguments = settings.GameArguments;
        JvmArguments = settings.JvmArguments;
    }

    private void BrowseMinecraftDirectory()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择 Minecraft 目录（versions / libraries / assets）",
            Multiselect = false,
        };
        if (Directory.Exists(MinecraftDirectory))
        {
            dialog.InitialDirectory = MinecraftDirectory;
        }

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            MinecraftDirectory = dialog.FolderName;
        }
    }

    private void RebuildJavaOptions(IReadOnlyList<JavaRuntimeInfo> runtimes, string? preferredPath)
    {
        JavaOptions.Clear();
        JavaOptions.Add(AutomaticJavaOption);
        foreach (JavaRuntimeInfo runtime in runtimes)
        {
            JavaOptions.Add(new JavaRuntimeOption(
                $"Java {runtime.MajorVersion} · {runtime.Version} · {runtime.ExecutablePath}",
                runtime.ExecutablePath));
        }

        // JavaRuntimeOption is a record; clear the field first so value equality cannot suppress the
        // change notification and leave WPF bound to an option that is no longer in JavaOptions.
        _selectedJavaChoice = null;
        SelectedJavaChoice = FindJavaOption(preferredPath);
    }

    private JavaRuntimeOption FindJavaOption(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return AutomaticJavaOption;
        }

        return JavaOptions.FirstOrDefault(option =>
            option.ExecutablePath is not null
            && string.Equals(option.ExecutablePath, executablePath, StringComparison.OrdinalIgnoreCase))
            ?? AutomaticJavaOption;
    }

    private static string FriendlyError(Exception exception, string operation) => exception switch
    {
        UnauthorizedAccessException => $"{operation}失败：没有访问权限，请检查设置文件或目录的权限。",
        IOException ioException => $"{operation}失败：{ioException.Message}",
        ArgumentException argumentException => $"{operation}失败：路径或参数无效（{argumentException.Message}）。",
        NotSupportedException => $"{operation}失败：路径格式不受支持，请选择本机目录。",
        InvalidOperationException invalidOperationException => $"{operation}失败：{invalidOperationException.Message}",
        _ => $"{operation}失败，请稍后重试。"
    };
}
