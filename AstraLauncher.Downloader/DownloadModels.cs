namespace AstraLauncher.Downloader;

public enum DownloadPriority { Low = 2, Normal = 1, High = 0 }
public enum DownloadState { Queued, Downloading, Paused, Retrying, Completed, Failed, Cancelled }

public sealed record DownloadRequest(Guid Id, Uri Source, string DestinationPath, string? ExpectedSha1 = null, DownloadPriority Priority = DownloadPriority.Normal, int MaximumAttempts = 3)
{
    public static DownloadRequest Create(Uri source, string destinationPath, string? expectedSha1 = null, DownloadPriority priority = DownloadPriority.Normal, int maximumAttempts = 3) => new(Guid.NewGuid(), source, destinationPath, expectedSha1, priority, maximumAttempts);
}

public sealed record DownloadProgress(Guid Id, DownloadState State, long BytesReceived, long? TotalBytes, int Attempt, string? ErrorMessage);
public sealed class DownloadConflictException(string message) : InvalidOperationException(message);
public sealed class DownloadIntegrityException(string message) : IOException(message);

public interface IDownloadHandle
{
    Guid Id { get; }
    DownloadProgress Progress { get; }
    event EventHandler<DownloadProgress>? ProgressChanged;
    void Pause();
    void Resume();
    void Cancel();
    Task Completion { get; }
}
