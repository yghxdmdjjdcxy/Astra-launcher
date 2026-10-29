namespace AstraLauncher.Game.Metadata;

public sealed class VersionMetadataResolver(string minecraftDirectory)
{
    private readonly string _minecraftDirectory = Path.GetFullPath(minecraftDirectory);

    public async Task<MinecraftVersionMetadata> ResolveAsync(string versionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);
        return await ResolveAsync(versionId, new HashSet<string>(StringComparer.Ordinal), cancellationToken).ConfigureAwait(false);
    }

    private async Task<MinecraftVersionMetadata> ResolveAsync(string versionId, HashSet<string> ancestry, CancellationToken cancellationToken)
    {
        ValidateVersionId(versionId);
        if (!ancestry.Add(versionId)) throw new VersionMetadataException($"Version inheritance contains a cycle at '{versionId}'.");
        try
        {
            string path = Path.Combine(_minecraftDirectory, "versions", versionId, $"{versionId}.json");
            await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            MinecraftVersionMetadata child = await VersionMetadataParser.ParseAsync(stream, path, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(child.InheritsFrom)) return child;
            MinecraftVersionMetadata parent = await ResolveAsync(child.InheritsFrom, ancestry, cancellationToken).ConfigureAwait(false);
            return Merge(parent, child);
        }
        finally
        {
            ancestry.Remove(versionId);
        }
    }

    public static MinecraftVersionMetadata Merge(MinecraftVersionMetadata parent, MinecraftVersionMetadata child)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(child);
        var downloads = new Dictionary<string, DownloadArtifact>(parent.Downloads, StringComparer.Ordinal);
        foreach ((string key, DownloadArtifact value) in child.Downloads) downloads[key] = value;
        return new MinecraftVersionMetadata(
            child.Id, child.InheritsFrom, child.MainClass ?? parent.MainClass, child.AssetIndex ?? parent.AssetIndex, child.JavaVersion ?? parent.JavaVersion,
            MergeLibraries(parent.Libraries, child.Libraries),
            new VersionArguments(parent.Arguments.Game.Concat(child.Arguments.Game).ToArray(), parent.Arguments.Jvm.Concat(child.Arguments.Jvm).ToArray()),
            child.LegacyMinecraftArguments ?? parent.LegacyMinecraftArguments, downloads, child.Logging ?? parent.Logging);
    }

    private static IReadOnlyList<LibraryDescriptor> MergeLibraries(IReadOnlyList<LibraryDescriptor> parent, IReadOnlyList<LibraryDescriptor> child)
    {
        var ordered = new List<LibraryDescriptor>();
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (LibraryDescriptor library in parent.Concat(child))
        {
            string key = LibraryIdentity(library.Name);
            if (positions.TryGetValue(key, out int position)) ordered[position] = library;
            else { positions[key] = ordered.Count; ordered.Add(library); }
        }
        return ordered;
    }

    private static string LibraryIdentity(string coordinate)
    {
        string[] segments = coordinate.Split(':');
        // Include the classifier: modern versions list base and natives variants with the same group:artifact
        // (for example org.lwjgl:lwjgl:3.3.3 and org.lwjgl:lwjgl:3.3.3:natives-windows-x86). Deduplicating only by
        // group:artifact would drop every variant but the last one and break inherited loader launches.
        if (segments.Length >= 4)
        {
            return $"{segments[0]}:{segments[1]}:{segments[3]}";
        }

        return segments.Length >= 2 ? $"{segments[0]}:{segments[1]}" : coordinate;
    }

    private static void ValidateVersionId(string versionId)
    {
        if (versionId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || versionId.Contains(Path.DirectorySeparatorChar) || versionId.Contains(Path.AltDirectorySeparatorChar) || versionId is "." or "..")
            throw new ArgumentException("Version IDs must be a single directory-safe name.", nameof(versionId));
    }
}
