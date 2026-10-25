using System.Net;

namespace AstraLauncher.Downloader;

public enum DownloadPriority { Low = 2, Normal = 1, High = 0 }

public enum DownloadState { Queued, Downloading, Paused, Retrying, Completed, Failed, Cancelled }

/// <summary>Describes one file to download. <see cref="ExpectedSize"/> and <see cref="ExpectedSha1"/> enable integrity checks.</summary>
public sealed record DownloadRequest(
    Guid Id,
    Uri Source,
    string DestinationPath,
    string? ExpectedSha1 = null,
    long? ExpectedSize = null,
    DownloadPriority Priority = DownloadPriority.Normal,
    int MaximumAttempts = 3)
{
    public static DownloadRequest Create(Uri source, string destinationPath, string? expectedSha1 = null, long? expectedSize = null, DownloadPriority priority = DownloadPriority.Normal, int maximumAttempts = 3)
        => new(Guid.NewGuid(), source, destinationPath, expectedSha1, expectedSize, priority, maximumAttempts);
}

public sealed record DownloadProgress(Guid Id, DownloadState State, long BytesReceived, long? TotalBytes, int Attempt, string? ErrorMessage);

public sealed class DownloadConflictException(string message) : InvalidOperationException(message);

/// <summary>Raised when a downloaded file does not match the expected size or SHA-1.</summary>
public sealed class DownloadIntegrityException(string message, bool deletePartialFile = true) : IOException(message)
{
    public bool DeletePartialFile { get; } = deletePartialFile;
}

/// <summary>Raised for connect/header/idle-read timeouts so they can be retried separately from user cancellation.</summary>
public sealed class DownloadTimeoutException(string message, Exception? innerException = null) : IOException(message, innerException);

/// <summary>Raised for non-success HTTP status codes; carries the response status and optional Retry-After.</summary>
public sealed class DownloadHttpException(HttpStatusCode statusCode, string message, TimeSpan? retryAfter = null) : HttpRequestException(message, null, statusCode)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

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
