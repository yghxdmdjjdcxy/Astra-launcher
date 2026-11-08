using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using AstraLauncher.App.Commands;
using AstraLauncher.App.Services;
using AstraLauncher.Core.Models;
using AstraLauncher.Core.Services;
using AstraLauncher.Plugin.Abstractions;
using AstraLauncher.Plugins;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace AstraLauncher.App.ViewModels;

/// <summary>Plugin manager: installs ZIPs, toggles manifests, and explains that changes require a restart.</summary>
public sealed class PluginsPageViewModel : PageViewModel
{
    private readonly ILauncherPaths _paths;
    private readonly ISettingsService _settings;
    private readonly PluginLoadResult _loadResult;
    private readonly IDialogService _dialogs;
    private readonly ILogger<PluginsPageViewModel> _logger;
    private readonly IReadOnlyList<IPluginPageContribution> _contributedPages;
    private readonly IPluginMarketplaceService? _marketplace;
    private bool _autoUpdatePlugins = true;
    private string _searchQuery = string.Empty;
    private string _marketStatus = "正在读取 GitHub 插件索引…";
    private bool _isMarketBusy;
    private string _statusMessage = "插件在启动器启动时加载；安装或启停后需要重启启动器生效。";

    public PluginsPageViewModel(
        ILauncherPaths paths,
        ISettingsService settings,
        PluginLoadResult loadResult,
        IDialogService dialogs,
        ILogger<PluginsPageViewModel> logger,
        IEnumerable<IPluginPageContribution>? contributedPages = null,
        IPluginMarketplaceService? marketplace = null) : base("插件")
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(loadResult);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(logger);
        _paths = paths;
        _settings = settings;
        _loadResult = loadResult;
        _dialogs = dialogs;
        _logger = logger;
        _contributedPages = contributedPages?.ToArray() ?? [];
        _marketplace = marketplace;
        _ = LoadAutoUpdateSettingAsync();

        InstallCommand = new AsyncRelayCommand(InstallAsync);
        RefreshCommand = new RelayCommand(_ => Refresh());
        OpenFolderCommand = new RelayCommand(_ => OpenFolder());
        ToggleCommand = new RelayCommand(parameter =>
        {
            if (parameter is PluginItem item)
            {
                Toggle(item);
            }
        });
        DeleteCommand = new RelayCommand(parameter =>
        {
            if (parameter is PluginItem item)
            {
                Delete(item);
            }
        });
        SearchMarketCommand = new AsyncRelayCommand(LoadMarketAsync);
        InstallMarketCommand = new RelayCommand(parameter =>
        {
            if (parameter is PluginMarketEntry entry)
            {
                _ = InstallMarketAsync(entry);
            }
        });
        OpenContributedPageCommand = new RelayCommand(parameter =>
        {
            if (parameter is PluginPageItem page)
            {
                OpenContributedPage(page);
            }
        });

        foreach (IPluginPageContribution contribution in _contributedPages)
        {
            ContributedPages.Add(new PluginPageItem(contribution));
        }

        Refresh();
        _ = LoadMarketAsync();
    }

    public ObservableCollection<PluginItem> Plugins { get; } = [];
    public ObservableCollection<PluginPageItem> ContributedPages { get; } = [];
    public ObservableCollection<PluginMarketEntry> MarketEntries { get; } = [];

    public ICommand InstallCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand ToggleCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand OpenContributedPageCommand { get; }
    public ICommand SearchMarketCommand { get; }
    public ICommand InstallMarketCommand { get; }

    public string SearchQuery
    {
        get => _searchQuery;
        set => SetProperty(ref _searchQuery, value);
    }

    public string MarketStatus
    {
        get => _marketStatus;
        private set => SetProperty(ref _marketStatus, value);
    }

    public bool AutoUpdatePlugins
    {
        get => _autoUpdatePlugins;
        set
        {
            if (SetProperty(ref _autoUpdatePlugins, value))
            {
                _ = SaveAutoUpdateSettingAsync(value);
            }
        }
    }

    public bool IsMarketBusy
    {
        get => _isMarketBusy;
        private set => SetProperty(ref _isMarketBusy, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string PluginRoot => UserRoot;

    private string UserRoot => Path.Combine(_paths.ApplicationDataDirectory, "plugins");

    private void Refresh()
    {
        Plugins.Clear();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in Roots())
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            IEnumerable<string> directories;
            try
            {
                directories = Directory.EnumerateDirectories(root).OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(exception, "Cannot enumerate plugin root {Root}.", root);
                continue;
            }

            foreach (string directory in directories)
            {
                if (!seen.Add(Path.GetFullPath(directory)))
                {
                    continue;
                }

                PluginItem? item = ReadItem(directory);
                if (item is not null)
                {
                    Plugins.Add(item);
                }
            }
        }

        StatusMessage = Plugins.Count == 0
            ? "还没有安装插件。安装 ZIP 后重启启动器即可加载。"
            : $"已发现 {Plugins.Count} 个插件。安装、启停或删除后请重启启动器。";
    }

    private PluginItem? ReadItem(string directory)
    {
        string manifestPath = Path.Combine(directory, PluginCatalog.ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            PluginManifest? manifest = JsonSerializer.Deserialize<PluginManifest>(
                File.ReadAllText(manifestPath), PluginApi.ManifestJsonOptions);
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id))
            {
                return null;
            }

            LoadedPlugin? loaded = _loadResult.Plugins.FirstOrDefault(
                plugin => string.Equals(Path.GetFullPath(plugin.Directory), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase));
            PluginLoadError? error = _loadResult.Errors.FirstOrDefault(
                item => string.Equals(Path.GetFullPath(item.Directory), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase));
            return new PluginItem(
                manifest.Id,
                string.IsNullOrWhiteSpace(manifest.Name) ? manifest.Id : manifest.Name,
                manifest.Version,
                manifest.Author ?? string.Empty,
                manifest.Description ?? string.Empty,
                Path.GetFullPath(directory),
                manifest.Enabled,
                loaded is not null,
                error?.Reason ?? string.Empty);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Cannot read plugin manifest {Manifest}.", manifestPath);
            return null;
        }
    }

    private IEnumerable<string> Roots()
    {
        yield return UserRoot;
        string portable = Path.Combine(AppContext.BaseDirectory, "plugins");
        if (!string.Equals(Path.GetFullPath(portable), Path.GetFullPath(UserRoot), StringComparison.OrdinalIgnoreCase))
        {
            yield return portable;
        }
    }

    private async Task InstallAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择插件 ZIP",
            Filter = "Astra 插件 (*.zip)|*.zip|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await InstallZipAsync(dialog.FileName, expectedId: null);
    }

    private async Task LoadAutoUpdateSettingAsync()
    {
        try
        {
            LauncherSettings settings = await _settings.GetAsync();
            _autoUpdatePlugins = settings.PluginAutoUpdate;
            SetProperty(ref _autoUpdatePlugins, settings.PluginAutoUpdate, nameof(AutoUpdatePlugins));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(exception, "Cannot read plugin auto-update setting.");
        }
    }

    private async Task SaveAutoUpdateSettingAsync(bool value)
    {
        try
        {
            LauncherSettings settings = await _settings.GetAsync();
            settings.PluginAutoUpdate = value;
            await _settings.SaveAsync(settings);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(exception, "Cannot save plugin auto-update setting.");
        }
    }

    private async Task LoadMarketAsync()
    {
        if (_marketplace is null)
        {
            MarketStatus = "插件市场服务不可用。";
            return;
        }

        if (IsMarketBusy)
        {
            return;
        }

        IsMarketBusy = true;
        MarketStatus = "正在读取 GitHub 插件索引…";
        try
        {
            IReadOnlyList<PluginMarketEntry> catalog = await _marketplace.GetCatalogAsync().ConfigureAwait(true);
            string query = SearchQuery.Trim();
            MarketEntries.Clear();
            foreach (PluginMarketEntry entry in catalog)
            {
                if (query.Length == 0 ||
                    entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    entry.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    entry.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    entry.Author.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    MarketEntries.Add(entry);
                }
            }

            MarketStatus = MarketEntries.Count == 0
                ? "没有找到插件。可以向插件索引仓库提交 Pull Request。"
                : $"共 {MarketEntries.Count} 个插件（来源：GitHub 插件索引）。";
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidOperationException or TaskCanceledException)
        {
            _logger.LogWarning(exception, "Plugin market load failed.");
            MarketStatus = "插件索引加载失败：" + exception.Message;
        }
        finally
        {
            IsMarketBusy = false;
        }
    }

    private async Task InstallMarketAsync(PluginMarketEntry entry)
    {
        if (_marketplace is null)
        {
            _dialogs.ShowError("插件市场服务不可用。", "安装插件");
            return;
        }

        IsMarketBusy = true;
        try
        {
            MarketStatus = $"正在下载 {entry.Name}…";
            var progress = new Progress<string>(message => MarketStatus = message);
            string zip = await _marketplace.DownloadAsync(entry, progress).ConfigureAwait(true);
            try
            {
                await InstallZipAsync(zip, entry.Id).ConfigureAwait(true);
            }
            finally
            {
                TryDeleteFile(zip);
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or InvalidOperationException or HttpRequestException)
        {
            _logger.LogError(exception, "Market plugin installation failed.");
            _dialogs.ShowError("安装插件失败：" + exception.Message, "插件市场");
        }
        finally
        {
            IsMarketBusy = false;
        }
    }

    private async Task InstallZipAsync(string zipPath, string? expectedId)
    {
        string temp = Path.Combine(Path.GetTempPath(), "AstraLauncher-PluginInstall", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temp);
            ExtractSafely(zipPath, temp);
            string? manifestPath = Directory.EnumerateFiles(temp, PluginCatalog.ManifestFileName, SearchOption.AllDirectories)
                .OrderBy(path => path.Length)
                .FirstOrDefault();
            if (manifestPath is null)
            {
                _dialogs.ShowError("插件 ZIP 中没有 plugin.json。", "安装插件");
                return;
            }

            string pluginDirectory = Path.GetDirectoryName(manifestPath)!;
            PluginManifest? manifest = JsonSerializer.Deserialize<PluginManifest>(
                await File.ReadAllTextAsync(manifestPath).ConfigureAwait(true), PluginApi.ManifestJsonOptions);
            if (manifest is null || !IsValidId(manifest.Id))
            {
                _dialogs.ShowError("plugin.json 缺少合法的 id。", "安装插件");
                return;
            }

            if (!string.IsNullOrWhiteSpace(expectedId) &&
                !string.Equals(manifest.Id, expectedId, StringComparison.OrdinalIgnoreCase))
            {
                _dialogs.ShowError($"插件包 id（{manifest.Id}）与市场记录（{expectedId}）不一致。", "安装插件");
                return;
            }

            if (manifest.ApiVersion != PluginApi.Version)
            {
                _dialogs.ShowError($"插件 API 版本 {manifest.ApiVersion} 与当前启动器 {PluginApi.Version} 不匹配。", "安装插件");
                return;
            }

            string? entry = manifest.EntryAssembly;
            string root = Path.GetFullPath(pluginDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string? entryPath = string.IsNullOrWhiteSpace(entry) ? null : Path.GetFullPath(Path.Combine(pluginDirectory, entry));
            if (entryPath is null || !entryPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(entryPath))
            {
                _dialogs.ShowError("插件入口程序集缺失或路径越界：" + entry, "安装插件");
                return;
            }

            Directory.CreateDirectory(UserRoot);
            string target = Path.Combine(UserRoot, manifest.Id);
            if (Directory.Exists(target))
            {
                if (!_dialogs.Confirm($"插件 {manifest.Id} 已存在，是否覆盖安装？", "安装插件"))
                {
                    return;
                }

                Directory.Delete(target, recursive: true);
            }

            CopyDirectory(pluginDirectory, target);
            StatusMessage = $"已安装 {manifest.Id}。重启启动器后生效。";
            Refresh();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            _logger.LogError(exception, "Plugin installation failed.");
            _dialogs.ShowError("安装插件失败：" + exception.Message, "安装插件");
        }
        finally
        {
            TryDeleteDirectory(temp);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best effort.
        }
    }
    private void Toggle(PluginItem item)
    {
        try
        {
            string manifestPath = Path.Combine(item.Directory, PluginCatalog.ManifestFileName);
            PluginManifest? manifest = JsonSerializer.Deserialize<PluginManifest>(
                File.ReadAllText(manifestPath), PluginApi.ManifestJsonOptions);
            if (manifest is null)
            {
                return;
            }

            manifest.Enabled = !manifest.Enabled;
            File.WriteAllText(
                manifestPath,
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            StatusMessage = $"已{(manifest.Enabled ? "启用" : "禁用")} {manifest.Id}。重启启动器后生效。";
            Refresh();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _dialogs.ShowError("修改插件状态失败：" + exception.Message, "插件");
        }
    }

    private void Delete(PluginItem item)
    {
        if (!_dialogs.Confirm($"确定删除插件 {item.Name}（{item.Id}）及其 data 目录？", "删除插件"))
        {
            return;
        }

        try
        {
            if (Directory.Exists(item.Directory))
            {
                Directory.Delete(item.Directory, recursive: true);
            }

            StatusMessage = $"已删除 {item.Id}。重启启动器后生效。";
            Refresh();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError("删除插件失败：" + exception.Message, "插件");
        }
    }

    private void OpenContributedPage(PluginPageItem page)
    {
        try
        {
            var window = new PluginViewWindow(page.Title, page.Contribution.CreateView())
            {
                Owner = Application.Current?.MainWindow,
            };
            window.ShowDialog();
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        {
            _dialogs.ShowError("打开插件页面失败：" + exception.Message, "插件");
        }
    }

    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(UserRoot);
            Process.Start(new ProcessStartInfo(UserRoot) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _dialogs.ShowError("无法打开插件目录：" + exception.Message, "插件");
        }
    }

    private static bool IsValidId(string id) =>
        !string.IsNullOrWhiteSpace(id)
        && id.Length <= 64
        && id.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void ExtractSafely(string zipPath, string destination)
    {
        string root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using ZipArchive archive = ZipFile.OpenRead(zipPath);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            string target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("插件 ZIP 包含越界路径：" + entry.FullName);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort.
        }
    }
}

/// <summary>Display model for one plugin folder.</summary>
public sealed record PluginPageItem(IPluginPageContribution Contribution)
{
    public string Title => string.IsNullOrWhiteSpace(Contribution.Title) ? Contribution.Id : Contribution.Title;
    public string Description => Contribution.Description;
}

/// <summary>Display model for one plugin folder.</summary>
public sealed record PluginItem(
    string Id,
    string Name,
    string Version,
    string Author,
    string Description,
    string Directory,
    bool Enabled,
    bool Loaded,
    string Error)
{
    public string StateText => !string.IsNullOrWhiteSpace(Error)
        ? "加载失败：" + Error
        : Loaded
            ? "已加载"
            : Enabled
                ? "已启用（重启后加载）"
                : "已禁用";
}
