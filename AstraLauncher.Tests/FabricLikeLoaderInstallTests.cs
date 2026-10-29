using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using AstraLauncher.Core.Models;
using AstraLauncher.Core.Services;
using AstraLauncher.Game.Loaders;
using AstraLauncher.Infrastructure.FileSystem;
using Microsoft.Extensions.Logging.Abstractions;

namespace AstraLauncher.Tests;

public sealed class FabricLikeLoaderInstallTests : IDisposable
{
    private const string MinecraftVersion = "1.21.1";
    private const string FabricLoaderVersion = "0.16.9";
    private const string QuiltLoaderVersion = "0.26.0";
    private const string FabricVersionId = "fabric-loader-0.16.9-1.21.1";
    private const string QuiltVersionId = "quilt-loader-0.26.0-1.21.1";
    private const string FabricProfileUrl = "https://meta.fabricmc.net/v2/versions/loader/1.21.1/0.16.9/profile/json";
    private const string QuiltProfileUrl = "https://meta.quiltmc.org/v3/versions/loader/1.21.1/0.26.0/profile/json";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "AstraLauncher.Tests", Guid.NewGuid().ToString("N"));
    private readonly LauncherPaths _paths;
    private readonly RecordingHandler _handler = new();
    private readonly RecordingInstallService _installService = new();
    private readonly HttpClient _httpClient;
    private readonly FabricLikeLoaderInstaller _installer;

    public FabricLikeLoaderInstallTests()
    {
        _paths = new LauncherPaths(_root, Path.Combine(_root, ".minecraft"));
        _paths.EnsureCreated();
        _httpClient = new HttpClient(_handler) { Timeout = Timeout.InfiniteTimeSpan };
        _installer = new FabricLikeLoaderInstaller(
            _paths,
            _httpClient,
            _installService,
            NullLogger<FabricLikeLoaderInstaller>.Instance);
    }

    [Fact]
    public async Task InstallAsync_Fabric_WritesProfileAndInvokesVersionInstall()
    {
        string profileJson = CreateProfileJson(FabricVersionId, MinecraftVersion);
        byte[] profileBytes = Encoding.UTF8.GetBytes(profileJson);
        _handler.AddJson(FabricProfileUrl, profileJson);

        LoaderInstallResult result = await _installer.InstallAsync(
            LoaderKind.Fabric,
            MinecraftVersion,
            FabricLoaderVersion);

        Assert.Equal(FabricVersionId, result.VersionId);
        Assert.Equal(MinecraftVersion, result.MinecraftVersion);
        Assert.Equal(LoaderKind.Fabric, result.LoaderKind);
        Assert.Equal(FabricLoaderVersion, result.LoaderVersion);
        string profilePath = Path.Combine(_paths.VersionsDirectory, FabricVersionId, $"{FabricVersionId}.json");
        Assert.True(File.Exists(profilePath));
        Assert.Equal(profileBytes, await File.ReadAllBytesAsync(profilePath));
        Assert.Equal(FabricVersionId, Assert.Single(_installService.InstalledVersionIds));
        Assert.Equal(FabricProfileUrl, Assert.Single(_handler.Requests));
    }

    [Fact]
    public async Task InstallAsync_Quilt_WritesProfileAndInvokesVersionInstall()
    {
        string profileJson = CreateProfileJson(QuiltVersionId, MinecraftVersion);
        byte[] profileBytes = Encoding.UTF8.GetBytes(profileJson);
        _handler.AddJson(QuiltProfileUrl, profileJson);

        LoaderInstallResult result = await _installer.InstallAsync(
            LoaderKind.Quilt,
            MinecraftVersion,
            QuiltLoaderVersion);

        Assert.Equal(QuiltVersionId, result.VersionId);
        Assert.Equal(MinecraftVersion, result.MinecraftVersion);
        Assert.Equal(LoaderKind.Quilt, result.LoaderKind);
        Assert.Equal(QuiltLoaderVersion, result.LoaderVersion);
        string profilePath = Path.Combine(_paths.VersionsDirectory, QuiltVersionId, $"{QuiltVersionId}.json");
        Assert.True(File.Exists(profilePath));
        Assert.Equal(profileBytes, await File.ReadAllBytesAsync(profilePath));
        Assert.Equal(QuiltVersionId, Assert.Single(_installService.InstalledVersionIds));
        Assert.Equal(QuiltProfileUrl, Assert.Single(_handler.Requests));
    }

    [Fact]
    public async Task InstallAsync_WhenProfileAlreadyExistsWithDifferentContent_OverwritesIt()
    {
        string profileDirectory = Path.Combine(_paths.VersionsDirectory, FabricVersionId);
        Directory.CreateDirectory(profileDirectory);
        string profilePath = Path.Combine(profileDirectory, $"{FabricVersionId}.json");
        await File.WriteAllTextAsync(profilePath, "stale profile");
        string profileJson = CreateProfileJson(FabricVersionId, MinecraftVersion);
        _handler.AddJson(FabricProfileUrl, profileJson);

        await _installer.InstallAsync(LoaderKind.Fabric, MinecraftVersion, FabricLoaderVersion);

        Assert.Equal(Encoding.UTF8.GetBytes(profileJson), await File.ReadAllBytesAsync(profilePath));
        Assert.Equal(FabricVersionId, Assert.Single(_installService.InstalledVersionIds));
    }

    [Theory]
    [InlineData("missing-id")]
    [InlineData("bad-json")]
    [InlineData("http-500")]
    public async Task InstallAsync_WhenProfileIsInvalid_ThrowsAndDoesNotInstall(string scenario)
    {
        if (string.Equals(scenario, "http-500", StringComparison.Ordinal))
        {
            _handler.AddStatus(FabricProfileUrl, HttpStatusCode.InternalServerError);
        }
        else
        {
            string profileJson = string.Equals(scenario, "missing-id", StringComparison.Ordinal)
                ? """{"inheritsFrom":"1.21.1","libraries":[]}"""
                : "{ not json";
            _handler.AddJson(FabricProfileUrl, profileJson);
        }

        await Assert.ThrowsAsync<InvalidDataException>(
            () => _installer.InstallAsync(LoaderKind.Fabric, MinecraftVersion, FabricLoaderVersion));

        Assert.Empty(_installService.InstalledVersionIds);
        Assert.Empty(Directory.EnumerateFiles(_paths.MinecraftDirectory, "*.json", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task InstallAsync_WhenInheritsFromDiffers_ThrowsInvalidDataException()
    {
        _handler.AddJson(FabricProfileUrl, CreateProfileJson(FabricVersionId, "1.20.1"));

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => _installer.InstallAsync(LoaderKind.Fabric, MinecraftVersion, FabricLoaderVersion));

        Assert.Contains("inheritsFrom", exception.Message, StringComparison.Ordinal);
        Assert.Empty(_installService.InstalledVersionIds);
        Assert.Empty(Directory.EnumerateFiles(_paths.MinecraftDirectory, "*.json", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("..\\escape")]
    [InlineData("..")]
    [InlineData("nested/id")]
    public async Task InstallAsync_WhenProfileIdIsUnsafe_RejectsItWithoutWritingOutsideVersions(string versionId)
    {
        _handler.AddJson(FabricProfileUrl, CreateProfileJson(versionId, MinecraftVersion));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => _installer.InstallAsync(LoaderKind.Fabric, MinecraftVersion, FabricLoaderVersion));

        Assert.Empty(_installService.InstalledVersionIds);
        Assert.Empty(Directory.EnumerateFiles(_paths.MinecraftDirectory, "*.json", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task InstallAsync_ReportsDownloadWriteAndVerifyStagesAndMapsInstallProgress()
    {
        _handler.AddJson(FabricProfileUrl, CreateProfileJson(FabricVersionId, MinecraftVersion));
        var reports = new List<LoaderInstallProgress>();

        await _installer.InstallAsync(
            LoaderKind.Fabric,
            MinecraftVersion,
            FabricLoaderVersion,
            new InlineProgress<LoaderInstallProgress>(reports.Add));

        Assert.Contains(reports, report => report.Stage == "下载加载器配置");
        Assert.Contains(reports, report => report.Stage == "写入版本描述");
        Assert.Contains(reports, report => report.Stage == "校验/补齐加载器文件");
        LoaderInstallProgress verify = Assert.Single(
            reports,
            report => report.Stage == "校验/补齐加载器文件" && report.Percent is not null);
        Assert.Equal(50d, verify.Percent!.Value);
        Assert.Contains("libraries/example.jar", verify.Message, StringComparison.Ordinal);
        Assert.True(_installService.ReceivedProgress);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static string CreateProfileJson(string versionId, string minecraftVersion) => $$"""
        {"id":{{JsonSerializer.Serialize(versionId)}},"inheritsFrom":{{JsonSerializer.Serialize(minecraftVersion)}},"mainClass":"net.fabricmc.loader.impl.launch.knot.KnotClient","libraries":[{"name":"net.fabricmc:fabric-loader:0.16.9","url":"https://maven.fabricmc.net/"}]}
        """;

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string? Json)> _responses = new(StringComparer.Ordinal);
        private readonly List<string> _requests = [];

        public IReadOnlyList<string> Requests => _requests;

        public void AddJson(string url, string json) => _responses[url] = (HttpStatusCode.OK, json);

        public void AddStatus(string url, HttpStatusCode status) => _responses[url] = (status, null);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri?.AbsoluteUri ?? string.Empty;
            _requests.Add(url);
            if (!_responses.TryGetValue(url, out var response))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            HttpResponseMessage message = new(response.Status);
            string? json = response.Json;
            if (json is not null)
            {
                message.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }

            return Task.FromResult(message);
        }
    }

    private sealed class RecordingInstallService : IVersionInstallService
    {
        public List<string> InstalledVersionIds { get; } = [];

        public bool ReceivedProgress { get; private set; }

        public Task<VersionInstallPlan> CreatePlanAsync(string versionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new VersionInstallPlan(versionId, [], 0));

        public Task InstallAsync(
            string versionId,
            IProgress<VersionInstallProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            InstalledVersionIds.Add(versionId);
            ReceivedProgress = progress is not null;
            progress?.Report(new VersionInstallProgress(4, 2, 1000, 500, "下载游戏文件", "libraries/example.jar"));
            return Task.CompletedTask;
        }

        public Task<bool> IsVersionInstalledAsync(string versionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }
}
