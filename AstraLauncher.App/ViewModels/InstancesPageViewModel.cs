using System.Collections.ObjectModel;
using System.Windows.Input;
using AstraLauncher.App.Commands;
using AstraLauncher.Core.Models;
using AstraLauncher.Game.Instances;
using AstraLauncher.Game.Java;

namespace AstraLauncher.App.ViewModels;

public sealed class InstancesPageViewModel : PageViewModel
{
    private readonly IGameInstanceRepository _instances; private readonly IJavaRuntimeDetector _java;
    private GameInstance? _selectedInstance; private string _name = "新实例"; private string _versionId = "1.21.1"; private string _importPath = ""; private string _statusMessage = "正在加载实例…";
    public InstancesPageViewModel(IGameInstanceRepository instances, IJavaRuntimeDetector java) : base("游戏实例")
    {
        _instances = instances; _java = java;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync); CreateCommand = new AsyncRelayCommand(CreateAsync); ImportCommand = new AsyncRelayCommand(ImportAsync); RenameCommand = new AsyncRelayCommand(RenameAsync); DuplicateCommand = new AsyncRelayCommand(DuplicateAsync); DeleteCommand = new AsyncRelayCommand(DeleteAsync); DetectJavaCommand = new AsyncRelayCommand(DetectJavaAsync);
    }
    public ObservableCollection<GameInstance> Instances { get; } = [];
    public ObservableCollection<JavaRuntimeInfo> JavaRuntimes { get; } = [];
    public GameInstance? SelectedInstance { get => _selectedInstance; set => SetProperty(ref _selectedInstance, value); }
    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string VersionId { get => _versionId; set => SetProperty(ref _versionId, value); }
    public string ImportPath { get => _importPath; set => SetProperty(ref _importPath, value); }
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public ICommand RefreshCommand { get; } public ICommand CreateCommand { get; } public ICommand ImportCommand { get; } public ICommand RenameCommand { get; } public ICommand DuplicateCommand { get; } public ICommand DeleteCommand { get; } public ICommand DetectJavaCommand { get; }
    public Task InitializeAsync() => RefreshAsync();
    private async Task RefreshAsync() => await RunAsync(async () => { IReadOnlyList<GameInstance> values = await _instances.ListAsync(); Instances.Clear(); foreach (GameInstance value in values) Instances.Add(value); StatusMessage = $"已加载 {Instances.Count} 个实例。"; });
    private async Task CreateAsync() => await RunAsync(async () => { GameInstance item = await _instances.CreateAsync(Name, VersionId); await RefreshAsync(); SelectedInstance = Instances.Single(value => value.Id == item.Id); StatusMessage = $"已创建实例：{item.Name}"; });
    private async Task ImportAsync() => await RunAsync(async () => { GameInstance item = await _instances.ImportAsync(Name, VersionId, ImportPath); await RefreshAsync(); SelectedInstance = Instances.Single(value => value.Id == item.Id); StatusMessage = $"已导入实例：{item.Name}"; });
    private async Task RenameAsync() => await RunAsync(async () => { if (SelectedInstance is null) throw new InvalidOperationException("请先选择实例。"); await _instances.RenameAsync(SelectedInstance.Id, Name); await RefreshAsync(); StatusMessage = "实例已重命名。"; });
    private async Task DuplicateAsync() => await RunAsync(async () => { if (SelectedInstance is null) throw new InvalidOperationException("请先选择实例。"); GameInstance item = await _instances.DuplicateAsync(SelectedInstance.Id, Name); await RefreshAsync(); SelectedInstance = Instances.Single(value => value.Id == item.Id); StatusMessage = "实例已复制。"; });
    private async Task DeleteAsync() => await RunAsync(async () => { if (SelectedInstance is null) throw new InvalidOperationException("请先选择实例。"); bool managed = SelectedInstance.IsManagedDirectory; await _instances.DeleteAsync(SelectedInstance.Id, managed); await RefreshAsync(); StatusMessage = managed ? "实例及其受管理目录已删除。" : "实例登记已删除；导入目录未被修改。"; });
    private async Task DetectJavaAsync() => await RunAsync(async () => { IReadOnlyList<JavaRuntimeInfo> values = await _java.DetectAsync(); JavaRuntimes.Clear(); foreach (JavaRuntimeInfo value in values) JavaRuntimes.Add(value); StatusMessage = values.Count == 0 ? "未检测到 Java。" : $"检测到 {values.Count} 个 Java 运行时。"; });
    private async Task RunAsync(Func<Task> action) { try { await action(); } catch (Exception exception) { StatusMessage = exception.Message; } }
}
