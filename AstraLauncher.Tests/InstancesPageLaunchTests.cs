using AstraLauncher.App.Services;
using AstraLauncher.App.ViewModels;
using AstraLauncher.Core.Models;
using AstraLauncher.Core.Services;
using AstraLauncher.Game.Instances;
using AstraLauncher.Game.Java;
using AstraLauncher.Game.Launch;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO;

namespace AstraLauncher.Tests;

public sealed class InstancesPageLaunchTests
{
    [Fact]
    public async Task LaunchCommand_StartsOfflineSessionAndReportsExit()
    {
        FakeSettingsService settings = new() { Settings = new LauncherSettings { MemoryMiB = 2048, OfflinePlayerName = "TestPlayer" } };
        FakeLaunchService launch = new();
        InstancesPageViewModel viewModel = CreateViewModel(settings, launch);
        viewModel.SelectedInstance = new GameInstance(Guid.NewGuid(), "Test", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), "1.21.1", true);

        viewModel.LaunchCommand.Execute(null);
        await WaitForAsync(() => viewModel.StatusMessage.Contains("游戏已退出", StringComparison.Ordinal), "游戏退出状态");

        Assert.Equal(1, launch.Calls);
        Assert.NotNull(launch.Session);
        Assert.Equal(GameSessionKind.Offline, launch.Session!.Kind);
        Assert.Equal("TestPlayer", launch.Session.PlayerName);
        Assert.False(string.IsNullOrWhiteSpace(launch.Session.PlayerUuid));
        Assert.Equal(2048, launch.Options!.MemoryMiB);
        Assert.False(viewModel.IsLaunching);
        Assert.Contains(viewModel.GameLogs, line => line.Contains("启动实例", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LaunchCommand_WithoutSelection_ShowsHint()
    {
        InstancesPageViewModel viewModel = CreateViewModel(new FakeSettingsService(), new FakeLaunchService());

        viewModel.LaunchCommand.Execute(null);
        await WaitForAsync(() => viewModel.StatusMessage.Contains("请先选择", StringComparison.Ordinal), "选择提示");

        Assert.Contains("请先选择", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    private static InstancesPageViewModel CreateViewModel(ISettingsService settings, IGameLaunchService launch) =>
        new(new EmptyRepository(), new EmptyJavaDetector(), NullLogger<InstancesPageViewModel>.Instance, new NoopDialogService(), settings, launch);

    private static async Task WaitForAsync(Func<bool> condition, string description)
    {
        for (int attempt = 0; attempt < 600; attempt++)
        {
            if (condition()) return;
            await Task.Delay(10);
        }

        throw new TimeoutException($"Timed out waiting for: {description}");
    }

    private sealed class FakeSettingsService : ISettingsService
    {
        public LauncherSettings Settings { get; set; } = new();

        public event EventHandler<LauncherSettings>? Changed;

        public Task<LauncherSettings> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(Settings);

        public Task SaveAsync(LauncherSettings settings, CancellationToken cancellationToken = default)
        {
            Settings = settings;
            Changed?.Invoke(this, settings);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeLaunchService : IGameLaunchService
    {
        public int Calls { get; private set; }

        public GameSession? Session { get; private set; }

        public GameLaunchOptions? Options { get; private set; }

        public Task<IGameProcess> LaunchAsync(GameInstance instance, GameSession session, GameLaunchOptions options, IProgress<LaunchProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            Session = session;
            Options = options;
            progress?.Report(new LaunchProgress("检查游戏文件", "0/1 文件", 0));
            progress?.Report(new LaunchProgress("启动游戏", "游戏进程已启动", 100));
            return Task.FromResult<IGameProcess>(new FakeProcess());
        }
    }

    private sealed class FakeProcess : IGameProcess
    {
        public int Id => 4242;

        public bool HasExited => false;

        public IReadOnlyList<string> RecentOutput => [];

        public event EventHandler<string>? OutputReceived;

        public Task<GameExitInfo> Exited => ExitAsync();

        public void Kill()
        {
        }

        private async Task<GameExitInfo> ExitAsync()
        {
            await Task.Yield();
            OutputReceived?.Invoke(this, "game output line");
            DateTimeOffset now = DateTimeOffset.UtcNow;
            return new GameExitInfo(0, now, now);
        }
    }

    private sealed class EmptyRepository : IGameInstanceRepository
    {
        public Task<IReadOnlyList<GameInstance>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<GameInstance>>([]);

        public Task<GameInstance> CreateAsync(string name, string versionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<GameInstance> ImportAsync(string name, string versionId, string directoryPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<GameInstance> RenameAsync(Guid id, string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<GameInstance> DuplicateAsync(Guid id, string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeleteAsync(Guid id, bool deleteManagedDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class EmptyJavaDetector : IJavaRuntimeDetector
    {
        public Task<IReadOnlyList<JavaRuntimeInfo>> DetectAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<JavaRuntimeInfo>>([]);

        public Task<JavaRuntimeInfo?> InspectAsync(string executablePath, CancellationToken cancellationToken = default) => Task.FromResult<JavaRuntimeInfo?>(null);
    }

    private sealed class NoopDialogService : IDialogService
    {
        public bool Confirm(string message, string title = "Astra Launcher") => true;

        public void ShowError(string message, string title = "Astra Launcher")
        {
        }
    }
}
