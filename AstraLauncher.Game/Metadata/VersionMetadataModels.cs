namespace AstraLauncher.Game.Metadata;

public sealed record MinecraftVersionMetadata(
    string Id,
    string? InheritsFrom,
    string? MainClass,
    AssetIndexDescriptor? AssetIndex,
    JavaVersionRequirement? JavaVersion,
    IReadOnlyList<LibraryDescriptor> Libraries,
    VersionArguments Arguments,
    string? LegacyMinecraftArguments,
    IReadOnlyDictionary<string, DownloadArtifact> Downloads);

public sealed record AssetIndexDescriptor(string Id, string? Sha1, long? Size, long? TotalSize, Uri? Url);
public sealed record JavaVersionRequirement(string Component, int MajorVersion);
public sealed record DownloadArtifact(string? Path, string? Sha1, long? Size, Uri? Url);
public sealed record LibraryDownloads(DownloadArtifact? Artifact, IReadOnlyDictionary<string, DownloadArtifact> Classifiers);
public sealed record LibraryDescriptor(string Name, LibraryDownloads? Downloads, IReadOnlyDictionary<string, string> Natives, IReadOnlyList<string> ExtractExcludes, IReadOnlyList<ArgumentRule> Rules);
public sealed record VersionArguments(IReadOnlyList<LaunchArgument> Game, IReadOnlyList<LaunchArgument> Jvm);
public sealed record LaunchArgument(IReadOnlyList<string> Values, IReadOnlyList<ArgumentRule> Rules);
public sealed record ArgumentRule(string Action, string? OperatingSystemName, string? OperatingSystemVersion, string? OperatingSystemArchitecture, IReadOnlyDictionary<string, bool> Features);

public sealed class VersionMetadataException(string message, string? sourcePath = null, Exception? innerException = null) : Exception(message, innerException)
{
    public string? SourcePath { get; } = sourcePath;
}
