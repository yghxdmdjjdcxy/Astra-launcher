using System.IO;
using AstraLauncher.Core.Models;
using AstraLauncher.Core.Services;
using AstraLauncher.Game.Diagnostics;
using AstraLauncher.Game.Mods;
using Microsoft.Extensions.Logging.Abstractions;

namespace AstraLauncher.Tests;

public sealed class CrashRepairServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AstraLauncher.Tests", Guid.NewGuid().ToString("N"));
    private readonly GameInstance _instance;

    public CrashRepairServiceTests()
    {
        _instance = new GameInstance(Guid.NewGuid(), "Repair Test", _root, "fabric-loader-0.16.9-1.21.1", true);
        Directory.CreateDirectory(Path.Combine(_root, "mods"));
    }

    [Fact]
    public async Task RepairAsync_InstallsMissingDependencyThroughModrinth()
    {
        var settings = new FakeSettingsService();
        var modrinth = new FakeModrinthClient();
        var service = new CrashRepairService(
            new LocalModService(NullLogger<LocalModService>.Instance),
            modrinth,
            settings,
            new FakePaths(_root),
            NullLogger<CrashRepairService>.Instance);
        var analysis = new CrashAnalysisResult(
            [new CrashIssue(CrashIssueKind.MissingDependency, "缺少前置 MOD：create", "detail", "suggestion", ModId: "create", CanAutoFix: true)],
            "缺少前置 MOD",
            null,
            null,
            string.Empty);

        CrashRepairResult result = await service.RepairAsync(_instance, analysis);

        CrashRepairAction action = Assert.Single(result.Actions);
        Assert.True(action.Success, action.Detail);
        Assert.Equal(1, modrinth.DownloadCalls);
        Assert.True(result.Changed);
    }

    [Fact]
    public async Task RepairAsync_DisablesDuplicateMods()
    {
        string first = Path.Combine(_root, "mods", "duplicate-1.jar");
        LocalModServiceTests.CreateJar(first, new Dictionary<string, string> { ["fabric.mod.json"] = """{"id":"duplicatemod","version":"1.0"}""" });
        string second = Path.Combine(_root, "mods", "duplicate-2.jar");
        LocalModServiceTests.CreateJar(second, new Dictionary<string, string> { ["fabric.mod.json"] = """{"id":"duplicatemod","version":"2.0"}""" });
        var service = new CrashRepairService(
            new LocalModService(NullLogger<LocalModService>.Instance),
            new FakeModrinthClient(),
            new FakeSettingsService(),
            new FakePaths(_root),
            NullLogger<CrashRepairService>.Instance);
        var analysis = new CrashAnalysisResult(
            [new CrashIssue(CrashIssueKind.DuplicateMods, "存在重复的 MOD", "detail", "suggestion", CanAutoFix: true)],
            "存在重复的 MOD",
            null,
            null,
            string.Empty);

        CrashRepairResult result = await service.RepairAsync(_instance, analysis);

        Assert.Contains(result.Actions, action => action.Success && action.Description.Contains("重复"));
        IReadOnlyList<LocalMod> mods = await new LocalModService(NullLogger<LocalModService>.Instance).ListAsync(_instance);
        Assert.Single(mods, mod => mod.IsEnabled);
        Assert.Single(mods, mod => !mod.IsEnabled);
    }

    [Fact]
    public async Task RepairAsync_OutOfMemory_IncreasesMemorySetting()
    {
        var settings = new FakeSettingsService();
        var service = new CrashRepairService(
            new LocalModService(NullLogger<LocalModService>.Instance),
            new FakeModrinthClient(),
            settings,
            new FakePaths(_root),
            NullLogger<CrashRepairService>.Instance);
        var analysis = new CrashAnalysisResult(
            [new CrashIssue(CrashIssueKind.OutOfMemory, "内存不足", "detail", "suggestion", CanAutoFix: true)],
            "内存不足",
            null,
            null,
            string.Empty);

        CrashRepairResult result = await service.RepairAsync(_instance, analysis);

        Assert.True(Assert.Single(result.Actions).Success);
        Assert.Equal(LauncherSettings.DefaultMemoryMiB + 1024, settings.Settings.MemoryMiB);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FakeSettingsService : ISettingsService
    {
        public LauncherSettings Settings { get; } = new();
        public event EventHandler<LauncherSettings>? Changed { add { } remove { } }
        public Task<LauncherSettings> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(Settings);
        public Task SaveAsync(LauncherSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakePaths : ILauncherPaths
    {
        private readonly string _root;
        public FakePaths(string root) => _root = root;
        public string ApplicationDataDirectory => _root;
        public string LogsDirectory => Path.Combine(_root, "logs");
        public string InstancesDirectory => Path.Combine(_root, "instances");
        public string MinecraftDirectory => _root;
        public string VersionsDirectory => Path.Combine(_root, "versions");
        public string LibrariesDirectory => Path.Combine(_root, "libraries");
        public string AssetsDirectory => Path.Combine(_root, "assets");
        public string RuntimeDirectory => Path.Combine(_root, "runtime");
        public string CacheDirectory => Path.Combine(_root, "cache");
        public string TempDirectory => Path.Combine(_root, "temp");
        public void SetMinecraftDirectory(string path) { }
        public void EnsureCreated() => Directory.CreateDirectory(_root);
    }

    private sealed class FakeModrinthClient : IModrinthClient
    {
        public int DownloadCalls { get; private set; }
        public Task<ModrinthSearchResult> SearchAsync(string query, string gameVersion, string loader, int offset = 0, int limit = 20, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ModrinthSearchResult([new ModrinthSearchHit("create", "create", "Create", "Tech", null, 1, [])], 1));
        public Task<IReadOnlyList<ModrinthVersion>> GetVersionsAsync(string projectId, string gameVersion, string loader, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ModrinthVersion>>([
                new ModrinthVersion("v1", "create", "Create 1.0", "1.0", [gameVersion], [loader], [new ModrinthVersionFile("create.jar", new Uri("https://example.test/create.jar"), 1, "AA", true)]),
            ]);
        public Task<string> DownloadAsync(ModrinthVersion version, string modsDirectory, IProgress<ModrinthDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            DownloadCalls++;
            Directory.CreateDirectory(modsDirectory);
            string path = Path.Combine(modsDirectory, "create.jar");
            File.WriteAllText(path, "jar");
            return Task.FromResult(path);
        }
    }
}
