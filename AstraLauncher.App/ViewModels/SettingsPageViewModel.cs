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
    private int _memoryMiB = LauncherSettings.DefaultMemoryMiB;
    private int _maxConcurrentDownloads = LauncherSettings.DefaultMaxConcurrentDownloads;
    private DownloadSourceMode _sourceMode = DownloadSourceMode.OfficialWithFallback;
    private string _mirrorBaseUrl = LauncherSettings.DefaultMirrorBaseUrl;
    private string _minecraftDirectory = string.Empty;
    private JavaRuntimeOption? _selectedJavaChoice = AutomaticJavaOption;
    private string? _selectedJavaPath;
    private bool _showSnapshots;
    private string _offlinePlayerName = "Player";
    private string? _gameArguments;
    private string? _jvmArguments;
    private bool _isBusy;
    private string _statusMessage = "正在加载设置…";

    public SettingsPageViewModel(ISettingsService settings, IJavaRuntimeDetector java, ILauncherPaths paths, ILogger<SettingsPageViewModel> logger, IDialogService dialogs) : base("设置")
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
        MinecraftDirectory = paths.MinecraftDirectory;
        JavaOptions.Add(AutomaticJavaOption);
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        RefreshJavaCommand = new AsyncRelayCommand(RefreshJavaAsync);
        BrowseMinecraftDirectoryCommand = new RelayCommand(_ => BrowseMinecraftDirectory());
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

    public string OfflinePlayerName { get => _offlinePlayerName; set => SetProperty(ref _offlinePlayerName, value); }

    public string? GameArguments { get => _gameArguments; set => SetProperty(ref _gameArguments, value); }

    public string? JvmArguments { get => _jvmArguments; set => SetProperty(ref _jvmArguments, value); }

    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }

    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }

    public ICommand SaveCommand { get; }

    public ICommand RefreshJavaCommand { get; }

    public ICommand BrowseMinecraftDirectoryCommand { get; }

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
            StatusMessage = $"设置已保存。Minecraft 目录：{_paths.MinecraftDirectory}";
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
