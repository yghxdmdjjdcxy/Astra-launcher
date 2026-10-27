namespace AstraLauncher.Core.Models;

public sealed record GameInstance(Guid Id, string Name, string DirectoryPath, string VersionId, bool IsManagedDirectory);
public sealed record GameVersion(string Id, string Type, DateTimeOffset ReleaseTime);
public sealed record DownloadJob(Guid Id, Uri Source, string TargetPath, DownloadJobState State);
public enum DownloadJobState { Queued, Downloading, Paused, Completed, Failed, Cancelled }
