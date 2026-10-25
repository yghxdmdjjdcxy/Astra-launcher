using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using AstraLauncher.App.Commands;
using AstraLauncher.App.Services;
using AstraLauncher.Core.Models;
using AstraLauncher.Game.Instances;
using AstraLauncher.Game.Java;
using Microsoft.Extensions.Logging;

namespace AstraLauncher.App.ViewModels;

public sealed class InstancesPageViewModel : PageViewModel
{
    private readonly IGameInstanceRepository _instances;
    private readonly IJavaRuntimeDetector _java;
    private readonly ILogger<InstancesPageViewModel> _logger;
    private readonly IDialogService _dialogs;
    private GameInstance? _selectedInstance;
    private Guid? _selectedInstanceId;
    private string _name = "新实例";
    private string _versionId = "1.21.1";
    private string _importPath = "";
    private string _statusMessage = "正在加载实例…";

    public InstancesPageViewModel(IGameInstanceRepository instances, IJavaRuntimeDetector java, ILogger<InstancesPageViewModel> logger, IDialogService dialogs) : base("游戏实例")
    {
        _instances = instances;
        _java = java;
        _logger = logger;
        _dialogs = dialogs;
        RefreshCommand = new AsyncRelayCommand(() => RunAsync("刷新实例列表", () => RefreshAsync(_selectedInstanceId)));
        CreateCommand = new AsyncRelayCommand(CreateAsync);
        ImportCommand = new AsyncRelayCommand(ImportAsync);
        RenameCommand = new AsyncRelayCommand(RenameAsync);
        DuplicateCommand = new AsyncRelayCommand(DuplicateAsync);
        DeleteCommand = new AsyncRelayCommand(DeleteAsync);
        DetectJavaCommand = new AsyncRelayCommand(DetectJavaAsync);
    }

    public ObservableCollection<GameInstance> Instances { get; } = [];
    public ObservableCollection<JavaRuntimeInfo> JavaRuntimes { get; } = [];

    public GameInstance? SelectedInstance
    {
        get => _selectedInstance;
        set
        {
            if (SetProperty(ref _selectedInstance, value)) _selectedInstanceId = value?.Id;
        }
    }

    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string VersionId { get => _versionId; set => SetProperty(ref _versionId, value); }
    public string ImportPath { get => _importPath; set => SetProperty(ref _importPath, value); }
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }

    public ICommand RefreshCommand { get; }
    public ICommand CreateCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand RenameCommand { get; }
    public ICommand DuplicateCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand DetectJavaCommand { get; }

    public Task InitializeAsync() => RunAsync("加载实例列表", () => RefreshAsync(_selectedInstanceId));

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
        GameInstance imported = await _instances.ImportAsync(Name, VersionId, ImportPath);
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
