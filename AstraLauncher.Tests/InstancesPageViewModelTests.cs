using AstraLauncher.App.Services;
using AstraLauncher.App.ViewModels;
using AstraLauncher.Core.Models;
using AstraLauncher.Game.Instances;
using AstraLauncher.Game.Java;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO;

namespace AstraLauncher.Tests;

public sealed class InstancesPageViewModelTests
{
    [Fact]
    public async Task DeleteManagedInstance_AsksForConfirmation_AndDeletesAfterConfirmation()
    {
        GameInstance managed = Instance("生存实例", managed: true);
        var repository = new FakeGameInstanceRepository();
        repository.Instances.Add(managed);
        var dialogs = new RecordingDialogService { ConfirmResult = true };
        InstancesPageViewModel viewModel = CreateViewModel(repository, dialogs);
        await viewModel.InitializeAsync();
        viewModel.SelectedInstance = viewModel.Instances.Single();

        viewModel.DeleteCommand.Execute(null);
        await WaitForAsync(() => repository.DeleteCalls.Count == 1 && viewModel.DeleteCommand.CanExecute(null), "受管实例删除完成");

        Assert.Single(repository.DeleteCalls);
        (Guid Id, bool DeleteManagedDirectory) deleteCall = repository.DeleteCalls[0];
        Assert.Equal(managed.Id, deleteCall.Id);
        Assert.True(deleteCall.DeleteManagedDirectory);
        string confirmation = Assert.Single(dialogs.ConfirmMessages);
        Assert.Contains("及其目录", confirmation, StringComparison.Ordinal);
        Assert.Contains("不可恢复", confirmation, StringComparison.Ordinal);
        Assert.Empty(viewModel.Instances);
        Assert.Null(viewModel.SelectedInstance);
        Assert.Empty(dialogs.ErrorMessages);
    }

    [Fact]
    public async Task DeleteInstance_WhenConfirmationIsCancelled_KeepsInstanceAndSkipsRepository()
    {
        GameInstance managed = Instance("待删除实例", managed: true);
        var repository = new FakeGameInstanceRepository();
        repository.Instances.Add(managed);
        var dialogs = new RecordingDialogService { ConfirmResult = false };
        InstancesPageViewModel viewModel = CreateViewModel(repository, dialogs);
        await viewModel.InitializeAsync();
        GameInstance selected = viewModel.Instances.Single();
        viewModel.SelectedInstance = selected;

        viewModel.DeleteCommand.Execute(null);
        await WaitForAsync(() => dialogs.ConfirmMessages.Count == 1 && viewModel.DeleteCommand.CanExecute(null), "删除确认被取消");

        Assert.Empty(repository.DeleteCalls);
        Assert.Same(selected, Assert.Single(viewModel.Instances));
        Assert.Same(selected, viewModel.SelectedInstance);
        Assert.Empty(dialogs.ErrorMessages);
    }

    [Fact]
    public async Task DeleteImportedInstance_ConfirmsRegistrationOnly_AndNeverDeletesDirectory()
    {
        GameInstance imported = Instance("导入实例", managed: false);
        var repository = new FakeGameInstanceRepository();
        repository.Instances.Add(imported);
        var dialogs = new RecordingDialogService { ConfirmResult = true };
        InstancesPageViewModel viewModel = CreateViewModel(repository, dialogs);
        await viewModel.InitializeAsync();
        viewModel.SelectedInstance = viewModel.Instances.Single();

        viewModel.DeleteCommand.Execute(null);
        await WaitForAsync(() => repository.DeleteCalls.Count == 1 && viewModel.DeleteCommand.CanExecute(null), "导入实例登记删除完成");

        (Guid Id, bool DeleteManagedDirectory) deleteCall = Assert.Single(repository.DeleteCalls);
        Assert.Equal(imported.Id, deleteCall.Id);
        Assert.False(deleteCall.DeleteManagedDirectory);
        string confirmation = Assert.Single(dialogs.ConfirmMessages);
        Assert.Contains("仅删除", confirmation, StringComparison.Ordinal);
        Assert.Contains("登记", confirmation, StringComparison.Ordinal);
        Assert.Contains("不会修改原目录", confirmation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateWithBlankName_ShowsLocalizedValidationMessage()
    {
        var repository = new FakeGameInstanceRepository();
        InstancesPageViewModel viewModel = CreateViewModel(repository, new RecordingDialogService());
        await viewModel.InitializeAsync();

        viewModel.Name = "   ";
        viewModel.CreateCommand.Execute(null);
        await WaitForAsync(() => viewModel.StatusMessage.StartsWith("输入无效：", StringComparison.Ordinal) && viewModel.CreateCommand.CanExecute(null), "本地化输入错误提示");

        Assert.StartsWith("输入无效：", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Empty(repository.Instances);
    }

    [Fact]
    public async Task Refresh_ReselectsInstanceById()
    {
        var repository = new FakeGameInstanceRepository { ReturnCopies = true };
        repository.Instances.Add(Instance("第一个实例", managed: true));
        repository.Instances.Add(Instance("第二个实例", managed: true));
        InstancesPageViewModel viewModel = CreateViewModel(repository, new RecordingDialogService());
        await viewModel.InitializeAsync();
        GameInstance previouslySelected = viewModel.Instances.Single(value => string.Equals(value.Name, "第二个实例", StringComparison.Ordinal));
        Guid selectedId = previouslySelected.Id;
        viewModel.SelectedInstance = previouslySelected;

        viewModel.RefreshCommand.Execute(null);
        await WaitForAsync(() => repository.ListCalls >= 2 && viewModel.RefreshCommand.CanExecute(null), "刷新完成");

        GameInstance? selectedAfterRefresh = viewModel.SelectedInstance;
        Assert.NotNull(selectedAfterRefresh);
        Assert.Equal(selectedId, selectedAfterRefresh!.Id);
        Assert.Same(viewModel.Instances.Single(value => value.Id == selectedId), selectedAfterRefresh);
        Assert.NotSame(previouslySelected, selectedAfterRefresh);
    }

    private static InstancesPageViewModel CreateViewModel(FakeGameInstanceRepository repository, RecordingDialogService dialogs) =>
        new(repository, new FakeJavaRuntimeDetector(), NullLogger<InstancesPageViewModel>.Instance, dialogs);

    private static GameInstance Instance(string name, bool managed) =>
        new(Guid.NewGuid(), name, Path.Combine(Path.GetTempPath(), "AstraLauncher.Tests", Guid.NewGuid().ToString("N")), "1.21.1", managed);

    private static async Task WaitForAsync(Func<bool> condition, string description)
    {
        for (int attempt = 0; attempt < 500; attempt++)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        Assert.Fail($"等待超时：{description}。");
    }

    private sealed class FakeGameInstanceRepository : IGameInstanceRepository
    {
        public List<GameInstance> Instances { get; } = [];
        public List<(Guid Id, bool DeleteManagedDirectory)> DeleteCalls { get; } = [];
        public int ListCalls { get; private set; }
        public bool ReturnCopies { get; set; }

        public Task<IReadOnlyList<GameInstance>> ListAsync(CancellationToken cancellationToken = default)
        {
            ListCalls++;
            IReadOnlyList<GameInstance> snapshot = ReturnCopies
                ? Instances.Select(value => new GameInstance(value.Id, value.Name, value.DirectoryPath, value.VersionId, value.IsManagedDirectory)).ToArray()
                : Instances.ToArray();
            return Task.FromResult(snapshot);
        }

        public Task<GameInstance> CreateAsync(string name, string versionId, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentException.ThrowIfNullOrWhiteSpace(versionId);
            GameInstance created = new(Guid.NewGuid(), name.Trim(), NewDirectory(), versionId.Trim(), true);
            Instances.Add(created);
            return Task.FromResult(created);
        }

        public Task<GameInstance> ImportAsync(string name, string versionId, string directoryPath, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentException.ThrowIfNullOrWhiteSpace(versionId);
            ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
            GameInstance imported = new(Guid.NewGuid(), name.Trim(), Path.GetFullPath(directoryPath), versionId.Trim(), false);
            Instances.Add(imported);
            return Task.FromResult(imported);
        }

        public Task<GameInstance> RenameAsync(Guid id, string name, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            GameInstance existing = Instances.Single(value => value.Id == id);
            GameInstance renamed = existing with { Name = name.Trim() };
            Instances[Instances.IndexOf(existing)] = renamed;
            return Task.FromResult(renamed);
        }

        public Task<GameInstance> DuplicateAsync(Guid id, string name, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            GameInstance source = Instances.Single(value => value.Id == id);
            GameInstance duplicate = new(Guid.NewGuid(), name.Trim(), NewDirectory(), source.VersionId, true);
            Instances.Add(duplicate);
            return Task.FromResult(duplicate);
        }

        public Task DeleteAsync(Guid id, bool deleteManagedDirectory, CancellationToken cancellationToken = default)
        {
            DeleteCalls.Add((id, deleteManagedDirectory));
            Instances.RemoveAll(value => value.Id == id);
            return Task.CompletedTask;
        }

        private static string NewDirectory() => Path.Combine(Path.GetTempPath(), "AstraLauncher.Tests", Guid.NewGuid().ToString("N"));
    }

    private sealed class FakeJavaRuntimeDetector : IJavaRuntimeDetector
    {
        public Task<IReadOnlyList<JavaRuntimeInfo>> DetectAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<JavaRuntimeInfo>>([]);

        public Task<JavaRuntimeInfo?> InspectAsync(string executablePath, CancellationToken cancellationToken = default) => Task.FromResult<JavaRuntimeInfo?>(null);
    }

    private sealed class RecordingDialogService : IDialogService
    {
        public bool ConfirmResult { get; set; } = true;
        public List<string> ConfirmMessages { get; } = [];
        public List<string> ErrorMessages { get; } = [];

        public bool Confirm(string message, string title = "Astra Launcher")
        {
            ConfirmMessages.Add(message);
            return ConfirmResult;
        }

        public void ShowError(string message, string title = "Astra Launcher") => ErrorMessages.Add(message);
    }
}
