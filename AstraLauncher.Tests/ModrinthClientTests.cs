using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using AstraLauncher.Game.Mods;
using Microsoft.Extensions.Logging.Abstractions;

namespace AstraLauncher.Tests;

public sealed class ModrinthClientTests
{
    [Fact]
    public async Task SearchAsync_ParsesHitsAndBuildsFacetQuery()
    {
        string? requestedUrl = null;
        var handler = new FakeHandler(request =>
        {
            requestedUrl = request.RequestUri?.AbsoluteUri;
            return JsonResponse("""
            {"hits":[{"project_id":"create","slug":"create","title":"Create","description":"Tech mod","icon_url":"https://cdn.example/icon.png","downloads":123456,"categories":["technology","fabric"]}],"total_hits":1}
            """);
        });
        var client = CreateClient(handler);

        ModrinthSearchResult result = await client.SearchAsync("create", "1.21.1", "fabric");

        ModrinthSearchHit hit = Assert.Single(result.Hits);
        Assert.Equal(1, result.TotalHits);
        Assert.Equal("create", hit.ProjectId);
        Assert.Equal("Create", hit.Title);
        Assert.Equal(123456, hit.Downloads);
        Assert.Contains("technology", hit.Categories);
        Assert.NotNull(requestedUrl);
        Assert.Contains("/v2/search", requestedUrl);
        Assert.Contains("project_type%3Amod", requestedUrl);
        Assert.Contains("versions%3A1.21.1", requestedUrl);
        Assert.Contains("categories%3Afabric", requestedUrl);
    }

    [Fact]
    public async Task GetVersionsAsync_ParsesFilesAndHashes()
    {
        var handler = new FakeHandler(_ => JsonResponse("""
        [{"id":"ver1","project_id":"create","name":"Create 1.0","version_number":"1.0","game_versions":["1.21.1"],"loaders":["fabric"],"files":[{"url":"https://cdn.example/create-1.0.jar","filename":"create-1.0.jar","size":42,"hashes":{"sha1":"ABCDEF"},"primary":true}]}]
        """));
        var client = CreateClient(handler);

        IReadOnlyList<ModrinthVersion> versions = await client.GetVersionsAsync("create", "1.21.1", "fabric");

        ModrinthVersion version = Assert.Single(versions);
        Assert.Equal("create", version.ProjectId);
        Assert.Equal("1.0", version.VersionNumber);
        ModrinthVersionFile file = Assert.Single(version.Files);
        Assert.True(file.IsPrimary);
        Assert.Equal("create-1.0.jar", file.FileName);
        Assert.Equal("ABCDEF", file.Sha1);
    }

    [Fact]
    public async Task DownloadAsync_VerifiesSha1AndWritesJar()
    {
        byte[] payload = Encoding.UTF8.GetBytes("jar-bytes");
        string sha1 = Convert.ToHexString(SHA1.HashData(payload));
        var version = new ModrinthVersion(
            "ver1",
            "create",
            "Create 1.0",
            "1.0",
            ["1.21.1"],
            ["fabric"],
            [new ModrinthVersionFile("create-1.0.jar", new Uri("https://cdn.example/create-1.0.jar"), payload.Length, sha1, true)]);
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        var client = CreateClient(handler);
        string modsDirectory = Path.Combine(Path.GetTempPath(), "AstraLauncher.Tests", Guid.NewGuid().ToString("N"), "mods");

        string installed = await client.DownloadAsync(version, modsDirectory);

        Assert.True(File.Exists(installed));
        Assert.Equal(payload, await File.ReadAllBytesAsync(installed));
        Assert.Empty(Directory.GetFiles(modsDirectory, "*.part"));
        Directory.Delete(Path.GetDirectoryName(modsDirectory)!, recursive: true);
    }

    [Fact]
    public async Task DownloadAsync_RejectsWrongHash()
    {
        var version = new ModrinthVersion(
            "ver1",
            "create",
            "Create 1.0",
            "1.0",
            ["1.21.1"],
            ["fabric"],
            [new ModrinthVersionFile("create-1.0.jar", new Uri("https://cdn.example/create-1.0.jar"), 3, "0000", true)]);
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
        var client = CreateClient(handler);
        string root = Path.Combine(Path.GetTempPath(), "AstraLauncher.Tests", Guid.NewGuid().ToString("N"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.DownloadAsync(version, Path.Combine(root, "mods")));
        Directory.Delete(root, recursive: true);
    }

    private static ModrinthClient CreateClient(FakeHandler handler) =>
        new(new HttpClient(handler), NullLogger<ModrinthClient>.Instance);

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
