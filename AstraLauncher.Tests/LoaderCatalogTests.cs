using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using AstraLauncher.Game.Loaders;
using Microsoft.Extensions.Logging.Abstractions;

namespace AstraLauncher.Tests;

public sealed class LoaderCatalogTests : IDisposable
{
    private const string MinecraftVersion = "1.21.1";
    private const string FabricLoaderUrl = "https://meta.fabricmc.net/v2/versions/loader/1.21.1";
    private const string QuiltLoaderUrl = "https://meta.quiltmc.org/v3/versions/loader/1.21.1";
    private const string ForgePromotionsUrl = "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json";
    private const string NeoForgeVersionsUrl = "https://maven.neoforged.net/api/maven/versions/releases/net/neoforged/neoforge";

    private readonly RecordingHandler _handler = new();
    private readonly HttpClient _httpClient;

    public LoaderCatalogTests()
    {
        _httpClient = new HttpClient(_handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    [Fact]
    public async Task GetLoaderVersionsAsync_Fabric_PreservesOrderAndMapsStableToRecommended()
    {
        _handler.AddJson(FabricLoaderUrl, """
        [
          {"loader":{"version":"0.16.9","stable":true}},
          {"loader":{"version":"0.16.8","stable":false}},
          {"loader":{"version":"0.16.7","stable":true}}
        ]
        """);

        IReadOnlyList<LoaderVersion> versions = await CreateCatalog()
            .GetLoaderVersionsAsync(LoaderKind.Fabric, MinecraftVersion);

        Assert.Equal(3, versions.Count);
        Assert.Equal("0.16.9", versions[0].Version);
        Assert.Equal("Fabric Loader 0.16.9", versions[0].DisplayName);
        Assert.True(versions[0].IsRecommended);
        Assert.Equal("0.16.8", versions[1].Version);
        Assert.False(versions[1].IsRecommended);
        Assert.Equal("0.16.7", versions[2].Version);
        Assert.True(versions[2].IsRecommended);
        Assert.Equal(FabricLoaderUrl, Assert.Single(_handler.Requests));
    }

    [Fact]
    public async Task GetLoaderVersionsAsync_Quilt_ParsesVersionAndDisplayName()
    {
        _handler.AddJson(QuiltLoaderUrl, """
        [
          {"loader":{"version":"0.26.0"}},
          {"loader":{"version":"0.25.0"}}
        ]
        """);

        IReadOnlyList<LoaderVersion> versions = await CreateCatalog()
            .GetLoaderVersionsAsync(LoaderKind.Quilt, MinecraftVersion);

        Assert.Equal(2, versions.Count);
        Assert.Equal("0.26.0", versions[0].Version);
        Assert.Equal("Quilt Loader 0.26.0", versions[0].DisplayName);
        Assert.False(versions[0].IsRecommended);
        Assert.Equal("0.25.0", versions[1].Version);
        Assert.Equal(QuiltLoaderUrl, Assert.Single(_handler.Requests));
    }

    [Fact]
    public async Task GetLoaderVersionsAsync_Forge_ReturnsOneRecommendedWhenLatestAndRecommendedMatch()
    {
        _handler.AddJson(ForgePromotionsUrl, """
        {"promos":{"1.21.1":"52.0.14","1.21.1-recommended":"52.0.14","1.21.1-latest":"52.0.14"}}
        """);

        IReadOnlyList<LoaderVersion> versions = await CreateCatalog()
            .GetLoaderVersionsAsync(LoaderKind.Forge, MinecraftVersion);

        LoaderVersion version = Assert.Single(versions);
        Assert.Equal("52.0.14", version.Version);
        Assert.Equal("Forge 52.0.14", version.DisplayName);
        Assert.True(version.IsRecommended);
        Assert.Equal(ForgePromotionsUrl, Assert.Single(_handler.Requests));
    }

    [Fact]
    public async Task GetLoaderVersionsAsync_Forge_ReturnsRecommendedBeforeLatestWhenTheyDiffer()
    {
        _handler.AddJson(ForgePromotionsUrl, """
        {"promos":{"1.21.1-recommended":"52.0.14","1.21.1-latest":"52.0.20"}}
        """);

        IReadOnlyList<LoaderVersion> versions = await CreateCatalog()
            .GetLoaderVersionsAsync(LoaderKind.Forge, MinecraftVersion);

        Assert.Equal(2, versions.Count);
        Assert.Equal("52.0.14", versions[0].Version);
        Assert.True(versions[0].IsRecommended);
        Assert.Equal("Forge 52.0.14", versions[0].DisplayName);
        Assert.Equal("52.0.20", versions[1].Version);
        Assert.False(versions[1].IsRecommended);
    }

    [Fact]
    public async Task GetLoaderVersionsAsync_Forge_SupportsBareLegacyKeyAndUnknownVersion()
    {
        _handler.AddJson(ForgePromotionsUrl, """
        {"promos":{"1.16.5":"36.2.34"}}
        """);

        LoaderVersion legacy = Assert.Single(
            await CreateCatalog().GetLoaderVersionsAsync(LoaderKind.Forge, "1.16.5"));
        Assert.Equal("36.2.34", legacy.Version);
        Assert.True(legacy.IsRecommended);

        IReadOnlyList<LoaderVersion> unknown = await CreateCatalog()
            .GetLoaderVersionsAsync(LoaderKind.Forge, "1.21.1");
        Assert.Empty(unknown);
    }

    [Fact]
    public async Task GetLoaderVersionsAsync_NeoForge_FiltersByBranchAndSortsNewestFirst()
    {
        _handler.AddJson(NeoForgeVersionsUrl, """
        {"versions":["21.1.9","20.1.5","21.1.100","21.2.0","21.1.80"]}
        """);

        IReadOnlyList<LoaderVersion> versions = await CreateCatalog()
            .GetLoaderVersionsAsync(LoaderKind.NeoForge, "1.21.1");

        Assert.Equal(3, versions.Count);
        Assert.Equal(["21.1.100", "21.1.80", "21.1.9"], versions.Select(version => version.Version));
        Assert.Equal("NeoForge 21.1.100", versions[0].DisplayName);
        Assert.False(versions[0].IsRecommended);

        LoaderVersion old = Assert.Single(
            await CreateCatalog().GetLoaderVersionsAsync(LoaderKind.NeoForge, "1.20.1"));
        Assert.Equal("20.1.5", old.Version);
    }

    [Fact]
    public async Task GetLoaderVersionsAsync_NeoForge_ReturnsEmptyWhenNoBranchMatches()
    {
        _handler.AddJson(NeoForgeVersionsUrl, """
        {"versions":["21.1.80","20.1.5"]}
        """);

        IReadOnlyList<LoaderVersion> versions = await CreateCatalog()
            .GetLoaderVersionsAsync(LoaderKind.NeoForge, "1.19.2");

        Assert.Empty(versions);
    }

    [Fact]
    public async Task GetLoaderVersionsAsync_WhenHttpRequestFails_ThrowsInvalidOperationExceptionWithStatus()
    {
        _handler.AddStatus(FabricLoaderUrl, HttpStatusCode.ServiceUnavailable);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateCatalog().GetLoaderVersionsAsync(LoaderKind.Fabric, MinecraftVersion));

        Assert.Contains("503", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetLoaderVersionsAsync_WhenJsonIsInvalid_ThrowsInvalidOperationException()
    {
        _handler.AddJson(FabricLoaderUrl, "{ \"loader\": ");

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateCatalog().GetLoaderVersionsAsync(LoaderKind.Fabric, MinecraftVersion));

        Assert.Contains("JSON", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private LoaderCatalog CreateCatalog() => new(_httpClient, NullLogger<LoaderCatalog>.Instance);

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
}
