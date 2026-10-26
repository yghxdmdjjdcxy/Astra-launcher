using System.IO;
using AstraLauncher.App.Services;
using AstraLauncher.App.ViewModels;
using AstraLauncher.Core.Models;
using AstraLauncher.Core.Services;
using AstraLauncher.Game.Java;
using AstraLauncher.Infrastructure.FileSystem;
using Microsoft.Extensions.Logging.Abstractions;

namespace AstraLauncher.Tests;

public sealed class SettingsPageViewModelTests
{
    private const string Java17Path = @"C:\Java\jdk-17\bin\java.exe";
    private const string Java21Path = @"C:\Java\jdk-21\bin\java.exe";

    [Fact]
    public async Task InitializeAsync_LoadsSettingsAndJavaOptions()
    {
        var settings = new FakeSettingsService
        {
            Current = new LauncherSettings
            {
                MemoryMiB = 8192,
                MaxConcurrentDownloads = 6,
                DownloadSourceMode = DownloadSourceMode.Mirror,
                MirrorBaseUrl = "https://mirror.example.test",
                JavaExecutablePath = Java21Path,
                ShowSnapshots = true,
                OfflinePlayerName = "AstraTester",
                GameArguments = "--demo",
                JvmArguments = "-XX:+UseG1GC",
            },
        };
        var java = new FakeJavaRuntimeDetector
        {
            Runtimes =
            [
                new JavaRuntimeInfo(Java17Path, "17.0.12", 17, "Eclipse Adoptium"),
                new JavaRuntimeInfo(Java21Path, "21.0.4", 21, "Microsoft"),
            ],
        };
        var dialogs = new RecordingDialogService();
        SettingsPageViewModel viewModel = CreateViewModel(settings, java, dialogs);

        await viewModel.InitializeAsync();

        Assert.Equal(8192, viewModel.MemoryMiB);
        Assert.Equal(6, viewModel.MaxConcurrentDownloads);
        Assert.Equal(DownloadSourceMode.Mirror, viewModel.SourceMode);
        Assert.Equal("https://mirror.example.test", viewModel.MirrorBaseUrl);
        Assert.True(viewModel.ShowSnapshots);
        Assert.Equal("AstraTester", viewModel.OfflinePlayerName);
        Assert.Equal("--demo", viewModel.GameArguments);
        Assert.Equal("-XX:+UseG1GC", viewModel.JvmArguments);
        Assert.Equal(3, viewModel.JavaOptions.Count);
        Assert.Equal("自动选择（按版本要求）", viewModel.JavaOptions[0].Display);
        Assert.Null(viewModel.JavaOptions[0].ExecutablePath);
        Assert.Equal($"Java 17 · 17.0.12 · {Java17Path}", viewModel.JavaOptions[1].Display);
        Assert.Equal($"Java 21 · 21.0.4 · {Java21Path}", viewModel.JavaOptions[2].Display);
        Assert.Equal(Java21Path, viewModel.SelectedJavaPath);
        Assert.Equal(Java21Path, viewModel.SelectedJavaChoice?.ExecutablePath);
        Assert.False(string.IsNullOrWhiteSpace(viewModel.MinecraftDirectory));
        Assert.False(viewModel.IsBusy);
        Assert.Empty(dialogs.ErrorMessages);
    }

    [Fact]
    public async Task SaveCommand_WritesNormalizedSettings()
    {
        var settings = new FakeSettingsService();
        var java = new FakeJavaRuntimeDetector
        {
            Runtimes =
            [
                new JavaRuntimeInfo(Java17Path, "17.0.12", 17, null),
                new JavaRuntimeInfo(Java21Path, "21.0.4", 21, null),
            ],
        };
        var dialogs = new RecordingDialogService();
        SettingsPageViewModel viewModel = CreateViewModel(settings, java, dialogs);
        await viewModel.InitializeAsync();

        viewModel.MemoryMiB = 64;
        viewModel.MaxConcurrentDownloads = 100;
        viewModel.SourceMode = DownloadSourceMode.Mirror;
        viewModel.MirrorBaseUrl = "   ";
        viewModel.SelectedJavaChoice = viewModel.JavaOptions[0];
        viewModel.ShowSnapshots = true;
        viewModel.OfflinePlayerName = "   ";
        viewModel.GameArguments = "  --demo  ";
        viewModel.JvmArguments = "  -XX:+UseG1GC  ";
        viewModel.MinecraftDirectory = Path.Combine(Path.GetTempPath(), "AstraLauncher.Tests", Guid.NewGuid().ToString("N"), ".minecraft");

        viewModel.SaveCommand.Execute(null);
        await WaitForAsync(() => settings.Saves.Count == 1 && viewModel.SaveCommand.CanExecute(null), "保存设置完成");

        LauncherSettings saved = Assert.Single(settings.Saves);
        Assert.Equal(512, saved.MemoryMiB);
        Assert.Equal(32, saved.MaxConcurrentDownloads);
        Assert.Equal(DownloadSourceMode.Mirror, saved.DownloadSourceMode);
        Assert.Equal(LauncherSettings.DefaultMirrorBaseUrl, saved.MirrorBaseUrl);
        Assert.Null(saved.JavaExecutablePath);
        Assert.True(saved.ShowSnapshots);
        Assert.Equal("Player", saved.OfflinePlayerName);
        Assert.Equal("--demo", saved.GameArguments);
        Assert.Equal("-XX:+UseG1GC", saved.JvmArguments);
        Assert.Equal(viewModel.MinecraftDirectory, saved.MinecraftDirectory);
        Assert.StartsWith("设置已保存。Minecraft 目录：", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(512, viewModel.MemoryMiB);
        Assert.Equal(32, viewModel.MaxConcurrentDownloads);
        Assert.Equal(LauncherSettings.DefaultMirrorBaseUrl, viewModel.MirrorBaseUrl);
        Assert.Equal("Player", viewModel.OfflinePlayerName);
        Assert.Empty(dialogs.ErrorMessages);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task SaveCommand_WhenMinecraftDirectoryIsInvalid_ShowsErrorAndDoesNotPersist()
    {
        var settings = new FakeSettingsService();
        var java = new FakeJavaRuntimeDetector();
        var dialogs = new RecordingDialogService();
        SettingsPageViewModel viewModel = CreateViewModel(settings, java, dialogs);
        await viewModel.InitializeAsync();

        viewModel.MinecraftDirectory = "C:\\invalid\0path";

        viewModel.SaveCommand.Execute(null);
        await WaitForAsync(() => dialogs.ErrorMessages.Count == 1 && viewModel.SaveCommand.CanExecute(null), "无效目录错误处理完成");

        Assert.Contains("保存设置失败", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Empty(settings.Saves);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task SaveCommand_WhenSaveFails_ShowsChineseErrorAndDialog()
    {
        var settings = new FakeSettingsService { SaveFailure = new IOException("磁盘已满。") };
        var java = new FakeJavaRuntimeDetector();
        var dialogs = new RecordingDialogService();
        SettingsPageViewModel viewModel = CreateViewModel(settings, java, dialogs);
        await viewModel.InitializeAsync();

        viewModel.SaveCommand.Execute(null);
        await WaitForAsync(() => dialogs.ErrorMessages.Count == 1 && viewModel.SaveCommand.CanExecute(null), "保存异常处理完成");

        Assert.Contains("保存设置失败", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("磁盘已满", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(viewModel.StatusMessage, Assert.Single(dialogs.ErrorMessages));
        Assert.Empty(settings.Saves);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task RefreshJavaCommand_PreservesSelectionAndFallsBackToAutomatic()
    {
        var java17 = new JavaRuntimeInfo(Java17Path, "17.0.12", 17, null);
        var java21 = new JavaRuntimeInfo(Java21Path, "21.0.4", 21, null);
        var settings = new FakeSettingsService
        {
            Current = new LauncherSettings { JavaExecutablePath = Java17Path },
        };
        var java = new FakeJavaRuntimeDetector { Runtimes = [java17, java21] };
        var dialogs = new RecordingDialogService();
        SettingsPageViewModel viewModel = CreateViewModel(settings, java, dialogs);
        await viewModel.InitializeAsync();
        Assert.Equal(Java17Path, viewModel.SelectedJavaPath);

        java.Runtimes = [java21, java17];
        int previousDetectCalls = java.DetectCalls;
        viewModel.RefreshJavaCommand.Execute(null);
        await WaitForAsync(() => java.DetectCalls > previousDetectCalls && viewModel.RefreshJavaCommand.CanExecute(null), "重新检测 Java 完成");

        Assert.Equal(Java17Path, viewModel.SelectedJavaPath);
        Assert.Equal(3, viewModel.JavaOptions.Count);

        java.Runtimes = [java21];
        previousDetectCalls = java.DetectCalls;
        viewModel.RefreshJavaCommand.Execute(null);
        await WaitForAsync(() => java.DetectCalls > previousDetectCalls && viewModel.RefreshJavaCommand.CanExecute(null), "刷新后回退自动选择");

        Assert.Null(viewModel.SelectedJavaPath);
        Assert.Equal("自动选择（按版本要求）", viewModel.SelectedJavaChoice?.Display);
        Assert.Equal(2, viewModel.JavaOptions.Count);
        Assert.Empty(dialogs.ErrorMessages);
        Assert.False(viewModel.IsBusy);
    }

    private static SettingsPageViewModel CreateViewModel(FakeSettingsService settings, FakeJavaRuntimeDetector java, RecordingDialogService dialogs) =>
        new(settings, java, CreatePaths(), NullLogger<SettingsPageViewModel>.Instance, dialogs);

    private static LauncherPaths CreatePaths() =>
        new(Path.Combine(Path.GetTempPath(), "AstraLauncher.Tests", Guid.NewGuid().ToString("N")));

    private static async Task WaitForAsync(Func<bool> condition, string description)
    {
        for (int attempt = 0; attempt < 500; attempt++)
        {
            if (condition()) return;
            await Task.Delay(10);
        }

        Assert.Fail($"等待超时：{description}。");
    }

    private sealed class FakeSettingsService : ISettingsService
    {
        public LauncherSettings Current { get; set; } = new();

        public List<LauncherSettings> Saves { get; } = [];

        public Exception? SaveFailure { get; set; }

        public event EventHandler<LauncherSettings>? Changed
        {
            add { }
            remove { }
        }

        public Task<LauncherSettings> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(Current);

        public Task SaveAsync(LauncherSettings settings, CancellationToken cancellationToken = default)
        {
            Exception? failure = SaveFailure;
            if (failure is not null)
            {
                return Task.FromException(failure);
            }

            Saves.Add(settings);
            Current = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeJavaRuntimeDetector : IJavaRuntimeDetector
    {
        public IReadOnlyList<JavaRuntimeInfo> Runtimes { get; set; } = [];

        public int DetectCalls { get; private set; }

        public Task<IReadOnlyList<JavaRuntimeInfo>> DetectAsync(CancellationToken cancellationToken = default)
        {
            DetectCalls++;
            return Task.FromResult(Runtimes);
        }

        public Task<JavaRuntimeInfo?> InspectAsync(string executablePath, CancellationToken cancellationToken = default) =>
            Task.FromResult<JavaRuntimeInfo?>(null);
    }

    private sealed class RecordingDialogService : IDialogService
    {
        public List<string> ErrorMessages { get; } = [];

        public bool Confirm(string message, string title = "Astra Launcher") => true;

        public void ShowError(string message, string title = "Astra Launcher") => ErrorMessages.Add(message);
    }
}
