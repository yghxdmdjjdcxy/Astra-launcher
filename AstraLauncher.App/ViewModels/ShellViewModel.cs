using System.IO;
using System.Reflection;
using System.Windows.Input;
using AstraLauncher.App.Commands;
using AstraLauncher.App.Services;
using AstraLauncher.Authentication;
using AstraLauncher.Core.Models;
using AstraLauncher.Core.Services;
using Microsoft.Extensions.Logging;

namespace AstraLauncher.App.ViewModels;

/// <summary>Shell state shared by the navigation sidebar, header and welcome page.</summary>
public sealed class ShellViewModel : ObservableObject
{
    private readonly InstancesPageViewModel _instances;
    private readonly DownloadsPageViewModel _downloads;
    private readonly SettingsPageViewModel _settings;
    private readonly AccountsPageViewModel _accounts;
    private readonly IAccountService _accountService;
    private readonly ILauncherPaths _paths;
    private readonly IVersionManifestService _manifest;
    private readonly ILogger<ShellViewModel> _logger;
    private string _accountSummary = "正在加载账户…";
    private string _javaSummary = "正在检测 Java…";
    private string _versionSummary = "正在加载版本列表…";
    private string _minecraftDirectory = string.Empty;

    public ShellViewModel(
        INavigationService navigation,
        InstancesPageViewModel instances,
        DownloadsPageViewModel downloads,
        SettingsPageViewModel settings,
        AccountsPageViewModel accounts,
        IAccountService accountService,
        ILauncherPaths paths,
        IVersionManifestService manifest,
        ILogger<ShellViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(navigation);
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(downloads);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(accountService);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(logger);
        Navigation = navigation;
        _instances = instances;
        _downloads = downloads;
        _settings = settings;
        _accounts = accounts;
        _accountService = accountService;
        _paths = paths;
        _manifest = manifest;
        _logger = logger;
        _minecraftDirectory = paths.MinecraftDirectory;
        AppVersion = ResolveVersion();
        NavigateCommand = new RelayCommand(Navigate);
        _downloads.LoaderInstalled += OnLoaderInstalled;
    }

    public INavigationService Navigation { get; }

    public ICommand NavigateCommand { get; }

    /// <summary>Gets the short application version shown in the sidebar/footer (for example <c>v1.0.0</c>).</summary>
    public string AppVersion { get; }

    /// <summary>Gets the resolved Minecraft root directory shown on the welcome page.</summary>
    public string MinecraftDirectory
    {
        get => _minecraftDirectory;
        private set => SetProperty(ref _minecraftDirectory, value);
    }

    /// <summary>Gets a human-readable summary of the currently selected account.</summary>
    public string AccountSummary
    {
        get => _accountSummary;
        private set => SetProperty(ref _accountSummary, value);
    }

    /// <summary>Gets a human-readable summary of the detected Java runtimes.</summary>
    public string JavaSummary
    {
        get => _javaSummary;
        private set => SetProperty(ref _javaSummary, value);
    }

    /// <summary>Gets a human-readable summary of the loaded version list.</summary>
    public string VersionSummary
    {
        get => _versionSummary;
        private set => SetProperty(ref _versionSummary, value);
    }

    /// <summary>Gets the one-line guidance shown in the window footer.</summary>
    public string FooterHint =>
        "开始游戏：在「下载」选择版本 → 在「游戏实例」创建并启动。启动前会自动校验并补齐游戏文件。";

    public async Task InitializeAsync()
    {
        await _instances.InitializeAsync();
        JavaSummary = _instances.JavaRuntimes.Count == 0
            ? "正在检测 Java…"
            : $"已检测到 {_instances.JavaRuntimes.Count} 个 Java";

        await _downloads.InitializeAsync();
        try
        {
            await _settings.InitializeAsync();
            int detectedJava = Math.Max(0, _settings.JavaOptions.Count - 1);
            JavaSummary = detectedJava == 0
                ? "未检测到 Java（可在设置页指定路径）"
                : $"已检测到 {detectedJava} 个 Java";
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "设置页初始化失败；设置页将使用默认值。");
            JavaSummary = "检测失败（可在设置页重新检测）";
        }

        try
        {
            await _accounts.InitializeAsync();
            AccountProfile? selected = await _accountService.GetSelectedAsync();
            AccountSummary = selected is null
                ? "未选择（将在设置/账户页选择）"
                : $"{selected.PlayerName}（{(selected.IsMicrosoft ? "官方 Microsoft" : "离线")}）";
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "账户页初始化失败；账户页稍后可手动刷新。");
            AccountSummary = "加载失败（可在账户页刷新）";
        }

        try
        {
            IReadOnlyList<VersionSummary> versions = await _manifest.GetVersionsAsync(includeSnapshots: false);
            var versionIds = versions.Select(version => version.Id).ToList();
            int localVersionCount = AddLocalVersions(versionIds);
            _instances.SetAvailableVersions(versionIds);
            VersionSummary = localVersionCount > 0
                ? $"已加载 {versions.Count} 个正式版本 · {localVersionCount} 个本地版本"
                : $"已加载 {versions.Count} 个正式版本";
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "版本列表加载失败；实例页仍可手动输入版本号。");
            VersionSummary = "加载失败（检查网络后可在下载页刷新）";
        }
    }

    /// <summary>
    /// Adds versions that already exist under <c>.minecraft\versions</c> (for example PCL-installed or freshly installed
    /// loader profiles) to the instance version picker, so they can be launched without being re-downloaded.
    /// </summary>
    private int AddLocalVersions(List<string> versionIds)
    {
        int added = 0;
        try
        {
            if (!Directory.Exists(_paths.VersionsDirectory))
            {
                return 0;
            }

            var known = new HashSet<string>(versionIds, StringComparer.Ordinal);
            foreach (string directory in Directory.EnumerateDirectories(_paths.VersionsDirectory))
            {
                string versionId = Path.GetFileName(directory);
                if (string.IsNullOrWhiteSpace(versionId) || known.Contains(versionId))
                {
                    continue;
                }

                if (!File.Exists(Path.Combine(directory, versionId + ".json")))
                {
                    continue;
                }

                versionIds.Add(versionId);
                known.Add(versionId);
                added++;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "读取本地版本目录失败；实例版本列表只显示在线版本。");
        }

        if (added > 0)
        {
            _logger.LogInformation("Added {LocalVersionCount} local version(s) from {VersionsDirectory} to the instance version picker.", added, _paths.VersionsDirectory);
        }

        return added;
    }

    private void OnLoaderInstalled(string versionId)
    {
        _instances.AddAvailableVersion(versionId);
        VersionSummary = string.IsNullOrWhiteSpace(VersionSummary)
            ? $"已使用本地版本 {versionId}"
            : $"{VersionSummary} · 新增本地版本 {versionId}";
    }

    private void Navigate(object? parameter)
    {
        if (parameter is not string pageName || !Enum.TryParse(pageName, out PageKey page))
            throw new ArgumentException("The navigation command requires a known page name.", nameof(parameter));
        Navigation.Navigate(page);
    }

    private static string ResolveVersion()
    {
        Assembly assembly = typeof(ShellViewModel).Assembly;
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            string shortVersion = informational.Split('+')[0];
            if (!string.IsNullOrWhiteSpace(shortVersion))
            {
                return $"v{shortVersion}";
            }
        }

        return $"v{assembly.GetName().Version?.ToString(3) ?? "1.0.0"}";
    }
}
