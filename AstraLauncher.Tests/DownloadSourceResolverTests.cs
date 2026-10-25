using AstraLauncher.Core.Models;
using AstraLauncher.Infrastructure.Net;

namespace AstraLauncher.Tests;

public sealed class DownloadSourceResolverTests
{
    private const string MirrorBaseUrl = "https://mirror.example.com";
    private const string OfficialManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

    [Fact]
    public void GetVersionManifestUri_OfficialMode_ReturnsOfficialManifest()
    {
        var resolver = new DownloadSourceResolver(MirrorBaseUrl);

        Uri manifest = resolver.GetVersionManifestUri(DownloadSourceMode.Official);

        Assert.Equal(OfficialManifestUrl, manifest.AbsoluteUri);
    }

    [Fact]
    public void GetVersionManifestUri_MirrorMode_ReturnsMirrorManifest()
    {
        var resolver = new DownloadSourceResolver(MirrorBaseUrl);

        Uri manifest = resolver.GetVersionManifestUri(DownloadSourceMode.Mirror);

        Assert.Equal("https://mirror.example.com/mc/game/version_manifest_v2.json", manifest.AbsoluteUri);
    }

    [Fact]
    public void GetVersionManifestUri_OfficialWithFallback_ReturnsOfficialManifest()
    {
        var resolver = new DownloadSourceResolver(MirrorBaseUrl);

        Uri manifest = resolver.GetVersionManifestUri(DownloadSourceMode.OfficialWithFallback);

        Assert.Equal(OfficialManifestUrl, manifest.AbsoluteUri);
    }

    [Fact]
    public void Constructor_TrimsTrailingSlashFromMirrorBaseUrl()
    {
        var resolver = new DownloadSourceResolver("https://mirror.example.com/");

        Assert.Equal("https://mirror.example.com/mc/game/version_manifest_v2.json", resolver.GetVersionManifestUri(DownloadSourceMode.Mirror).AbsoluteUri);
    }

    [Fact]
    public void GetCandidates_OfficialMode_ReturnsOnlyOfficialUri()
    {
        var resolver = new DownloadSourceResolver(MirrorBaseUrl);
        Uri official = new("https://libraries.minecraft.net/net/minecraft/launchwrapper/1.12/launchwrapper-1.12.jar");

        IReadOnlyList<Uri> candidates = resolver.GetCandidates(official, DownloadSourceMode.Official);

        Assert.Single(candidates);
        Assert.Equal(official, candidates[0]);
    }

    [Fact]
    public void GetCandidates_MirrorMode_MapsMetadataHostToMirrorPathAndQuery()
    {
        var resolver = new DownloadSourceResolver(MirrorBaseUrl);
        Uri official = new("https://launchermeta.mojang.com/v1/packages/abc123/1.21.1.json?verify=1");

        IReadOnlyList<Uri> candidates = resolver.GetCandidates(official, DownloadSourceMode.Mirror);

        Assert.Single(candidates);
        Assert.Equal("https://mirror.example.com/v1/packages/abc123/1.21.1.json?verify=1", candidates[0].AbsoluteUri);
    }

    [Fact]
    public void GetCandidates_MirrorMode_MapsLibraryHostToMavenPath()
    {
        var resolver = new DownloadSourceResolver(MirrorBaseUrl);
        Uri official = new("https://libraries.minecraft.net/net/minecraft/launchwrapper/1.12/launchwrapper-1.12.jar");

        IReadOnlyList<Uri> candidates = resolver.GetCandidates(official, DownloadSourceMode.Mirror);

        Assert.Single(candidates);
        Assert.Equal("https://mirror.example.com/maven/net/minecraft/launchwrapper/1.12/launchwrapper-1.12.jar", candidates[0].AbsoluteUri);
    }

    [Fact]
    public void GetCandidates_MirrorMode_MapsResourceHostToAssetsPath()
    {
        var resolver = new DownloadSourceResolver(MirrorBaseUrl);
        Uri official = new("https://resources.download.minecraft.net/ab/abcdef0123456789");

        IReadOnlyList<Uri> candidates = resolver.GetCandidates(official, DownloadSourceMode.Mirror);

        Assert.Single(candidates);
        Assert.Equal("https://mirror.example.com/assets/ab/abcdef0123456789", candidates[0].AbsoluteUri);
    }

    [Fact]
    public void GetCandidates_OfficialWithFallback_ReturnsOfficialFirstThenMirror()
    {
        var resolver = new DownloadSourceResolver(MirrorBaseUrl);
        Uri official = new("https://piston-data.mojang.com/v1/objects/c0ffee/client.jar");

        IReadOnlyList<Uri> candidates = resolver.GetCandidates(official, DownloadSourceMode.OfficialWithFallback);

        Assert.Equal(2, candidates.Count);
        Assert.Equal(official, candidates[0]);
        Assert.Equal("https://mirror.example.com/v1/objects/c0ffee/client.jar", candidates[1].AbsoluteUri);
    }

    [Fact]
    public void GetCandidates_MirrorMode_UnknownHost_ReturnsNoCandidates()
    {
        var resolver = new DownloadSourceResolver(MirrorBaseUrl);
        Uri official = new("https://example.com/some/file.bin");

        IReadOnlyList<Uri> candidates = resolver.GetCandidates(official, DownloadSourceMode.Mirror);

        Assert.Empty(candidates);
    }

    [Fact]
    public void GetCandidates_OfficialWithFallback_UnknownHost_ReturnsOnlyOfficial()
    {
        var resolver = new DownloadSourceResolver(MirrorBaseUrl);
        Uri official = new("https://example.com/some/file.bin");

        IReadOnlyList<Uri> candidates = resolver.GetCandidates(official, DownloadSourceMode.OfficialWithFallback);

        Assert.Single(candidates);
        Assert.Equal(official, candidates[0]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("/relative/path")]
    [InlineData("ftp://mirror.example.com")]
    public void Constructor_InvalidMirrorBaseUrl_ThrowsArgumentException(string mirrorBaseUrl)
    {
        Assert.Throws<ArgumentException>(() => new DownloadSourceResolver(mirrorBaseUrl));
    }
}
