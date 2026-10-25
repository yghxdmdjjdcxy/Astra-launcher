using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using AstraLauncher.Core.Models;
using AstraLauncher.Core.Services;
using AstraLauncher.Game.Metadata;
using AstraLauncher.Infrastructure.FileSystem;
using AstraLauncher.Infrastructure.Net;
using Microsoft.Extensions.Logging.Abstractions;

namespace AstraLauncher.Tests;

public sealed class VersionMetadataStoreTests : IAsyncLifetime
{
    private const string ChildMetadataUrl = "https://launchermeta.mojang.com/v1/packages/child/child.json";
    private const string ParentMetadataUrl = "https://launchermeta.mojang.com/v1/packages/parent/parent.json";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "AstraLauncher.Tests", Guid.NewGuid().ToString("N"));
    private readonly LauncherPaths _paths;
    private readonly FakeManifestService _manifest = new();
    private readonly FakeHttpMessageHandler _handler = new();
    private readonly HttpClient _httpClient;

    public VersionMetadataStoreTests()
    {
        _paths = new LauncherPaths(_root);
        _paths.EnsureCreated();
        _httpClient = new HttpClient(_handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    [Fact]
    public async Task EnsureResolvedAsync_DownloadsChildAndParentAndMergesLogging()
    {
        string childJson = """
        {"id":"child","inheritsFrom":"parent","mainClass":"child.Main","libraries":[],"downloads":{"client":{"sha1":"0123456789abcdef0123456789abcdef01234567","size":11,"url":"https://piston-data.mojang.com/v1/objects/c0ffee/client.jar"}}}
        """;
        string parentJson = """
        {"id":"parent","mainClass":"parent.Main","assetIndex":{"id":"legacy","sha1":"aaaa","size":1,"totalSize":2,"url":"https://piston-meta.mojang.com/v1/packages/abc/legacy.json"},"logging":{"client":{"argument":"-Dlog4j.configurationFile=${path}","file":{"id":"client-1.12.xml","sha1":"bbbb","size":12,"url":"https://launchermeta.mojang.com/v1/objects/bbbb/client-1.12.xml"},"type":"log4j2-xml"}}}
        """;
        byte[] childBytes = Encoding.UTF8.GetBytes(childJson);
        byte[] parentBytes = Encoding.UTF8.GetBytes(parentJson);
        _manifest.AddSummary("child", ChildMetadataUrl, Sha1(childBytes));
        _manifest.AddSummary("parent", ParentMetadataUrl, null);
        _handler.Add(ChildMetadataUrl, childBytes);
        _handler.Add(ParentMetadataUrl, parentBytes);

        MinecraftVersionMetadata metadata = await CreateStore().EnsureResolvedAsync("child");

        string childPath = Path.Combine(_root, "versions", "child", "child.json");
        string parentPath = Path.Combine(_root, "versions", "parent", "parent.json");
        Assert.True(File.Exists(childPath));
        Assert.True(File.Exists(parentPath));
        Assert.Equal(childBytes, await File.ReadAllBytesAsync(childPath));
        Assert.Equal(parentBytes, await File.ReadAllBytesAsync(parentPath));
        Assert.Equal("child.Main", metadata.MainClass);
        Assert.Equal("legacy", metadata.AssetIndex!.Id);
        Assert.NotNull(metadata.Logging);
        Assert.Equal("-Dlog4j.configurationFile=${path}", metadata.Logging!.Client.Argument);
        Assert.Equal("client-1.12.xml", metadata.Logging.Client.File.Id);
        Assert.Equal("log4j2-xml", metadata.Logging.Client.Type);
        Assert.Equal("0123456789abcdef0123456789abcdef01234567", metadata.Downloads["client"].Sha1);
        Assert.Equal(2, _manifest.SummaryRequestCount);
    }

    [Fact]
    public async Task EnsureResolvedAsync_WhenSha1DoesNotMatch_ThrowsAndLeavesNoFile()
    {
        const string versionId = "1.21.1";
        const string metadataUrl = "https://launchermeta.mojang.com/v1/packages/1.21.1/1.21.1.json";
        byte[] payload = Encoding.UTF8.GetBytes("""{"id":"1.21.1"}""");
        _manifest.AddSummary(versionId, metadataUrl, new string('0', 40));
        _handler.Add(metadataUrl, payload);

        VersionMetadataException exception = await Assert.ThrowsAsync<VersionMetadataException>(() => CreateStore().EnsureResolvedAsync(versionId));

        Assert.Contains("1.21.1", exception.Message, StringComparison.Ordinal);
        Assert.Contains("SHA-1", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_root, "versions", versionId, $"{versionId}.json")));
    }

    [Fact]
    public async Task EnsureResolvedAsync_WhenJsonIsInvalid_ThrowsVersionMetadataException()
    {
        const string versionId = "broken";
        const string metadataUrl = "https://launchermeta.mojang.com/v1/packages/broken/broken.json";
        _manifest.AddSummary(versionId, metadataUrl, null);
        _handler.Add(metadataUrl, Encoding.UTF8.GetBytes("{ this is not json"));

        VersionMetadataException exception = await Assert.ThrowsAsync<VersionMetadataException>(() => CreateStore().EnsureResolvedAsync(versionId));

        Assert.Contains("JSON", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(_root, "versions", versionId, $"{versionId}.json")));
    }

    [Fact]
    public async Task EnsureResolvedAsync_WhenLocalMetadataExists_DoesNotDownload()
    {
        const string versionId = "local";
        string directory = Path.Combine(_root, "versions", versionId);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, $"{versionId}.json"), """{"id":"local","mainClass":"local.Main"}""");

        MinecraftVersionMetadata metadata = await CreateStore().EnsureResolvedAsync(versionId);

        Assert.Equal("local.Main", metadata.MainClass);
        Assert.Equal(0, _manifest.SummaryRequestCount);
        Assert.Equal(0, _handler.RequestCount);
    }

    [Fact]
    public async Task EnsureResolvedAsync_RejectsInheritanceCycles()
    {
        await WriteLocalVersionAsync("one", """{"id":"one","inheritsFrom":"two"}""");
        await WriteLocalVersionAsync("two", """{"id":"two","inheritsFrom":"one"}""");

        VersionMetadataException exception = await Assert.ThrowsAsync<VersionMetadataException>(() => CreateStore().EnsureResolvedAsync("one"));

        Assert.Contains("cycle", exception.Message, StringComparison.OrdinalIgnoreCase);
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

    private VersionMetadataStore CreateStore() => new(
        _paths,
        _manifest,
        new DownloadSourceResolver("https://mirror.example.com"),
        new FakeSettingsService(),
        _httpClient,
        NullLogger<VersionMetadataStore>.Instance);

    private async Task WriteLocalVersionAsync(string versionId, string content)
    {
        string directory = Path.Combine(_root, "versions", versionId);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, $"{versionId}.json"), content);
    }

    private static string Sha1(byte[] data) => Convert.ToHexString(SHA1.HashData(data));

    private sealed class FakeManifestService : IVersionManifestService
    {
        private readonly Dictionary<string, VersionSummary> _summaries = new(StringComparer.Ordinal);
        private int _summaryRequestCount;

        public int SummaryRequestCount => Volatile.Read(ref _summaryRequestCount);

        public void AddSummary(string id, string url, string? sha1) =>
            _summaries[id] = new VersionSummary(id, "release", url, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, sha1);

        public Task<VersionManifest> GetManifestAsync(bool forceRefresh = false, CancellationToken cancellationToken = default) =>
            Task.FromResult(new VersionManifest(new VersionManifestLatest(string.Empty, string.Empty), [.. _summaries.Values]));

        public Task<IReadOnlyList<VersionSummary>> GetVersionsAsync(bool includeSnapshots, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<VersionSummary>>([.. _summaries.Values]);

        public Task<VersionSummary> GetVersionSummaryAsync(string versionId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _summaryRequestCount);
            return Task.FromResult(_summaries.TryGetValue(versionId, out VersionSummary? summary)
                ? summary
                : throw new KeyNotFoundException($"No fake version summary for '{versionId}'."));
        }
    }

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _responses = new(StringComparer.Ordinal);
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        public void Add(string url, byte[] payload) => _responses[url] = payload;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            string url = request.RequestUri?.AbsoluteUri ?? string.Empty;
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
