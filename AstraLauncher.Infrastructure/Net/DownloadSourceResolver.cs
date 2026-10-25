using AstraLauncher.Core.Models;
using AstraLauncher.Core.Services;

namespace AstraLauncher.Infrastructure.Net;

/// <summary>Maps Mojang download URLs to the candidates allowed by <see cref="DownloadSourceMode"/>.</summary>
public sealed class DownloadSourceResolver : IDownloadSourceResolver
{
    private const string OfficialVersionManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    private const string VersionManifestPath = "mc/game/version_manifest_v2.json";
    private const string LauncherMetaHost = "launchermeta.mojang.com";
    private const string PistonMetaHost = "piston-meta.mojang.com";
    private const string PistonDataHost = "piston-data.mojang.com";
    private const string LibrariesHost = "libraries.minecraft.net";
    private const string ResourcesHost = "resources.download.minecraft.net";

    private readonly Uri _officialVersionManifestUri = new(OfficialVersionManifestUrl, UriKind.Absolute);
    private readonly Uri _mirrorBaseUri;
    private readonly Uri _mirrorVersionManifestUri;

    public DownloadSourceResolver(string mirrorBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(mirrorBaseUrl))
        {
            throw new ArgumentException("A mirror base URL is required.", nameof(mirrorBaseUrl));
        }

        string candidate = mirrorBaseUrl.Trim();
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? parsed))
        {
            throw new ArgumentException("The mirror base URL must be an absolute URL.", nameof(mirrorBaseUrl));
        }

        if (!IsHttp(parsed.Scheme))
        {
            throw new ArgumentException("The mirror base URL must use the HTTP or HTTPS scheme.", nameof(mirrorBaseUrl));
        }

        _mirrorBaseUri = NormalizeBaseUri(parsed);
        _mirrorVersionManifestUri = new Uri(_mirrorBaseUri, VersionManifestPath);
    }

    public Uri GetVersionManifestUri(DownloadSourceMode mode) =>
        mode == DownloadSourceMode.Mirror ? _mirrorVersionManifestUri : _officialVersionManifestUri;

    public IReadOnlyList<Uri> GetCandidates(Uri officialUri, DownloadSourceMode mode)
    {
        ArgumentNullException.ThrowIfNull(officialUri);
        if (!officialUri.IsAbsoluteUri)
        {
            throw new ArgumentException("An absolute official URI is required.", nameof(officialUri));
        }

        Uri? mirrorUri = MapToMirror(officialUri);
        return mode switch
        {
            DownloadSourceMode.Official => new[] { officialUri },
            DownloadSourceMode.Mirror => mirrorUri is null ? Array.Empty<Uri>() : new[] { mirrorUri },
            _ => mirrorUri is null ? new[] { officialUri } : new[] { officialUri, mirrorUri },
        };
    }

    private Uri? MapToMirror(Uri officialUri)
    {
        string host = officialUri.Host;
        if (IsMojangMetadataHost(host))
        {
            return Combine(officialUri.PathAndQuery.TrimStart('/'));
        }

        if (IsHost(host, LibrariesHost))
        {
            return Combine(string.Concat("maven/", officialUri.AbsolutePath.TrimStart('/')));
        }

        if (IsHost(host, ResourcesHost))
        {
            return Combine(string.Concat("assets/", officialUri.AbsolutePath.TrimStart('/')));
        }

        return null;
    }

    private Uri Combine(string relativePath) => new(_mirrorBaseUri, relativePath);

    private static bool IsMojangMetadataHost(string host) =>
        IsHost(host, LauncherMetaHost) || IsHost(host, PistonMetaHost) || IsHost(host, PistonDataHost);

    private static bool IsHost(string candidate, string expected) => string.Equals(candidate, expected, StringComparison.OrdinalIgnoreCase);

    private static bool IsHttp(string scheme) =>
        string.Equals(scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
        || string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    private static Uri NormalizeBaseUri(Uri parsed)
    {
        UriBuilder builder = new(parsed)
        {
            Query = string.Empty,
            Fragment = string.Empty,
        };
        string path = builder.Path.TrimEnd('/');
        builder.Path = path.Length == 0 ? "/" : string.Concat(path, "/");
        return builder.Uri;
    }
}
