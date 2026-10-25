using AstraLauncher.Core.Models;

namespace AstraLauncher.Core.Services;

/// <summary>Resolves official Minecraft URLs to the candidates allowed by a <see cref="DownloadSourceMode"/>.</summary>
public interface IDownloadSourceResolver
{
    /// <summary>Gets the primary version manifest URI for the supplied mode.</summary>
    Uri GetVersionManifestUri(DownloadSourceMode mode);

    /// <summary>Gets the download candidates for an official URI, in the order callers should try them.</summary>
    IReadOnlyList<Uri> GetCandidates(Uri officialUri, DownloadSourceMode mode);
}
