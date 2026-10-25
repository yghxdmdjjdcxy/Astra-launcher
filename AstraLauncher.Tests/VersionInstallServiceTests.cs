using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using AstraLauncher.Core.Models;
using AstraLauncher.Core.Services;
using AstraLauncher.Downloader;
using AstraLauncher.Game.Install;
using AstraLauncher.Game.Metadata;
using AstraLauncher.Infrastructure.FileSystem;
using AstraLauncher.Infrastructure.Net;
using Microsoft.Extensions.Logging.Abstractions;

namespace AstraLauncher.Tests;

public sealed class VersionInstallServiceTests : IAsyncLifetime
{
    private const string VersionId = "1.21.1";
    private const string VersionMetadataUrl = "https://launchermeta.mojang.com/v1/packages/aaaa/1.21.1.json";
    private const string ClientUrl = "https://piston-data.mojang.com/v1/objects/c0ffee/client.jar";
    private const string LibraryUrl = "https://libraries.minecraft.net/com/example/artifact/1.0/artifact-1.0.jar";
    private const string LwjglUrl = "https://libraries.minecraft.net/org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3.jar";
    private const string NativeUrl = "https://libraries.minecraft.net/org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3-natives.jar";
    private const string DeniedLibraryUrl = "https://libraries.minecraft.net/com/example/denied/1.0/denied-1.0.jar";
    private const string AssetIndexUrl = "https://piston-meta.mojang.com/v1/packages/bbbb/17.json";
    private const string LoggingUrl = "https://launchermeta.mojang.com/v1/objects/cccc/client-1.12.xml";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "AstraLauncher.Tests", Guid.NewGuid().ToString("N"));
    private readonly LauncherPaths _paths;
    private readonly FakeManifestService _manifest = new();
    private readonly FakeHttpMessageHandler _handler = new();
    private readonly FakeSettingsService _settings = new();
    private readonly DownloadSourceResolver _resolver = new("https://mirror.example.com");
    private readonly HttpClient _httpClient;
    private readonly DownloadQueue _downloadQueue;
    private readonly VersionMetadataStore _metadataStore;
    private readonly VersionInstallService _service;
    private readonly byte[] _versionMetadata;
    private readonly byte[] _client;
    private readonly byte[] _library;
    private readonly byte[] _native;
    private readonly byte[] _logging;
    private readonly byte[] _assetIndex;
    private readonly byte[] _asset1;
    private readonly byte[] _asset2;
    private readonly string _assetHash1;
    private readonly string _assetHash2;

    public VersionInstallServiceTests()
    {
        _paths = new LauncherPaths(_root);
        _paths.EnsureCreated();
        _httpClient = new HttpClient(_handler) { Timeout = Timeout.InfiniteTimeSpan };
        _downloadQueue = new DownloadQueue(_httpClient, new DownloadQueueOptions
        {
            MaximumConcurrentDownloads = 2,
            MaximumConcurrentDownloadsPerHost = 2,
            InitialRetryDelay = TimeSpan.FromMilliseconds(1),
            MaximumRetryDelay = TimeSpan.FromMilliseconds(5),
            ProgressReportIntervalMilliseconds = 0,
        });

        _client = Encoding.UTF8.GetBytes("astra-client-jar");
        _library = Encoding.UTF8.GetBytes("astra-library-jar");
        _native = Encoding.UTF8.GetBytes("astra-native-jar");
        _logging = Encoding.UTF8.GetBytes("<log4j configuration/>");
        _asset1 = Encoding.UTF8.GetBytes("click-ogg");
        _asset2 = Encoding.UTF8.GetBytes("icon-png");
        _assetHash1 = Sha1(_asset1);
        _assetHash2 = Sha1(_asset2);
        string assetIndexJson = $$"""
        {"objects":{"minecraft/sounds/click.ogg":{"hash":"{{_assetHash1}}","size": {{_asset1.Length}} },"icons/icon.png":{"hash":"{{_assetHash2}}","size": {{_asset2.Length}} } } }
        """;
        _assetIndex = Encoding.UTF8.GetBytes(assetIndexJson);

        string versionJson = $$"""
        {
          "id": "1.21.1",
          "mainClass": "net.minecraft.client.main.Main",
          "assetIndex": { "id": "17", "sha1": "{{Sha1(_assetIndex)}}", "size": {{_assetIndex.Length}}, "totalSize": {{_asset1.Length + _asset2.Length}}, "url": "{{AssetIndexUrl}}" },
          "logging": {
            "client": {
              "argument": "-Dlog4j.configurationFile=${path}",
              "file": { "id": "client-1.12.xml", "sha1": "{{Sha1(_logging)}}", "size": {{_logging.Length}}, "url": "{{LoggingUrl}}" },
              "type": "log4j2-xml"
            }
          },
          "downloads": {
            "client": { "sha1": "{{Sha1(_client)}}", "size": {{_client.Length}}, "url": "{{ClientUrl}}" }
          },
          "libraries": [
            {
              "name": "com.example:artifact:1.0",
              "downloads": {
                "artifact": { "path": "com/example/artifact/1.0/artifact-1.0.jar", "sha1": "{{Sha1(_library)}}", "size": {{_library.Length}}, "url": "{{LibraryUrl}}" }
              }
            },
            {
              "name": "org.lwjgl:lwjgl:3.3.3",
              "natives": { "windows": "natives-windows", "osx": "natives-macos", "linux": "natives-linux" },
              "downloads": {
                "artifact": { "path": "org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3.jar", "sha1": "{{Sha1(_library)}}", "size": {{_library.Length}}, "url": "{{LwjglUrl}}" },
                "classifiers": {
                  "natives-windows": { "path": "org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3-natives.jar", "sha1": "{{Sha1(_native)}}", "size": {{_native.Length}}, "url": "{{NativeUrl}}" },
                  "natives-macos": { "path": "org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3-natives.jar", "sha1": "{{Sha1(_native)}}", "size": {{_native.Length}}, "url": "{{NativeUrl}}" },
                  "natives-linux": { "path": "org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3-natives.jar", "sha1": "{{Sha1(_native)}}", "size": {{_native.Length}}, "url": "{{NativeUrl}}" }
                }
              }
            },
            {
              "name": "com.example:featured:1.0",
              "rules": [ { "action": "allow", "features": { "has_custom_resolution": true } } ],
              "downloads": {
                "artifact": { "path": "com/example/featured/1.0/featured-1.0.jar", "sha1": "{{Sha1(_library)}}", "size": {{_library.Length}}, "url": "{{DeniedLibraryUrl}}" }
              }
            },
            {
              "name": "com.example:other-os:1.0",
              "rules": [ { "action": "allow", "os": { "name": "plan9" } } ],
              "downloads": {
                "artifact": { "path": "com/example/other-os/1.0/other-os-1.0.jar", "sha1": "{{Sha1(_library)}}", "size": {{_library.Length}}, "url": "{{DeniedLibraryUrl}}" }
              }
            }
          ]
        }
        """;
        _versionMetadata = Encoding.UTF8.GetBytes(versionJson);

        _manifest.AddSummary(VersionId, VersionMetadataUrl, Sha1(_versionMetadata));
        _handler.Add(VersionMetadataUrl, _versionMetadata);
        _handler.Add(ClientUrl, _client);
        _handler.Add(LibraryUrl, _library);
        _handler.Add(LwjglUrl, _library);
        _handler.Add(NativeUrl, _native);
        _handler.Add(DeniedLibraryUrl, _library);
        _handler.Add(AssetIndexUrl, _assetIndex);
        _handler.Add(LoggingUrl, _logging);
        _handler.Add(AssetUrl(_assetHash1), _asset1);
        _handler.Add(AssetUrl(_assetHash2), _asset2);

        _metadataStore = new VersionMetadataStore(_paths, _manifest, _resolver, _settings, _httpClient, NullLogger<VersionMetadataStore>.Instance);
        _service = new VersionInstallService(_paths, _manifest, _metadataStore, _resolver, _settings, _downloadQueue, NullLogger<VersionInstallService>.Instance);
    }

    [Fact]
    public async Task CreatePlanAsync_BuildsCompletePlanWithCategories()
    {
        VersionInstallPlan plan = await _service.CreatePlanAsync(VersionId);

        Assert.Equal(VersionId, plan.VersionId);
        Assert.Equal(9, plan.Files.Count);
        Assert.Single(plan.Files, file => file.Category == "version-json");
        Assert.Single(plan.Files, file => file.Category == "client");
        Assert.Equal(3, plan.Files.Count(file => file.Category == "library"));
        Assert.Single(plan.Files, file => file.Category == "asset-index");
        Assert.Equal(2, plan.Files.Count(file => file.Category == "asset"));
        Assert.Single(plan.Files, file => file.Category == "logging");
        Assert.Equal(plan.Files.Sum(file => file.Size ?? 0), plan.KnownTotalBytes);
        Assert.Contains(plan.Files, file => file.DestinationPath == Path.Combine(_root, "versions", VersionId, $"{VersionId}.json"));
        Assert.Contains(plan.Files, file => file.DestinationPath == Path.Combine(_root, "versions", VersionId, $"{VersionId}.jar"));
        Assert.Contains(plan.Files, file => file.DestinationPath == Path.Combine(_root, "assets", "indexes", "17.json"));
        Assert.Contains(plan.Files, file => file.DestinationPath == Path.Combine(_root, "assets", "logging", "client-1.12.xml"));
        Assert.Contains(plan.Files, file => file.DestinationPath == Path.Combine(_root, "assets", "objects", _assetHash1[..2], _assetHash1));
        Assert.Equal(0, _handler.CountFor(DeniedLibraryUrl));
    }

    [Fact]
    public async Task InstallAsync_DownloadsEveryFileAndSkipsAlreadyInstalledFiles()
    {
        await _service.InstallAsync(VersionId);

        await AssertFileContentAsync(Path.Combine(_root, "versions", VersionId, $"{VersionId}.jar"), _client);
        await AssertFileContentAsync(Path.Combine(_root, "versions", VersionId, $"{VersionId}.json"), _versionMetadata);
        await AssertFileContentAsync(Path.Combine(_root, "libraries", "com", "example", "artifact", "1.0", "artifact-1.0.jar"), _library);
        await AssertFileContentAsync(Path.Combine(_root, "libraries", "org", "lwjgl", "lwjgl", "3.3.3", "lwjgl-3.3.3.jar"), _library);
        await AssertFileContentAsync(Path.Combine(_root, "libraries", "org", "lwjgl", "lwjgl", "3.3.3", "lwjgl-3.3.3-natives.jar"), _native);
        await AssertFileContentAsync(Path.Combine(_root, "assets", "indexes", "17.json"), _assetIndex);
        await AssertFileContentAsync(Path.Combine(_root, "assets", "objects", _assetHash1[..2], _assetHash1), _asset1);
        await AssertFileContentAsync(Path.Combine(_root, "assets", "objects", _assetHash2[..2], _assetHash2), _asset2);
        await AssertFileContentAsync(Path.Combine(_root, "assets", "logging", "client-1.12.xml"), _logging);

        Assert.Equal(1, _handler.CountFor(VersionMetadataUrl));
        Assert.Equal(1, _handler.CountFor(ClientUrl));
        Assert.Equal(1, _handler.CountFor(LibraryUrl));
        Assert.Equal(1, _handler.CountFor(NativeUrl));
        Assert.Equal(1, _handler.CountFor(AssetIndexUrl));
        Assert.Equal(1, _handler.CountFor(LoggingUrl));
        Assert.Equal(0, _handler.CountFor(DeniedLibraryUrl));

        // A second install must be a no-op: nothing is requested again and the files stay intact.
        _handler.ResetCounts();
        await _service.InstallAsync(VersionId);

        Assert.Equal(0, _handler.RequestCount);
        await AssertFileContentAsync(Path.Combine(_root, "versions", VersionId, $"{VersionId}.jar"), _client);
        await AssertFileContentAsync(Path.Combine(_root, "assets", "logging", "client-1.12.xml"), _logging);
    }

    [Fact]
    public async Task InstallAsync_ReportsProgressAndCompletesAllFiles()
    {
        VersionInstallPlan plan = await _service.CreatePlanAsync(VersionId);
        var reports = new List<VersionInstallProgress>();
        var progress = new InlineProgress<VersionInstallProgress>(value =>
        {
            lock (reports)
            {
                reports.Add(value);
            }
        });

        await _service.InstallAsync(VersionId, progress);

        VersionInstallProgress last;
        lock (reports)
        {
            Assert.NotEmpty(reports);
            last = reports[^1];
        }

        Assert.Equal(plan.Files.Count, last.TotalFiles);
        Assert.Equal(last.TotalFiles, last.CompletedFiles);
        Assert.Equal(last.TotalBytes, last.DownloadedBytes);
        Assert.Contains("下载", last.Stage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IsVersionInstalledAsync_IsFalseBeforeAndTrueAfterInstall()
    {
        string clientJar = Path.Combine(_root, "versions", VersionId, $"{VersionId}.jar");

        Assert.False(await _service.IsVersionInstalledAsync(VersionId));
        Assert.False(File.Exists(clientJar));

        await _service.InstallAsync(VersionId);

        Assert.True(await _service.IsVersionInstalledAsync(VersionId));
        Assert.True(File.Exists(clientJar));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _downloadQueue.DisposeAsync();
        _httpClient.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static string AssetUrl(string hash) => $"https://resources.download.minecraft.net/{hash[..2]}/{hash}";

    private static string Sha1(byte[] data) => Convert.ToHexString(SHA1.HashData(data));

    private static async Task AssertFileContentAsync(string path, byte[] expected)
    {
        Assert.True(File.Exists(path), $"Expected file '{path}' to exist.");
        Assert.Equal(expected, await File.ReadAllBytesAsync(path));
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class FakeManifestService : IVersionManifestService
    {
        private readonly Dictionary<string, VersionSummary> _summaries = new(StringComparer.Ordinal);

        public void AddSummary(string id, string url, string? sha1) =>
            _summaries[id] = new VersionSummary(id, "release", url, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, sha1);

        public Task<VersionManifest> GetManifestAsync(bool forceRefresh = false, CancellationToken cancellationToken = default) =>
            Task.FromResult(new VersionManifest(new VersionManifestLatest(string.Empty, string.Empty), [.. _summaries.Values]));

        public Task<IReadOnlyList<VersionSummary>> GetVersionsAsync(bool includeSnapshots, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<VersionSummary>>([.. _summaries.Values]);

        public Task<VersionSummary> GetVersionSummaryAsync(string versionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_summaries.TryGetValue(versionId, out VersionSummary? summary)
                ? summary
                : throw new KeyNotFoundException($"No fake version summary for '{versionId}'."));
    }

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _responses = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, int> _requestCounts = new(StringComparer.Ordinal);
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        public void Add(string url, byte[] payload) => _responses[url] = payload;

        public int CountFor(string url) => _requestCounts.TryGetValue(url, out int count) ? count : 0;

        public void ResetCounts()
        {
            _requestCounts.Clear();
            Interlocked.Exchange(ref _requestCount, 0);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            string url = request.RequestUri?.AbsoluteUri ?? string.Empty;
            _requestCounts.AddOrUpdate(url, 1, (_, count) => count + 1);
            return Task.FromResult(_responses.TryGetValue(url, out byte[]? payload)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class FakeSettingsService : ISettingsService
    {
        private readonly LauncherSettings _settings = new() { DownloadSourceMode = DownloadSourceMode.Official };

        public event EventHandler<LauncherSettings>? Changed
        {
            add { }
            remove { }
        }

        public Task<LauncherSettings> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(_settings);

        public Task SaveAsync(LauncherSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
