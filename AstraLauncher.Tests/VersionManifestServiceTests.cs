using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using AstraLauncher.Core.Models;
using AstraLauncher.Core.Services;
using AstraLauncher.Infrastructure.FileSystem;
using AstraLauncher.Infrastructure.Net;
using AstraLauncher.Infrastructure.Versioning;
using Microsoft.Extensions.Logging.Abstractions;

namespace AstraLauncher.Tests;

public sealed class VersionManifestServiceTests : IDisposable
{
    private const string MirrorBaseUrl = "https://mirror.example.com";
    private const string OfficialManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    private const string MirrorManifestUrl = "https://mirror.example.com/mc/game/version_manifest_v2.json";
    private const string CacheFileName = "version_manifest_v2.json";
    private const string CacheTimestampFileName = "version_manifest_v2.fetched";

    private static readonly DateTimeOffset FirstReleaseTime = new(2024, 8, 8, 14, 24, 33, TimeSpan.Zero);
    private static readonly DateTimeOffset FirstReleaseDocumentTime = new(2024, 8, 8, 14, 29, 42, TimeSpan.Zero);

    private const string ManifestJson = """
        {
          "latest": { "release": "1.21.1", "snapshot": "24w40a" },
          "versions": [
            {
              "id": "1.21.1",
              "type": "release",
              "url": "https://piston-meta.mojang.com/v1/packages/0000000000000000000000000000000000000001/1.21.1.json",
              "time": "2024-08-08T14:29:42+00:00",
              "releaseTime": "2024-08-08T14:24:33+00:00",
              "sha1": "1111111111111111111111111111111111111111"
            },
            {
              "id": "1.21",
              "type": "release",
              "url": "https://piston-meta.mojang.com/v1/packages/0000000000000000000000000000000000000002/1.21.json",
              "time": "2024-06-13T15:00:00+00:00",
              "releaseTime": "2024-06-13T14:50:00+00:00",
              "sha1": "2222222222222222222222222222222222222222"
            }
          ]
        }
        """;

    private const string ReplacementManifestJson = """
        {
          "latest": { "release": "1.22", "snapshot": "24w45a" },
          "versions": [
            {
              "id": "1.22",
              "type": "release",
              "url": "https://piston-meta.mojang.com/v1/packages/0000000000000000000000000000000000000003/1.22.json",
              "time": "2024-11-01T00:00:00+00:00",
              "releaseTime": "2024-10-31T00:00:00+00:00",
              "sha1": "3333333333333333333333333333333333333333"
            }
          ]
        }
        """;

    private const string MixedManifestJson = """
        {
          "latest": { "release": "release-new", "snapshot": "snapshot-new" },
          "versions": [
            {
              "id": "release-old",
              "type": "release",
              "url": "https://piston-meta.mojang.com/v1/packages/0000000000000000000000000000000000000010/release-old.json",
              "time": "2024-01-01T00:00:00+00:00",
              "releaseTime": "2024-01-01T00:00:00+00:00",
              "sha1": "1010101010101010101010101010101010101010"
            },
            {
              "id": "snapshot-new",
              "type": "snapshot",
              "url": "https://piston-meta.mojang.com/v1/packages/0000000000000000000000000000000000000011/snapshot-new.json",
              "time": "2024-10-01T00:00:00+00:00",
              "releaseTime": "2024-10-01T00:00:00+00:00",
              "sha1": "1111111111111111111111111111111111111112"
            },
            {
              "id": "old-beta",
              "type": "old_beta",
              "url": "https://piston-meta.mojang.com/v1/packages/0000000000000000000000000000000000000012/old-beta.json",
              "time": "2024-08-01T00:00:00+00:00",
              "releaseTime": "2024-08-01T00:00:00+00:00",
              "sha1": "1212121212121212121212121212121212121212"
            },
            {
              "id": "release-new",
              "type": "release",
              "url": "https://piston-meta.mojang.com/v1/packages/0000000000000000000000000000000000000013/release-new.json",
              "time": "2024-09-01T00:00:00+00:00",
              "releaseTime": "2024-09-01T00:00:00+00:00",
              "sha1": "1313131313131313131313131313131313131313"
            },
            {
              "id": "old-alpha",
              "type": "old_alpha",
              "url": "https://piston-meta.mojang.com/v1/packages/0000000000000000000000000000000000000014/old-alpha.json",
              "time": "2023-11-01T00:00:00+00:00",
              "releaseTime": "2023-11-01T00:00:00+00:00",
              "sha1": "1414141414141414141414141414141414141414"
            },
            {
              "id": "snapshot-old",
              "type": "snapshot",
              "url": "https://piston-meta.mojang.com/v1/packages/0000000000000000000000000000000000000015/snapshot-old.json",
              "time": "2023-12-01T00:00:00+00:00",
              "releaseTime": "2023-12-01T00:00:00+00:00",
              "sha1": "1515151515151515151515151515151515151515"
            }
          ]
        }
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "AstraLauncher.Tests", Guid.NewGuid().ToString("N"));
    private readonly List<HttpClient> _httpClients = new();

    private string CacheDirectory => Path.Combine(_root, "cache");

    private string CacheFilePath => Path.Combine(CacheDirectory, CacheFileName);

    private string CacheTimestampPath => Path.Combine(CacheDirectory, CacheTimestampFileName);

    public void Dispose()
    {
        foreach (HttpClient httpClient in _httpClients)
        {
            httpClient.Dispose();
        }

        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temporary directory must not fail an otherwise passing test.
        }
        catch (UnauthorizedAccessException)
        {
            // Same as above: cleanup is best effort.
        }
    }

    [Fact]
    public async Task GetManifestAsync_WithoutCache_DownloadsParsesAndCachesManifest()
    {
        var handler = new FakeHttpMessageHandler(_ => JsonResponse(ManifestJson));
        VersionManifestService service = CreateService(handler);

        VersionManifest manifest = await service.GetManifestAsync();

        Assert.Equal("1.21.1", manifest.Latest.Release);
        Assert.Equal("24w40a", manifest.Latest.Snapshot);
        Assert.Equal(2, manifest.Versions.Count);
        VersionSummary first = manifest.Versions[0];
        Assert.Equal("1.21.1", first.Id);
        Assert.Equal("release", first.Type);
        Assert.Equal("https://piston-meta.mojang.com/v1/packages/0000000000000000000000000000000000000001/1.21.1.json", first.Url);
        Assert.Equal(FirstReleaseDocumentTime, first.Time);
        Assert.Equal(FirstReleaseTime, first.ReleaseTime);
        Assert.Equal("1111111111111111111111111111111111111111", first.Sha1);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(new[] { new Uri(OfficialManifestUrl) }, handler.RequestedUris);

        Assert.True(File.Exists(CacheFilePath));
        Assert.True(File.Exists(CacheTimestampPath));
        Assert.Equal(ManifestJson, await File.ReadAllTextAsync(CacheFilePath));
        Assert.Empty(Directory.GetFiles(CacheDirectory, "*.tmp"));
    }

    [Fact]
    public async Task GetManifestAsync_WithFreshCache_DoesNotUseNetwork()
    {
        await WriteCacheAsync(ManifestJson, DateTimeOffset.UtcNow);
        var handler = new FakeHttpMessageHandler(_ => throw new InvalidOperationException("The network must not be used for a fresh cache."));
        VersionManifestService service = CreateService(handler);

        VersionManifest manifest = await service.GetManifestAsync();

        Assert.Equal("1.21.1", manifest.Latest.Release);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task GetManifestAsync_WithStaleCache_RefreshesFromNetwork()
    {
        await WriteCacheAsync(ManifestJson, DateTimeOffset.UtcNow.AddHours(-2));
        var handler = new FakeHttpMessageHandler(_ => JsonResponse(ReplacementManifestJson));
        VersionManifestService service = CreateService(handler);

        VersionManifest manifest = await service.GetManifestAsync();

        Assert.Equal("1.22", manifest.Latest.Release);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(ReplacementManifestJson, await File.ReadAllTextAsync(CacheFilePath));
    }

    [Fact]
    public async Task GetManifestAsync_WithStaleCacheAndFailingNetwork_ReturnsCachedManifest()
    {
        await WriteCacheAsync(ManifestJson, DateTimeOffset.UtcNow.AddHours(-2));
        var handler = new FakeHttpMessageHandler(_ => throw new HttpRequestException("The network is unreachable."));
        VersionManifestService service = CreateService(handler);

        VersionManifest manifest = await service.GetManifestAsync();

        Assert.Equal("1.21.1", manifest.Latest.Release);
        Assert.Equal(2, manifest.Versions.Count);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task GetManifestAsync_ForceRefresh_BypassesFreshCacheAndReplacesIt()
    {
        await WriteCacheAsync(ManifestJson, DateTimeOffset.UtcNow);
        var handler = new FakeHttpMessageHandler(_ => JsonResponse(ReplacementManifestJson));
        VersionManifestService service = CreateService(handler);

        VersionManifest manifest = await service.GetManifestAsync(forceRefresh: true);
        VersionManifest secondCall = await service.GetManifestAsync();

        Assert.Equal("1.22", manifest.Latest.Release);
        Assert.Equal("1.22", secondCall.Latest.Release);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(ReplacementManifestJson, await File.ReadAllTextAsync(CacheFilePath));
    }

    [Fact]
    public async Task GetManifestAsync_ForceRefreshWithFailingNetwork_FallsBackToCachedManifest()
    {
        await WriteCacheAsync(ManifestJson, DateTimeOffset.UtcNow.AddHours(-2));
        var handler = new FakeHttpMessageHandler(_ => throw new HttpRequestException("The network is unreachable."));
        VersionManifestService service = CreateService(handler);

        VersionManifest manifest = await service.GetManifestAsync(forceRefresh: true);

        Assert.Equal("1.21.1", manifest.Latest.Release);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task GetManifestAsync_OfficialWithFallback_TriesMirrorAfterOfficialFailure()
    {
        var handler = new FakeHttpMessageHandler(request =>
            request.RequestUri!.Host == "piston-meta.mojang.com"
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : JsonResponse(ManifestJson));
        VersionManifestService service = CreateService(handler, DownloadSourceMode.OfficialWithFallback);

        VersionManifest manifest = await service.GetManifestAsync();

        Assert.Equal("1.21.1", manifest.Latest.Release);
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(
            new[] { OfficialManifestUrl, MirrorManifestUrl },
            handler.RequestedUris.Select(uri => uri.AbsoluteUri));
    }

    [Fact]
    public async Task GetManifestAsync_WithoutCacheAndFailingNetwork_ThrowsInvalidOperationException()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new HttpRequestException("The network is unreachable."));
        VersionManifestService service = CreateService(handler);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetManifestAsync());

        Assert.IsType<HttpRequestException>(exception.InnerException);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task GetManifestAsync_WithMalformedJson_ThrowsInvalidDataException()
    {
        var handler = new FakeHttpMessageHandler(_ => JsonResponse("{ \"latest\": { \"release\": "));
        VersionManifestService service = CreateService(handler);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetManifestAsync());
    }

    [Fact]
    public async Task GetManifestAsync_WithMissingLatestSection_ThrowsInvalidDataException()
    {
        const string json = """
            { "versions": [] }
            """;
        var handler = new FakeHttpMessageHandler(_ => JsonResponse(json));
        VersionManifestService service = CreateService(handler);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetManifestAsync());
    }

    [Fact]
    public async Task GetManifestAsync_WithInvalidVersionUrl_ThrowsFormatException()
    {
        const string json = """
            {
              "latest": { "release": "1.21.1", "snapshot": "24w40a" },
              "versions": [
                {
                  "id": "1.21.1",
                  "type": "release",
                  "url": "ftp://example.test/1.21.1.json",
                  "time": "2024-08-08T14:29:42+00:00",
                  "releaseTime": "2024-08-08T14:24:33+00:00"
                }
              ]
            }
            """;
        var handler = new FakeHttpMessageHandler(_ => JsonResponse(json));
        VersionManifestService service = CreateService(handler);

        await Assert.ThrowsAsync<FormatException>(() => service.GetManifestAsync());
    }

    [Fact]
    public async Task GetVersionsAsync_FiltersByTypeAndOrdersByReleaseTimeDescending()
    {
        var handler = new FakeHttpMessageHandler(_ => JsonResponse(MixedManifestJson));
        VersionManifestService service = CreateService(handler);

        IReadOnlyList<VersionSummary> releases = await service.GetVersionsAsync(includeSnapshots: false);
        IReadOnlyList<VersionSummary> withSnapshots = await service.GetVersionsAsync(includeSnapshots: true);

        Assert.Equal(new[] { "release-new", "release-old" }, releases.Select(version => version.Id));
        Assert.Equal(
            new[] { "snapshot-new", "release-new", "release-old", "snapshot-old" },
            withSnapshots.Select(version => version.Id));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task GetVersionSummaryAsync_KnownVersionId_ReturnsManifestEntry()
    {
        var handler = new FakeHttpMessageHandler(_ => JsonResponse(ManifestJson));
        VersionManifestService service = CreateService(handler);

        VersionSummary summary = await service.GetVersionSummaryAsync("1.21");

        Assert.Equal("1.21", summary.Id);
        Assert.Equal("release", summary.Type);
        Assert.Equal(new DateTimeOffset(2024, 6, 13, 14, 50, 0, TimeSpan.Zero), summary.ReleaseTime);
    }

    [Fact]
    public async Task GetVersionSummaryAsync_UnknownVersionId_ThrowsKeyNotFoundException()
    {
        var handler = new FakeHttpMessageHandler(_ => JsonResponse(ManifestJson));
        VersionManifestService service = CreateService(handler);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.GetVersionSummaryAsync("9.9.9"));
    }

    [Fact]
    public async Task GetManifestAsync_ConcurrentCalls_UseSingleDownload()
    {
        var handler = new FakeHttpMessageHandler(_ => JsonResponse(ManifestJson));
        VersionManifestService service = CreateService(handler);

        VersionManifest[] manifests = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => service.GetManifestAsync()));

        Assert.All(manifests, manifest => Assert.Equal("1.21.1", manifest.Latest.Release));
        Assert.Equal(1, handler.RequestCount);
    }

    private VersionManifestService CreateService(
        FakeHttpMessageHandler handler,
        DownloadSourceMode mode = DownloadSourceMode.Official)
    {
        HttpClient httpClient = new(handler);
        _httpClients.Add(httpClient);
        return new VersionManifestService(
            new LauncherPaths(_root),
            new DownloadSourceResolver(MirrorBaseUrl),
            new FakeSettingsService(mode),
            httpClient,
            NullLogger<VersionManifestService>.Instance);
    }

    private async Task WriteCacheAsync(string json, DateTimeOffset fetchedAt)
    {
        Directory.CreateDirectory(CacheDirectory);
        await File.WriteAllTextAsync(CacheFilePath, json);
        await File.WriteAllTextAsync(
            CacheTimestampPath,
            fetchedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json) };

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        private readonly List<Uri> _requestedUris = new();
        private int _requestCount;

        public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        public int RequestCount => Volatile.Read(ref _requestCount);

        public IReadOnlyList<Uri> RequestedUris => _requestedUris;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            _requestedUris.Add(request.RequestUri!);
            return Task.FromResult(_responder(request));
        }
    }

    private sealed class FakeSettingsService : ISettingsService
    {
        private readonly LauncherSettings _settings;

        public FakeSettingsService(DownloadSourceMode mode)
        {
            _settings = new LauncherSettings { DownloadSourceMode = mode };
        }

        public event EventHandler<LauncherSettings>? Changed
        {
            add { }
            remove { }
        }

        public Task<LauncherSettings> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(_settings);

        public Task SaveAsync(LauncherSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
