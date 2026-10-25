using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace AstraLauncher.Downloader;

public sealed class DownloadQueueOptions
{
    public int MaximumConcurrentDownloads { get; init; } = 4;
    public int MaximumConcurrentDownloadsPerHost { get; init; } = 2;

    /// <summary>Budget for establishing the connection and receiving response headers.</summary>
    public TimeSpan RequestHeadersTimeout { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>Maximum time to wait for the next chunk of body data. The timer restarts after every successful read.</summary>
    public TimeSpan ReadIdleTimeout { get; init; } = TimeSpan.FromSeconds(45);

    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaximumRetryDelay { get; init; } = TimeSpan.FromSeconds(30);
    public int ProgressReportIntervalMilliseconds { get; init; } = 100;
}

/// <summary>
/// Bounded-concurrency priority download queue with resume, integrity checks and pause/resume/cancel support.
/// The supplied <see cref="HttpClient"/> should use <see cref="Timeout.InfiniteTimeSpan"/>; this queue owns its own timeouts.
/// </summary>
public sealed class DownloadQueue : IAsyncDisposable
{
    private readonly HttpClient _httpClient;
    private readonly DownloadQueueOptions _options;
    private readonly PriorityQueue<WorkItem, int> _pending = new();
    private readonly object _pendingLock = new();
    private readonly SemaphoreSlim _available = new(0);
    private readonly ConcurrentDictionary<string, WorkItem> _targets = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _hostGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task[] _workers;
    private readonly long _progressIntervalTicks;
    private int _disposed;

    public DownloadQueue(HttpClient httpClient, DownloadQueueOptions? options = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? new DownloadQueueOptions();
        if (_options.MaximumConcurrentDownloads <= 0) throw new ArgumentOutOfRangeException(nameof(options), "MaximumConcurrentDownloads must be positive.");
        if (_options.MaximumConcurrentDownloadsPerHost <= 0) throw new ArgumentOutOfRangeException(nameof(options), "MaximumConcurrentDownloadsPerHost must be positive.");
        if (_options.RequestHeadersTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options), "RequestHeadersTimeout must be positive.");
        if (_options.ReadIdleTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options), "ReadIdleTimeout must be positive.");
        if (_options.ProgressReportIntervalMilliseconds < 0) throw new ArgumentOutOfRangeException(nameof(options), "ProgressReportIntervalMilliseconds must not be negative.");
        _progressIntervalTicks = (long)(Stopwatch.Frequency * (_options.ProgressReportIntervalMilliseconds / 1000.0));
        _workers = Enumerable.Range(0, _options.MaximumConcurrentDownloads).Select(_ => Task.Run(WorkerLoopAsync)).ToArray();
    }

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public IDownloadHandle Enqueue(DownloadRequest request)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        Validate(request);
        string destination = Path.GetFullPath(request.DestinationPath);
        var item = new WorkItem(request with { DestinationPath = destination }, this);
        if (!_targets.TryAdd(destination, item)) throw new DownloadConflictException($"A download is already writing '{destination}'.");
        EnqueueInternal(item);
        return item;
    }

    private void EnqueueInternal(WorkItem item)
    {
        if (IsDisposed) return;
        lock (_pendingLock) _pending.Enqueue(item, (int)item.Request.Priority);
        try
        {
            _available.Release();
        }
        catch (ObjectDisposedException)
        {
            // The queue was disposed concurrently; the item is settled by the dispose path.
        }
    }

    private async Task WorkerLoopAsync()
    {
        while (true)
        {
            try
            {
                await _available.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            WorkItem? item = null;
            lock (_pendingLock)
            {
                if (_pending.Count > 0) _pending.TryDequeue(out item, out _);
            }

            if (item is null) continue;

            try
            {
                await ProcessAsync(item).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // A worker must never die silently: settle the item and keep serving the queue.
                item.FailUnexpected(exception);
            }
        }
    }

    private async Task ProcessAsync(WorkItem item)
    {
        if (!item.TryBegin(out CancellationToken token, out int generation)) return;

        SemaphoreSlim? gate = null;
        try
        {
            gate = _hostGates.GetOrAdd(item.Request.Source.Host, _ => new SemaphoreSlim(_options.MaximumConcurrentDownloadsPerHost));
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await DownloadWithRetriesAsync(item, token).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }

            if (item.MarkCompleted(generation)) ReleaseTarget(item);
        }
        catch (OperationCanceledException)
        {
            QueueCancellationReason reason = item.GetCancellationReason(generation);
            switch (reason)
            {
                case QueueCancellationReason.Cancel:
                    DeletePartFiles(item);
                    if (item.MarkCancelled(generation)) ReleaseTarget(item);
                    break;
                case QueueCancellationReason.Pause:
                    item.MarkPaused(generation);
                    break;
                case QueueCancellationReason.Shutdown:
                    item.MarkCancelled(generation);
                    break;
                default:
                    if (item.MarkFailed(generation, new TaskCanceledException("The download was cancelled unexpectedly."))) ReleaseTarget(item);
                    break;
            }
        }
        catch (Exception exception)
        {
            if (item.MarkFailed(generation, exception)) ReleaseTarget(item);
        }
    }

    private async Task DownloadWithRetriesAsync(WorkItem item, CancellationToken cancellationToken)
    {
        DownloadRequest request = item.Request;
        for (int attempt = 1; attempt <= request.MaximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            item.BeginAttempt(attempt);
            try
            {
                await DownloadOnceAsync(item, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (attempt < request.MaximumAttempts && IsRetryable(exception))
            {
                item.NotifyRetry(attempt, exception);
                if (exception is DownloadIntegrityException integrity && integrity.DeletePartialFile) DeletePartFiles(item);
                TimeSpan delay = GetRetryDelay(exception, attempt);
                if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new DownloadHttpException(HttpStatusCode.RequestTimeout, $"Download failed after {request.MaximumAttempts} attempt(s).");
    }

    private async Task DownloadOnceAsync(WorkItem item, CancellationToken cancellationToken)
    {
        DownloadRequest request = item.Request;
        string destination = request.DestinationPath;
        string part = PartPath(item);
        string metadataPath = MetadataPath(item);
        string? directory = Path.GetDirectoryName(destination);
        if (directory is null) throw new IOException("Download destination does not have a parent directory.");
        Directory.CreateDirectory(directory);

        long existing = File.Exists(part) ? new FileInfo(part).Length : 0;
        PartMetadata? metadata = existing > 0 ? await ReadPartMetadataAsync(metadataPath, cancellationToken).ConfigureAwait(false) : null;

        if (request.ExpectedSize is long expectedSize)
        {
            if (existing > expectedSize)
            {
                DeletePartFiles(item);
                existing = 0;
                metadata = null;
            }
            else if (existing == expectedSize && existing > 0)
            {
                bool verified = true;
                if (!string.IsNullOrWhiteSpace(request.ExpectedSha1))
                {
                    string actualHash = await ComputeSha1Async(part, cancellationToken).ConfigureAwait(false);
                    verified = actualHash.Equals(request.ExpectedSha1, StringComparison.OrdinalIgnoreCase);
                }

                if (verified)
                {
                    item.SetProgress(existing, existing);
                    File.Move(part, destination, overwrite: true);
                    TryDeleteFile(metadataPath);
                    return;
                }

                DeletePartFiles(item);
                existing = 0;
                metadata = null;
            }
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, request.Source);
        if (existing > 0)
        {
            httpRequest.Headers.Range = new RangeHeaderValue(existing, null);
            if (metadata?.EntityTag is string entityTag && EntityTagHeaderValue.TryParse(entityTag, out EntityTagHeaderValue? parsedTag))
            {
                httpRequest.Headers.IfRange = new RangeConditionHeaderValue(parsedTag);
            }
            else if (metadata?.LastModified is DateTimeOffset lastModified)
            {
                httpRequest.Headers.IfRange = new RangeConditionHeaderValue(lastModified);
            }
        }

        using var headersTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        headersTimeout.CancelAfter(_options.RequestHeadersTimeout);
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, headersTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DownloadTimeoutException($"Timed out waiting for a response from '{request.Source}'.", exception);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && existing > 0)
            {
                DeletePartFiles(item);
                throw new DownloadIntegrityException($"The server rejected the resume request for '{destination}'.", deletePartialFile: false);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new DownloadHttpException(response.StatusCode, $"GET {request.Source} returned {(int)response.StatusCode} ({response.ReasonPhrase}).", GetRetryAfter(response));
            }

            bool append = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
            if (append)
            {
                ContentRangeHeaderValue? contentRange = response.Content.Headers.ContentRange;
                if (contentRange?.From is not long from || from != existing)
                {
                    DeletePartFiles(item);
                    throw new DownloadIntegrityException($"The server returned an unexpected resume offset for '{destination}'.");
                }
            }
            else if (existing > 0)
            {
                // The server ignored the Range request: start over from byte zero.
                DeletePartFiles(item);
                existing = 0;
                metadata = null;
            }

            long? total = response.Content.Headers.ContentRange?.Length
                ?? (response.Content.Headers.ContentLength is long contentLength ? contentLength + existing : null);
            if (request.ExpectedSize is long expectedLength)
            {
                if (total is long reportedTotal && reportedTotal != expectedLength)
                {
                    DeletePartFiles(item);
                    throw new DownloadIntegrityException($"The server reported {reportedTotal} bytes for '{destination}' but {expectedLength} were expected.");
                }

                total ??= expectedLength;
            }

            if (total is long knownTotal && existing > knownTotal)
            {
                DeletePartFiles(item);
                throw new DownloadIntegrityException($"The resumable partial file for '{destination}' is larger than the expected size.");
            }

            await WritePartMetadataAsync(metadataPath, new PartMetadata(response.Headers.ETag?.ToString(), response.Content.Headers.LastModified, total), cancellationToken).ConfigureAwait(false);

            item.SetProgress(existing, total);
            await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            long received = existing;
            await using (FileStream output = new(part, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                byte[] buffer = new byte[81920];
                while (true)
                {
                    int read = await ReadWithIdleTimeoutAsync(input, buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    received += read;
                    if (total is long upperBound && received > upperBound)
                    {
                        throw new DownloadIntegrityException($"The response body for '{destination}' exceeded the expected size.", deletePartialFile: false);
                    }

                    item.SetProgress(received, total);
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (total is long expectedTotal && received != expectedTotal)
            {
                throw new DownloadIntegrityException($"The download of '{destination}' stopped at {received} of {expectedTotal} bytes.", deletePartialFile: false);
            }

            if (request.ExpectedSize is long exactSize && received != exactSize)
            {
                throw new DownloadIntegrityException($"The download of '{destination}' stopped at {received} of {exactSize} bytes.", deletePartialFile: false);
            }

            if (!string.IsNullOrWhiteSpace(request.ExpectedSha1))
            {
                string actual = await ComputeSha1Async(part, cancellationToken).ConfigureAwait(false);
                if (!actual.Equals(request.ExpectedSha1, StringComparison.OrdinalIgnoreCase))
                {
                    throw new DownloadIntegrityException($"SHA-1 mismatch for '{destination}'.");
                }
            }

            File.Move(part, destination, overwrite: true);
            TryDeleteFile(metadataPath);
        }
    }

    private async Task<int> ReadWithIdleTimeoutAsync(Stream input, byte[] buffer, CancellationToken cancellationToken)
    {
        using var idleTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idleTimeout.CancelAfter(_options.ReadIdleTimeout);
        try
        {
            return await input.ReadAsync(buffer.AsMemory(), idleTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DownloadTimeoutException("Timed out while waiting for download data.", exception);
        }
    }

    private static async Task<string> ComputeSha1Async(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA1.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static async Task<PartMetadata?> ReadPartMetadataAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        try
        {
            string json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<PartMetadata>(json);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task WritePartMetadataAsync(string path, PartMetadata metadata, CancellationToken cancellationToken)
    {
        try
        {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(metadata), cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // Metadata only improves resume safety; a failure must not fail the download.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private TimeSpan GetRetryDelay(Exception exception, int attempt)
    {
        if (exception is DownloadHttpException { RetryAfter: TimeSpan retryAfter } && retryAfter > TimeSpan.Zero)
        {
            return retryAfter > _options.MaximumRetryDelay ? _options.MaximumRetryDelay : retryAfter;
        }

        double factor = Math.Pow(2, attempt - 1);
        double milliseconds = _options.InitialRetryDelay.TotalMilliseconds * factor;
        if (milliseconds > _options.MaximumRetryDelay.TotalMilliseconds) milliseconds = _options.MaximumRetryDelay.TotalMilliseconds;
        return milliseconds <= 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(milliseconds);
    }

    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        RetryConditionHeaderValue? retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is TimeSpan delta) return delta;
        if (retryAfter?.Date is DateTimeOffset date)
        {
            TimeSpan wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
    }

    private static bool IsRetryable(Exception exception) => exception switch
    {
        DownloadIntegrityException => true,
        DownloadTimeoutException => true,
        DownloadHttpException http => IsRetryableStatus(http.StatusCode),
        HttpRequestException => true,
        IOException => true,
        TaskCanceledException => true,
        _ => false,
    };

    private static bool IsRetryableStatus(HttpStatusCode? statusCode)
    {
        if (statusCode is not HttpStatusCode status) return true;
        int code = (int)status;
        return code >= 500 || code is 408 or 425 or 429;
    }

    private static string PartPath(WorkItem item) => item.Request.DestinationPath + ".astra.part";

    private static string MetadataPath(WorkItem item) => PartPath(item) + ".meta";

    private static void DeletePartFiles(WorkItem item)
    {
        TryDeleteFile(PartPath(item));
        TryDeleteFile(MetadataPath(item));
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void ReleaseTarget(WorkItem item) => _targets.TryRemove(item.Request.DestinationPath, out _);

    private static void Validate(DownloadRequest request)
    {
        if (!request.Source.IsAbsoluteUri || request.Source.Scheme is not ("https" or "http"))
        {
            throw new ArgumentException("Only absolute HTTP(S) download sources are supported.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.DestinationPath)) throw new ArgumentException("A destination path is required.", nameof(request));
        if (request.MaximumAttempts <= 0) throw new ArgumentOutOfRangeException(nameof(request), "MaximumAttempts must be positive.");
        if (request.ExpectedSize is <= 0) throw new ArgumentOutOfRangeException(nameof(request), "ExpectedSize must be positive when provided.");
        if (!string.IsNullOrWhiteSpace(request.ExpectedSha1) && request.ExpectedSha1.Length != 40)
        {
            throw new ArgumentException("ExpectedSha1 must be a 40 character hex SHA-1 digest.", nameof(request));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        foreach (WorkItem item in _targets.Values) item.AbortForShutdown();
        _targets.Clear();
        _shutdown.Cancel();
        try
        {
            _available.Release(_workers.Length);
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            await Task.WhenAll(_workers).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        foreach (SemaphoreSlim gate in _hostGates.Values) gate.Dispose();
        _hostGates.Clear();
        _shutdown.Dispose();
        _available.Dispose();
    }

    private enum QueueCancellationReason { None, Pause, Cancel, Shutdown }

    private sealed record PartMetadata(string? EntityTag, DateTimeOffset? LastModified, long? TotalLength);

    private sealed class WorkItem : IDownloadHandle
    {
        private readonly DownloadQueue _owner;
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _lock = new();
        private CancellationTokenSource? _runCts;
        private DownloadProgress _progress;
        private bool _running;
        private bool _pauseRequested;
        private bool _cancelRequested;
        private bool _resumeRequested;
        private QueueCancellationReason _cancellationReason = QueueCancellationReason.None;
        private int _generation;
        private int _attempt;
        private long _lastProgressTimestamp;

        public WorkItem(DownloadRequest request, DownloadQueue owner)
        {
            Request = request;
            _owner = owner;
            _progress = new DownloadProgress(request.Id, DownloadState.Queued, 0, request.ExpectedSize, 0, null);
        }

        public DownloadRequest Request { get; }

        public Guid Id => Request.Id;

        public DownloadProgress Progress
        {
            get
            {
                lock (_lock) return _progress;
            }
        }

        public event EventHandler<DownloadProgress>? ProgressChanged;

        public Task Completion => _completion.Task;

        public bool TryBegin(out CancellationToken token, out int generation)
        {
            DownloadProgress? update;
            lock (_lock)
            {
                token = default;
                generation = 0;
                if (_running || _pauseRequested || _cancelRequested || IsTerminalLocked()) return false;

                _running = true;
                _attempt = 1;
                generation = ++_generation;
                _cancellationReason = QueueCancellationReason.None;
                _runCts?.Dispose();
                _runCts = CancellationTokenSource.CreateLinkedTokenSource(_owner._shutdown.Token);
                update = _progress = MakeProgress(DownloadState.Downloading, null, null, 0, null);
                token = _runCts.Token;
            }

            Raise(update);
            return true;
        }

        public void BeginAttempt(int attempt)
        {
            DownloadProgress? update;
            lock (_lock)
            {
                if (!_running || IsTerminalLocked()) return;
                _attempt = attempt;
                update = _progress = MakeProgress(DownloadState.Downloading, null, null, null, null);
            }

            Raise(update);
        }

        public void NotifyRetry(int attempt, Exception exception)
        {
            DownloadProgress? update;
            lock (_lock)
            {
                if (!_running) return;
                _attempt = attempt;
                update = _progress = MakeProgress(DownloadState.Retrying, null, null, null, exception.Message);
            }

            Raise(update);
        }

        public void SetProgress(long bytes, long? total)
        {
            DownloadProgress? update = null;
            lock (_lock)
            {
                if (!_running || IsTerminalLocked()) return;
                long now = Stopwatch.GetTimestamp();
                bool alwaysRaise = total is long known && bytes >= known;
                if (!alwaysRaise && now - _lastProgressTimestamp < _owner._progressIntervalTicks)
                {
                    _progress = MakeProgress(_progress.State, bytes, total, null, null);
                    return;
                }

                _lastProgressTimestamp = now;
                update = _progress = MakeProgress(_progress.State, bytes, total, null, null);
            }

            Raise(update);
        }

        public bool MarkCompleted(int generation)
        {
            DownloadProgress? update;
            lock (_lock)
            {
                if (!IsCurrentRunLocked(generation)) return false;
                _running = false;
                update = _progress = MakeProgress(DownloadState.Completed, null, null, null, null);
                _completion.TrySetResult();
            }

            Raise(update);
            return true;
        }

        public bool MarkFailed(int generation, Exception exception)
        {
            DownloadProgress? update;
            lock (_lock)
            {
                if (!IsCurrentRunLocked(generation)) return false;
                _running = false;
                update = _progress = MakeProgress(DownloadState.Failed, null, null, null, exception.Message);
                _completion.TrySetException(exception);
            }

            Raise(update);
            return true;
        }

        public bool MarkCancelled(int generation)
        {
            DownloadProgress? update;
            lock (_lock)
            {
                if (!IsCurrentRunLocked(generation)) return false;
                _running = false;
                update = _progress = MakeProgress(DownloadState.Cancelled, null, null, null, null);
                _completion.TrySetCanceled();
            }

            Raise(update);
            return true;
        }

        public bool MarkPaused(int generation)
        {
            DownloadProgress? update;
            bool enqueue;
            lock (_lock)
            {
                if (!IsCurrentRunLocked(generation)) return false;
                _running = false;
                if (_resumeRequested)
                {
                    _resumeRequested = false;
                    _pauseRequested = false;
                    _cancellationReason = QueueCancellationReason.None;
                    update = _progress = MakeProgress(DownloadState.Queued, null, null, null, null);
                    enqueue = true;
                }
                else
                {
                    update = _progress = MakeProgress(DownloadState.Paused, null, null, null, null);
                    enqueue = false;
                }
            }

            Raise(update);
            if (enqueue) _owner.EnqueueInternal(this);
            return true;
        }

        public void Pause()
        {
            DownloadProgress? update = null;
            lock (_lock)
            {
                if (IsTerminalLocked() || _pauseRequested) return;
                _pauseRequested = true;
                if (_running)
                {
                    _cancellationReason = QueueCancellationReason.Pause;
                    _runCts?.Cancel();
                    return;
                }

                update = _progress = MakeProgress(DownloadState.Paused, null, null, null, null);
            }

            Raise(update);
        }

        public void Resume()
        {
            DownloadProgress? update = null;
            lock (_lock)
            {
                if (IsTerminalLocked() || !_pauseRequested || _cancelRequested) return;
                if (_running)
                {
                    _resumeRequested = true;
                    return;
                }

                _pauseRequested = false;
                _cancellationReason = QueueCancellationReason.None;
                update = _progress = MakeProgress(DownloadState.Queued, null, null, null, null);
            }

            Raise(update);
            _owner.EnqueueInternal(this);
        }

        public void Cancel()
        {
            DownloadProgress? update = null;
            bool release;
            lock (_lock)
            {
                if (IsTerminalLocked()) return;
                _cancelRequested = true;
                if (_running)
                {
                    _cancellationReason = QueueCancellationReason.Cancel;
                    _runCts?.Cancel();
                    return;
                }

                _running = false;
                update = _progress = MakeProgress(DownloadState.Cancelled, null, null, null, null);
                _completion.TrySetCanceled();
                release = true;
            }

            Raise(update);
            if (release) _owner.ReleaseTarget(this);
        }

        public void AbortForShutdown()
        {
            DownloadProgress? update = null;
            lock (_lock)
            {
                if (IsTerminalLocked()) return;
                _cancelRequested = true;
                _cancellationReason = QueueCancellationReason.Shutdown;
                if (_running) _runCts?.Cancel();
                _running = false;
                update = _progress = MakeProgress(DownloadState.Cancelled, null, null, null, null);
                _completion.TrySetCanceled();
            }

            Raise(update);
        }

        public void FailUnexpected(Exception exception)
        {
            DownloadProgress? update = null;
            bool release = false;
            lock (_lock)
            {
                if (IsTerminalLocked()) return;
                _running = false;
                update = _progress = MakeProgress(DownloadState.Failed, null, null, null, exception.Message);
                _completion.TrySetException(exception);
                release = true;
            }

            Raise(update);
            if (release) _owner.ReleaseTarget(this);
        }

        public QueueCancellationReason GetCancellationReason(int generation)
        {
            lock (_lock)
            {
                return generation == _generation ? _cancellationReason : QueueCancellationReason.None;
            }
        }

        private bool IsCurrentRunLocked(int generation) => _running && generation == _generation;

        private bool IsTerminalLocked() => _progress.State is DownloadState.Completed or DownloadState.Failed or DownloadState.Cancelled;

        private DownloadProgress MakeProgress(DownloadState state, long? bytes, long? total, int? attempt, string? error)
            => new(Id, state, bytes ?? _progress.BytesReceived, total ?? _progress.TotalBytes, attempt ?? _attempt, error);

        private void Raise(DownloadProgress? progress)
        {
            if (progress is null) return;
            EventHandler<DownloadProgress>? handler = ProgressChanged;
            if (handler is null) return;
            try
            {
                handler(this, progress);
            }
            catch (Exception)
            {
                // A faulty UI subscriber must never break the download worker.
            }
        }
    }
}
