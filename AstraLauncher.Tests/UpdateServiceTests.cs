using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using AstraLauncher.App.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AstraLauncher.Tests;

public sealed class UpdateServiceTests
{
    [Theory]
    [InlineData("v1.0.1", "1.0.0", true)]
    [InlineData("v1.0.0", "1.0.0", false)]
    [InlineData("v1.2.0", "1.1.9", true)]
    [InlineData("1.0.1", "1.0.0", true)]
    [InlineData("v2.0.0-beta", "1.9.9", true)]
    [InlineData("v1.0.0", "1.0.1", false)]
    [InlineData("not-a-version", "1.0.0", false)]
    public void IsNewerTag_ComparesSemanticVersions(string tag, string current, bool expected)
    {
        Assert.Equal(expected, UpdateService.IsNewerTag(tag, current));
    }

    [Fact]
    public void IsNewerTag_HandlesBuildRevision()
    {
        Assert.True(UpdateService.IsNewerTag("v1.0.1", new Version(1, 0, 0, 5)));
        Assert.False(UpdateService.IsNewerTag("v1.0.0", new Version(1, 0, 0, 5)));
    }

    [Fact]
    public async Task CheckAsync_ParsesNewerReleaseAsset()
    {
        const string json = """
            {
              "tag_name": "v9.9.9",
              "name": "Astra Launcher v9.9.9",
              "html_url": "https://github.com/yghxdmdjjdcxy/Astra-launcher/releases/tag/v9.9.9",
              "draft": false,
              "prerelease": false,
              "assets": [
                { "name": "AstraLauncher.exe", "size": 1234, "browser_download_url": "https://example.test/AstraLauncher.exe", "digest": "sha256:AABBCC" }
              ]
            }
            """;
        var service = new UpdateService(NullLogger<UpdateService>.Instance, new HttpClient(new JsonHandler(json)));

        LauncherUpdate? update = await service.CheckAsync();

        Assert.NotNull(update);
        Assert.Equal("v9.9.9", update!.Tag);
        Assert.Equal(1234, update.Size);
        Assert.Equal("AABBCC", update.Sha256);
        Assert.Equal("https://example.test/AstraLauncher.exe", update.DownloadUri.ToString());
    }

    [Fact]
    public async Task DownloadAsync_WritesAndVerifiesAsset()
    {
        byte[] payload = Encoding.UTF8.GetBytes("new launcher bytes");
        string sha = Convert.ToHexString(SHA256.HashData(payload));
        var update = new LauncherUpdate(
            "v9.9.9",
            "v9.9.9",
            new Uri("https://example.test/AstraLauncher.exe"),
            payload.Length,
            sha,
            new Uri("https://example.test/release"));
        var service = new UpdateService(NullLogger<UpdateService>.Instance, new HttpClient(new BytesHandler(payload)));

        string path = await service.DownloadAsync(update);

        try
        {
            Assert.True(File.Exists(path));
            Assert.Equal(payload, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
    }

    private sealed class BytesHandler(byte[] payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
            });
    }
}
