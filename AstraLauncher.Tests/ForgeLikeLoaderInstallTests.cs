using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using AstraLauncher.Core.Models;
using AstraLauncher.Core.Services;
using AstraLauncher.Game.Java;
using AstraLauncher.Game.Loaders;
using AstraLauncher.Infrastructure.FileSystem;
using Microsoft.Extensions.Logging.Abstractions;

namespace AstraLauncher.Tests;

public sealed class ForgeLikeLoaderInstallTests : IAsyncLifetime
{
    private const string MinecraftVersion = "1.21.1";
    private const string ForgeLoaderVersion = "52.0.14";
    private const string NeoForgeLoaderVersion = "21.1.80";
    private const string ForgeInstallerUrl = "https://maven.minecraftforge.net/net/minecraftforge/forge/1.21.1-52.0.14/forge-1.21.1-52.0.14-installer.jar";
    private const string NeoForgeInstallerUrl = "https://maven.neoforged.net/releases/net/neoforged/neoforge/21.1.80/neoforge-21.1.80-installer.jar";
    private const string DownloadStage = "下载官方安装器";
    private const string RunStage = "运行官方安装器";
    private const string VerifyStage = "校验/补齐加载器文件";
    private const string Java17Path = @"C:\Java\jdk-17\bin\java.exe";
    private const string Java21Path = @"C:\Java\jdk-21\bin\java.exe";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "AstraLauncher.Tests", Guid.NewGuid().ToString("N"));
    private readonly LauncherPaths _paths;
    private readonly RecordingHttpMessageHandler _httpHandler = new(Encoding.UTF8.GetBytes("fake-installer-payload"));
    private readonly HttpClient _httpClient;
    private readonly FakeJavaRuntimeDetector _java = new();
    private readonly FakeExternalProcessRunner _runner = new();
    private readonly FakeVersionInstallService _installService = new();
    private readonly ForgeLikeLoaderInstaller _installer;

    public ForgeLikeLoaderInstallTests()
    {
        _paths = new LauncherPaths(_root, Path.Combine(_root, ".minecraft"));
        _paths.EnsureCreated();
        _httpClient = new HttpClient(_httpHandler) { Timeout = Timeout.InfiniteTimeSpan };
        _java.Runtimes =
        [
            new JavaRuntimeInfo(Java17Path, "17.0.12", 17, "Eclipse Adoptium"),
            new JavaRuntimeInfo(Java21Path, "21.0.4", 21, "Microsoft"),
        ];
        _installer = new ForgeLikeLoaderInstaller(
            _paths,
            _httpClient,
            _java,
            _runner,
            _installService,
            NullLogger<ForgeLikeLoaderInstaller>.Instance);
    }

    [Fact]
    public async Task InstallAsync_Forge_DownloadsRunsInstallerAndCallsVersionInstallService()
    {
        DateTime oldTimestamp = DateTime.UtcNow.AddMinutes(-10);
        _runner.BeforeExit = () =>
        {
            CreateProfile("1.21.1-forge-52.0.13", MinecraftVersion, oldTimestamp);
            CreateProfile("1.21.1-forge-52.0.14", MinecraftVersion, DateTime.UtcNow);
            CreateProfile("1.20.1-forge-52.0.14", "1.20.1", DateTime.UtcNow.AddMinutes(10));
        };
        var reports = new List<LoaderInstallProgress>();

        LoaderInstallResult result = await _installer.InstallAsync(
            LoaderKind.Forge,
            MinecraftVersion,
            ForgeLoaderVersion,
            new InlineProgress<LoaderInstallProgress>(reports.Add));

        Uri requestedUri = Assert.Single(_httpHandler.Requests);
        Assert.Equal(ForgeInstallerUrl, requestedUri.AbsoluteUri);

        string expectedInstallerPath = Path.Combine(_paths.TempDirectory, "loader-installers", "forge-1.21.1-52.0.14-installer.jar");
        Assert.True(File.Exists(expectedInstallerPath), $"Expected installer at '{expectedInstallerPath}'.");

        Assert.Equal(Java21Path, _runner.ExecutablePath);
        Assert.Equal(4, _runner.Arguments.Count);
        Assert.Equal("-jar", _runner.Arguments[0]);
        Assert.Equal(expectedInstallerPath, _runner.Arguments[1]);
        Assert.Equal("--installClient", _runner.Arguments[2]);
        Assert.Equal(_paths.MinecraftDirectory, _runner.Arguments[3]);
        Assert.Equal(_paths.MinecraftDirectory, _runner.WorkingDirectory);

        Assert.Equal(1, _installService.InstallCalls);
        Assert.Equal("1.21.1-forge-52.0.14", _installService.LastVersionId);
        Assert.Equal(
            new LoaderInstallResult("1.21.1-forge-52.0.14", MinecraftVersion, LoaderKind.Forge, ForgeLoaderVersion),
            result);

        List<string> stages = [.. reports.Select(report => report.Stage)];
        Assert.Contains(DownloadStage, stages);
        Assert.Contains(RunStage, stages);
        Assert.Contains(VerifyStage, stages);
        Assert.True(stages.IndexOf(DownloadStage) < stages.IndexOf(RunStage));
        Assert.True(stages.IndexOf(RunStage) < stages.IndexOf(VerifyStage));
        Assert.Contains(reports, report => report.Stage == VerifyStage && report.Message.Contains("libraries/foo.jar", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InstallAsync_NeoForge_UsesNeoForgeMavenAndDetectsNeoForgeProfile()
    {
        _runner.BeforeExit = () => CreateProfile("neoforge-21.1.80", MinecraftVersion, DateTime.UtcNow);

        LoaderInstallResult result = await _installer.InstallAsync(LoaderKind.NeoForge, MinecraftVersion, NeoForgeLoaderVersion);

        Uri requestedUri = Assert.Single(_httpHandler.Requests);
        Assert.Equal(NeoForgeInstallerUrl, requestedUri.AbsoluteUri);

        string expectedInstallerPath = Path.Combine(_paths.TempDirectory, "loader-installers", "neoforge-21.1.80-installer.jar");
        Assert.Equal(expectedInstallerPath, _runner.Arguments[1]);
        Assert.Equal("neoforge-21.1.80", _installService.LastVersionId);
        Assert.Equal(LoaderKind.NeoForge, result.LoaderKind);
        Assert.Equal(NeoForgeLoaderVersion, result.LoaderVersion);
        Assert.Equal(MinecraftVersion, result.MinecraftVersion);
    }

    [Fact]
    public async Task InstallAsync_WhenOnlyJava17IsAvailable_UsesJava17()
    {
        _java.Runtimes = [new JavaRuntimeInfo(Java17Path, "17.0.12", 17, "Eclipse Adoptium")];
        _runner.BeforeExit = () => CreateProfile("1.21.1-forge-52.0.14", MinecraftVersion, DateTime.UtcNow);

        await _installer.InstallAsync(LoaderKind.Forge, MinecraftVersion, ForgeLoaderVersion);

        Assert.Equal(Java17Path, _runner.ExecutablePath);
    }

    [Fact]
    public async Task InstallAsync_WhenInstallerExitsWithNonZeroCode_ThrowsAndSkipsVerification()
    {
        _runner.ExitCode = 3;
        _runner.BeforeExit = () => CreateProfile("1.21.1-forge-52.0.14", MinecraftVersion, DateTime.UtcNow);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _installer.InstallAsync(LoaderKind.Forge, MinecraftVersion, ForgeLoaderVersion));

        Assert.Contains("退出码为 3", exception.Message, StringComparison.Ordinal);
        Assert.Contains("[installer] extracting files", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, _installService.InstallCalls);
    }

    [Fact]
    public async Task InstallAsync_WhenNoMatchingProfileExists_Throws()
    {
        _runner.BeforeExit = () => CreateProfile("1.20.1-forge-52.0.14", "1.20.1", DateTime.UtcNow);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _installer.InstallAsync(LoaderKind.Forge, MinecraftVersion, ForgeLoaderVersion));

        Assert.Contains("未找到", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, _installService.InstallCalls);
    }

    [Fact]
    public async Task InstallAsync_WhenNoJava17OrNewer_ThrowsBeforeDownloading()
    {
        _java.Runtimes = [];

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _installer.InstallAsync(LoaderKind.Forge, MinecraftVersion, ForgeLoaderVersion));

        Assert.Contains("Java", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, _java.DetectCalls);
        Assert.Equal(0, _httpHandler.RequestCount);
        Assert.Equal(0, _runner.RunCalls);
        Assert.Equal(0, _installService.InstallCalls);
    }

    [Theory]
    [InlineData("", ForgeLoaderVersion)]
    [InlineData("..", ForgeLoaderVersion)]
    [InlineData("../evil", ForgeLoaderVersion)]
    [InlineData(MinecraftVersion, "..\\evil")]
    [InlineData(MinecraftVersion, "52/0.14")]
    [InlineData(MinecraftVersion, "52.0.14..")]
    public async Task InstallAsync_RejectsUnsafeVersionArguments(string minecraftVersion, string loaderVersion)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _installer.InstallAsync(LoaderKind.Forge, minecraftVersion, loaderVersion));

        Assert.Equal(0, _java.DetectCalls);
        Assert.Equal(0, _httpHandler.RequestCount);
        Assert.Equal(0, _runner.RunCalls);
        Assert.Equal(0, _installService.InstallCalls);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        _httpClient.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        return Task.CompletedTask;
    }

    private void CreateProfile(string versionId, string inheritsFrom, DateTime? lastWriteTimeUtc = null)
    {
        string directory = Path.Combine(_paths.VersionsDirectory, versionId);
        Directory.CreateDirectory(directory);
        string jsonPath = Path.Combine(directory, $"{versionId}.json");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(new { id = versionId, inheritsFrom }));
        if (lastWriteTimeUtc is DateTime timestamp)
        {
            File.SetLastWriteTimeUtc(jsonPath, timestamp);
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class RecordingHttpMessageHandler(byte[] payload) : HttpMessageHandler
    {
        private readonly List<Uri> _requests = [];

        public IReadOnlyList<Uri> Requests
        {
            get
            {
                lock (_requests)
                {
                    return [.. _requests];
                }
            }
        }

        public int RequestCount
        {
            get
            {
                lock (_requests)
                {
                    return _requests.Count;
                }
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_requests)
            {
                if (request.RequestUri is not null)
                {
                    _requests.Add(request.RequestUri);
                }
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
            });
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

    private sealed class FakeExternalProcessRunner : IExternalProcessRunner
    {
        public string? ExecutablePath { get; private set; }

        public IReadOnlyList<string> Arguments { get; private set; } = [];

        public string? WorkingDirectory { get; private set; }

        public int ExitCode { get; set; }

        public int RunCalls { get; private set; }

        public Action? BeforeExit { get; set; }

        public Task<int> RunAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            Action<string>? onOutput,
            CancellationToken cancellationToken = default)
        {
            RunCalls++;
            ExecutablePath = executablePath;
            Arguments = [.. arguments];
            WorkingDirectory = workingDirectory;
            onOutput?.Invoke("[installer] extracting files");
            BeforeExit?.Invoke();
            return Task.FromResult(ExitCode);
        }
    }

    private sealed class FakeVersionInstallService : IVersionInstallService
    {
        public int InstallCalls { get; private set; }

        public string? LastVersionId { get; private set; }

        public Task<VersionInstallPlan> CreatePlanAsync(string versionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task InstallAsync(string versionId, IProgress<VersionInstallProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            InstallCalls++;
            LastVersionId = versionId;
            progress?.Report(new VersionInstallProgress(10, 5, 1000, 500, "下载游戏文件", "libraries/foo.jar"));
            return Task.CompletedTask;
        }

        public Task<bool> IsVersionInstalledAsync(string versionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }
}
