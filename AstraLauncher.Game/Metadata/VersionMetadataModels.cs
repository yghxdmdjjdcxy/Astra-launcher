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
    IReadOnlyDictionary<string, DownloadArtifact> Downloads,
    VersionLogging? Logging = null);

public sealed record AssetIndexDescriptor(string Id, string? Sha1, long? Size, long? TotalSize, Uri? Url);
public sealed record JavaVersionRequirement(string Component, int MajorVersion);
public sealed record DownloadArtifact(string? Path, string? Sha1, long? Size, Uri? Url);
public sealed record LibraryDownloads(DownloadArtifact? Artifact, IReadOnlyDictionary<string, DownloadArtifact> Classifiers);
public sealed record LibraryDescriptor(string Name, LibraryDownloads? Downloads, IReadOnlyDictionary<string, string> Natives, IReadOnlyList<string> ExtractExcludes, IReadOnlyList<ArgumentRule> Rules);
public sealed record VersionArguments(IReadOnlyList<LaunchArgument> Game, IReadOnlyList<LaunchArgument> Jvm);
public sealed record LaunchArgument(IReadOnlyList<string> Values, IReadOnlyList<ArgumentRule> Rules);
public sealed record ArgumentRule(string Action, string? OperatingSystemName, string? OperatingSystemVersion, string? OperatingSystemArchitecture, IReadOnlyDictionary<string, bool> Features);

/// <summary>The logging configuration block of a version; only the client descriptor is exposed.</summary>
public sealed record VersionLogging(VersionLoggingClient Client);

/// <summary>Describes the log4j configuration file a version wants its client to use.</summary>
public sealed record VersionLoggingClient(string Argument, VersionLoggingFile File, string Type);

/// <summary>The downloadable log configuration file referenced by <see cref="VersionLoggingClient"/>.</summary>
public sealed record VersionLoggingFile(string Id, string? Sha1, long? Size, Uri? Url);

public sealed class VersionMetadataException(string message, string? sourcePath = null, Exception? innerException = null) : Exception(message, innerException)
{
    public string? SourcePath { get; } = sourcePath;
}
