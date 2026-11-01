using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Input;
using AstraLauncher.App.Commands;
using AstraLauncher.App.Services;
using AstraLauncher.Core.Models;
using AstraLauncher.Core.Services;
using AstraLauncher.Game.Instances;
using AstraLauncher.Game.Mods;
using Microsoft.Extensions.Logging;

namespace AstraLauncher.App.ViewModels;

/// <summary>
/// MOD management page: manages the selected instance's mods folder (local jars, enable/disable/delete) and searches
/// Modrinth for compatible mods using the instance's Minecraft version and loader.
/// </summary>
public sealed partial class ModsPageViewModel : PageViewModel
{
    private readonly IGameInstanceRepository _instances;
    private readonly ILocalModService _mods;
    private readonly IModrinthClient _modrinth;
    private readonly IFilePickerService _filePicker;
    private readonly ILauncherPaths _paths;
    private readonly IDialogService _dialogs;
    private readonly ILogger<ModsPageViewModel> _logger;
    private GameInstance? _selectedInstance;
    private LocalMod? _selectedMod;
    private ModrinthSearchHit? _selectedSearchResult;
    private ModrinthVersion? _selectedModVersion;
    private LoaderOption _selectedLoader;
    private string _gameVersion = "1.21.1";
    private string _searchQuery = string.Empty;
    private string _statusMessage = "正在加载实例…";
    private string _searchStatus = "输入关键词搜索 Modrinth 上兼容的 MOD。";
    private bool _isBusy;
    private bool _isSearchBusy;

    public ModsPageViewModel(
        IGameInstanceRepository instances,
        ILocalModService mods,
        IModrinthClient modrinth,
        IFilePickerService filePicker,
        ILauncherPaths paths,
        IDialogService dialogs,
        ILogger<ModsPageViewModel> logger) : base("模组")
    {
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(mods);
        ArgumentNullException.ThrowIfNull(modrinth);
        ArgumentNullException.ThrowIfNull(filePicker);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(logger);
        _instances = instances;
        _mods = mods;
        _modrinth = modrinth;
        _filePicker = filePicker;
        _paths = paths;
        _dialogs = dialogs;
        _logger = logger;
        LoaderOptions =
        [
            new LoaderOption(ModPlatform.Fabric, "Fabric"),
            new LoaderOption(ModPlatform.Quilt, "Quilt"),
            new LoaderOption(ModPlatform.Forge, "Forge"),
            new LoaderOption(ModPlatform.NeoForge, "NeoForge"),
        ];
        _selectedLoader = LoaderOptions[0];
        RefreshInstancesCommand = new AsyncRelayCommand(RefreshInstancesAsync);
        RefreshModsCommand = new AsyncRelayCommand(LoadModsAsync);
        ImportCommand = new AsyncRelayCommand(ImportAsync);
        ToggleCommand = new AsyncRelayCommand(ToggleAsync);
        ToggleModCommand = new RelayCommand(parameter =>
        {
            if (parameter is LocalMod mod)
            {
                _ = ToggleAsync(mod);
            }
        });
        DeleteCommand = new AsyncRelayCommand(DeleteAsync);
        OpenFolderCommand = new RelayCommand(_ => OpenModsFolder());
        SearchCommand = new AsyncRelayCommand(SearchAsync);
        InstallCommand = new AsyncRelayCommand(InstallAsync);
    }

    public ObservableCollection<GameInstance> Instances { get; } = [];
    public ObservableCollection<LocalMod> Mods { get; } = [];
    public ObservableCollection<ModrinthSearchHit> SearchResults { get; } = [];
    public ObservableCollection<ModrinthVersion> ModVersions { get; } = [];
    public IReadOnlyList<LoaderOption> LoaderOptions { get; }

    public ICommand RefreshInstancesCommand { get; }
    public ICommand RefreshModsCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand ToggleCommand { get; }
    public ICommand ToggleModCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand SearchCommand { get; }
    public ICommand InstallCommand { get; }

    public GameInstance? SelectedInstance
    {
        get => _selectedInstance;
        set
        {
            if (!SetProperty(ref _selectedInstance, value) || value is null)
            {
                return;
            }

            DetectContext(value);
            _ = LoadModsAsync();
        }
    }

    public LocalMod? SelectedMod
    {
        get => _selectedMod;
        set => SetProperty(ref _selectedMod, value);
    }

    public ModrinthSearchHit? SelectedSearchResult
    {
        get => _selectedSearchResult;
        set
        {
            if (SetProperty(ref _selectedSearchResult, value) && value is not null)
            {
                _ = LoadVersionsAsync(value);
            }
        }
    }

    public ModrinthVersion? SelectedModVersion
    {
        get => _selectedModVersion;
        set => SetProperty(ref _selectedModVersion, value);
    }

    public LoaderOption SelectedLoader
    {
        get => _selectedLoader;
        set => SetProperty(ref _selectedLoader, value);
    }

    public string GameVersion
    {
        get => _gameVersion;
        set => SetProperty(ref _gameVersion, value);
    }

    public string SearchQuery
    {
        get => _searchQuery;
        set => SetProperty(ref _searchQuery, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string SearchStatus
    {
        get => _searchStatus;
        private set => SetProperty(ref _searchStatus, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public bool IsSearchBusy
    {
        get => _isSearchBusy;
        private set => SetProperty(ref _isSearchBusy, value);
    }

    public async Task InitializeAsync()
    {
        await RefreshInstancesAsync();
    }

    private async Task RefreshInstancesAsync()
    {
        try
        {
            Guid? selectedId = SelectedInstance?.Id;
            IReadOnlyList<GameInstance> values = await _instances.ListAsync();
            Instances.Clear();
            foreach (GameInstance instance in values)
            {
                Instances.Add(instance);
            }

            SelectedInstance = selectedId is Guid id
                ? Instances.FirstOrDefault(instance => instance.Id == id) ?? Instances.FirstOrDefault()
                : Instances.FirstOrDefault();
            if (SelectedInstance is null)
            {
                StatusMessage = "还没有游戏实例。请先在「游戏实例」页创建一个实例。";
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(exception, "加载实例列表失败。");
            StatusMessage = "加载实例列表失败：" + exception.Message;
        }
    }

    private async Task LoadModsAsync()
    {
        GameInstance? instance = SelectedInstance;
        if (instance is null)
        {
            Mods.Clear();
            return;
        }

        IsBusy = true;
        try
        {
            IReadOnlyList<LocalMod> mods = await _mods.ListAsync(instance);
            Mods.Clear();
            foreach (LocalMod mod in mods)
            {
                Mods.Add(mod);
            }

            SelectedMod = null;
            StatusMessage = Mods.Count == 0
                ? $"实例「{instance.Name}」还没有 MOD。可以导入本地 jar，或在下方搜索 Modrinth。"
                : $"实例「{instance.Name}」已安装 {Mods.Count} 个 MOD（{Mods.Count(mod => mod.IsEnabled)} 个启用）。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(exception, "读取 MOD 列表失败。");
            StatusMessage = "读取 MOD 列表失败：" + exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ImportAsync()
    {
        GameInstance? instance = SelectedInstance;
        if (instance is null)
        {
            StatusMessage = "请先选择实例。";
            return;
        }

        IReadOnlyList<string> files = _filePicker.PickModFiles();
        if (files.Count == 0)
        {
            return;
        }

        ModImportResult result = await _mods.ImportAsync(instance, files);
        await LoadModsAsync();
        StatusMessage = result.Errors.Count == 0
            ? $"已导入 {result.Imported.Count} 个 MOD。"
            : $"已导入 {result.Imported.Count} 个 MOD；{result.Errors.Count} 个未导入：{string.Join("；", result.Errors)}";
    }

    private async Task ToggleAsync()
    {
        LocalMod? selected = SelectedMod;
        if (selected is null)
        {
            StatusMessage = "请先在列表中选择一个 MOD。";
            return;
        }

        await ToggleAsync(selected);
    }

    private async Task ToggleAsync(LocalMod selected)
    {
        GameInstance? instance = SelectedInstance;
        if (instance is null)
        {
            StatusMessage = "请先选择实例。";
            return;
        }

        try
        {
            LocalMod updated = await _mods.SetEnabledAsync(instance, selected, !selected.IsEnabled);
            await LoadModsAsync();
            SelectedMod = Mods.FirstOrDefault(mod => string.Equals(mod.FilePath, updated.FilePath, StringComparison.OrdinalIgnoreCase));
            StatusMessage = updated.IsEnabled ? $"已启用 MOD：{updated.DisplayName}" : $"已禁用 MOD：{updated.DisplayName}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogError(exception, "切换 MOD 状态失败。");
            StatusMessage = "切换 MOD 状态失败：" + exception.Message;
        }
    }

    private async Task DeleteAsync()
    {
        GameInstance? instance = SelectedInstance;
        LocalMod? selected = SelectedMod;
        if (instance is null || selected is null)
        {
            StatusMessage = "请先在列表中选择一个 MOD。";
            return;
        }

        if (!_dialogs.Confirm($"确定删除 MOD「{selected.DisplayName}」？文件将从实例的 mods 目录移除。", "删除 MOD"))
        {
            return;
        }

        try
        {
            await _mods.DeleteAsync(instance, selected);
            await LoadModsAsync();
            StatusMessage = $"已删除 MOD：{selected.DisplayName}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(exception, "删除 MOD 失败。");
            StatusMessage = "删除 MOD 失败：" + exception.Message;
        }
    }

    private void OpenModsFolder()
    {
        GameInstance? instance = SelectedInstance;
        if (instance is null)
        {
            return;
        }

        try
        {
            string directory = _mods.GetModsDirectory(instance);
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(exception, "打开 MOD 目录失败。");
            StatusMessage = "打开 MOD 目录失败：" + exception.Message;
        }
    }

    private async Task SearchAsync()
    {
        if (SelectedInstance is null)
        {
            SearchStatus = "请先选择实例（用于确定 MOD 加载器与游戏版本）。";
            return;
        }

        if (string.IsNullOrWhiteSpace(SearchQuery))
        {
            SearchStatus = "请输入要搜索的 MOD 名称或关键词。";
            return;
        }

        if (string.IsNullOrWhiteSpace(GameVersion))
        {
            SearchStatus = "请填写 Minecraft 版本。";
            return;
        }

        IsSearchBusy = true;
        try
        {
            string loader = ModrinthClient.GetLoaderName(SelectedLoader.Platform);
            ModrinthSearchResult result = await _modrinth.SearchAsync(SearchQuery.Trim(), GameVersion.Trim(), loader);
            SearchResults.Clear();
            foreach (ModrinthSearchHit hit in result.Hits)
            {
                SearchResults.Add(hit);
            }

            ModVersions.Clear();
            SelectedSearchResult = SearchResults.FirstOrDefault();
            SearchStatus = SearchResults.Count == 0
                ? $"没有找到兼容 {GameVersion} / {SelectedLoader.Display} 的 MOD。"
                : $"找到 {result.TotalHits} 个结果（显示 {SearchResults.Count} 个，{GameVersion} / {SelectedLoader.Display}）。";
        }
        catch (InvalidOperationException exception)
        {
            _logger.LogWarning(exception, "Modrinth 搜索失败。");
            SearchStatus = "搜索失败：" + exception.Message;
        }
        finally
        {
            IsSearchBusy = false;
        }
    }

    private async Task LoadVersionsAsync(ModrinthSearchHit project)
    {
        try
        {
            string loader = ModrinthClient.GetLoaderName(SelectedLoader.Platform);
            IReadOnlyList<ModrinthVersion> versions = await _modrinth.GetVersionsAsync(project.ProjectId, GameVersion.Trim(), loader);
            ModVersions.Clear();
            foreach (ModrinthVersion version in versions)
            {
                ModVersions.Add(version);
            }

            SelectedModVersion = ModVersions.FirstOrDefault();
            SearchStatus = ModVersions.Count == 0
                ? $"{project.Title} 没有兼容 {GameVersion} / {SelectedLoader.Display} 的版本。"
                : $"{project.Title}：{ModVersions.Count} 个兼容版本。";
        }
        catch (InvalidOperationException exception)
        {
            _logger.LogWarning(exception, "获取 MOD 版本失败。");
            SearchStatus = "获取版本失败：" + exception.Message;
        }
    }

    private async Task InstallAsync()
    {
        GameInstance? instance = SelectedInstance;
        ModrinthVersion? version = SelectedModVersion;
        if (instance is null || version is null)
        {
            SearchStatus = "请先搜索并选择一个 MOD 版本。";
            return;
        }

        IsSearchBusy = true;
        try
        {
            var progress = new Progress<ModrinthDownloadProgress>(value =>
            {
                SearchStatus = value.Percent is double percent
                    ? $"{value.Stage}（{percent:0}%）"
                    : value.Stage;
            });
            string installed = await _modrinth.DownloadAsync(version, _mods.GetModsDirectory(instance), progress);
            await LoadModsAsync();
            SearchStatus = $"已安装 {Path.GetFileName(installed)} 到实例「{instance.Name}」。";
        }
        catch (InvalidOperationException exception)
        {
            _logger.LogWarning(exception, "安装 MOD 失败。");
            SearchStatus = "安装 MOD 失败：" + exception.Message;
        }
        finally
        {
            IsSearchBusy = false;
        }
    }

    private void DetectContext(GameInstance instance)
    {
        string versionId = instance.VersionId;
        string? inherited = ResolveInheritedVersion(versionId);
        GameVersion = inherited ?? ExtractMinecraftVersion(versionId);
        SelectedLoader = DetectLoader(versionId);
    }

    private string? ResolveInheritedVersion(string versionId)
    {
        string current = versionId;
        for (int depth = 0; depth < 8; depth++)
        {
            string path = Path.Combine(_paths.VersionsDirectory, current, current + ".json");
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
                if (document.RootElement.TryGetProperty("inheritsFrom", out JsonElement inherited) && inherited.ValueKind == JsonValueKind.String)
                {
                    current = inherited.GetString() ?? current;
                    if (MinecraftVersionRegex().Match(current) is { Success: true } match && current == match.Value)
                    {
                        return current;
                    }

                    continue;
                }
            }
            catch (Exception exception) when (exception is IOException or JsonException)
            {
                _logger.LogWarning(exception, "读取版本 {VersionId} 的继承信息失败。", current);
            }

            return current;
        }

        return null;
    }

    private LoaderOption DetectLoader(string versionId)
    {
        if (versionId.Contains("neoforge", StringComparison.OrdinalIgnoreCase))
        {
            return LoaderOptions.First(option => option.Platform == ModPlatform.NeoForge);
        }

        if (versionId.Contains("quilt-loader", StringComparison.OrdinalIgnoreCase))
        {
            return LoaderOptions.First(option => option.Platform == ModPlatform.Quilt);
        }

        if (versionId.Contains("fabric-loader", StringComparison.OrdinalIgnoreCase))
        {
            return LoaderOptions.First(option => option.Platform == ModPlatform.Fabric);
        }

        if (versionId.Contains("forge", StringComparison.OrdinalIgnoreCase))
        {
            return LoaderOptions.First(option => option.Platform == ModPlatform.Forge);
        }

        return SelectedLoader;
    }

    private static string ExtractMinecraftVersion(string versionId) =>
        MinecraftVersionRegex().Match(versionId) is { Success: true } match ? match.Value : versionId;

    [GeneratedRegex(@"\d+\.\d+(?:\.\d+)?")]
    private static partial Regex MinecraftVersionRegex();
}

public sealed record LoaderOption(ModPlatform Platform, string Display);
