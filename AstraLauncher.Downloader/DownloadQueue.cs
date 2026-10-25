using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace AstraLauncher.Downloader;

public sealed class DownloadQueueOptions
{
    public int MaximumConcurrentDownloads { get; init; } = 4;
    public int MaximumConcurrentDownloadsPerHost { get; init; } = 2;
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(45);
    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromSeconds(1);
}

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
    private bool _disposed;

    public DownloadQueue(HttpClient httpClient, DownloadQueueOptions? options = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? new DownloadQueueOptions();
        if (_options.MaximumConcurrentDownloads <= 0 || _options.MaximumConcurrentDownloadsPerHost <= 0) throw new ArgumentOutOfRangeException(nameof(options));
        _workers = Enumerable.Range(0, _options.MaximumConcurrentDownloads).Select(_ => Task.Run(WorkerAsync)).ToArray();
    }

    public IDownloadHandle Enqueue(DownloadRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Validate(request);
        string destination = Path.GetFullPath(request.DestinationPath);
        var item = new WorkItem(request with { DestinationPath = destination }, this);
        if (!_targets.TryAdd(destination, item)) throw new DownloadConflictException($"A download is already writing '{destination}'.");
        EnqueueInternal(item);
        return item;
    }

    private void EnqueueInternal(WorkItem item)
    {
        lock (_pendingLock) _pending.Enqueue(item, (int)item.Request.Priority);
        _available.Release();
    }

    private async Task WorkerAsync()
    {
        try
        {
            while (true)
            {
                await _available.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                WorkItem? item;
                lock (_pendingLock) item = _pending.Count > 0 ? _pending.Dequeue() : null;
                if (item is not null) await ProcessAsync(item).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    private async Task ProcessAsync(WorkItem item)
    {
        if (!item.TryBegin(out CancellationToken token)) return;
        SemaphoreSlim gate = _hostGates.GetOrAdd(item.Request.Source.Host, _ => new SemaphoreSlim(_options.MaximumConcurrentDownloadsPerHost));
        try
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try { await DownloadWithRetriesAsync(item, token).ConfigureAwait(false); }
            finally { gate.Release(); }
        }
        catch (OperationCanceledException) when (item.IsPauseRequested) { item.SetPaused(); }
        catch (OperationCanceledException) when (item.IsCancelRequested) { DeletePart(item); item.SetCancelled(); ReleaseTarget(item); }
        catch (Exception exception) { item.SetFailed(exception); ReleaseTarget(item); }
    }

    private async Task DownloadWithRetriesAsync(WorkItem item, CancellationToken cancellationToken)
    {
        Exception? last = null;
        for (int attempt = 1; attempt <= item.Request.MaximumAttempts; attempt++)
        {
            item.SetAttempt(attempt, attempt == 1 ? DownloadState.Downloading : DownloadState.Retrying);
            try
            {
                await DownloadOnceAsync(item, cancellationToken).ConfigureAwait(false);
                item.SetCompleted(); ReleaseTarget(item); return;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) when (attempt < item.Request.MaximumAttempts && IsRetryable(exception))
            {
                last = exception;
                if (exception is DownloadIntegrityException) DeletePart(item);
                item.SetRetrying(exception);
                TimeSpan delay = TimeSpan.FromMilliseconds(_options.InitialRetryDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
        throw last ?? new IOException("Download failed without a reported error.");
    }

    private async Task DownloadOnceAsync(WorkItem item, CancellationToken cancellationToken)
    {
        string part = PartPath(item);
        string? directory = Path.GetDirectoryName(item.Request.DestinationPath);
        if (directory is null) throw new IOException("Download destination does not have a parent directory.");
        Directory.CreateDirectory(directory);
        long existing = File.Exists(part) ? new FileInfo(part).Length : 0;
        using var request = new HttpRequestMessage(HttpMethod.Get, item.Request.Source);
        if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);
        using HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && existing > 0) { File.Delete(part); await DownloadOnceAsync(item, cancellationToken).ConfigureAwait(false); return; }
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"GET {item.Request.Source} returned {(int)response.StatusCode} ({response.ReasonPhrase}).", null, response.StatusCode);
        bool append = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (!append && existing > 0) { File.Delete(part); existing = 0; }
        long? total = response.Content.Headers.ContentRange?.Length ?? (response.Content.Headers.ContentLength is long length ? length + existing : null);
        item.SetProgress(existing, total);
        await using Stream input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        await using FileStream output = new(part, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] buffer = new byte[81920]; long received = existing; int read;
        while ((read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
            received += read; item.SetProgress(received, total);
        }
        await output.FlushAsync(timeout.Token).ConfigureAwait(false);
        await output.DisposeAsync().ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(item.Request.ExpectedSha1))
        {
            await using FileStream hashStream = new(part, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            string actual = Convert.ToHexString(await SHA1.HashDataAsync(hashStream, timeout.Token).ConfigureAwait(false));
            if (!actual.Equals(item.Request.ExpectedSha1, StringComparison.OrdinalIgnoreCase)) throw new DownloadIntegrityException($"SHA-1 mismatch for '{item.Request.DestinationPath}'.");
        }
        File.Move(part, item.Request.DestinationPath, overwrite: true);
    }

    private static bool IsRetryable(Exception exception) => exception is HttpRequestException or IOException or DownloadIntegrityException or TaskCanceledException;
    private static string PartPath(WorkItem item) => item.Request.DestinationPath + ".astra.part";
    private static void DeletePart(WorkItem item) { string path = PartPath(item); if (File.Exists(path)) File.Delete(path); }
    private void ReleaseTarget(WorkItem item) => _targets.TryRemove(item.Request.DestinationPath, out _);
    private static void Validate(DownloadRequest request)
    {
        if (!request.Source.IsAbsoluteUri || request.Source.Scheme is not ("https" or "http")) throw new ArgumentException("Only absolute HTTP(S) download sources are supported.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.DestinationPath)) throw new ArgumentException("A destination path is required.", nameof(request));
        if (request.MaximumAttempts <= 0) throw new ArgumentOutOfRangeException(nameof(request));
    }
    public async ValueTask DisposeAsync() { if (_disposed) return; _disposed = true; _shutdown.Cancel(); _available.Release(_workers.Length); await Task.WhenAll(_workers).ConfigureAwait(false); _shutdown.Dispose(); _available.Dispose(); }

    private sealed class WorkItem : IDownloadHandle
    {
        private readonly DownloadQueue _owner; private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously); private readonly object _lock = new(); private CancellationTokenSource? _runCts; private DownloadProgress _progress;
        public WorkItem(DownloadRequest request, DownloadQueue owner) { Request = request; _owner = owner; _progress = new(request.Id, DownloadState.Queued, 0, null, 0, null); }
        public DownloadRequest Request { get; }
        public Guid Id => Request.Id;
        public DownloadProgress Progress { get { lock (_lock) return _progress; } }
        public event EventHandler<DownloadProgress>? ProgressChanged;
        public Task Completion => _completion.Task;
        public bool IsPauseRequested { get; private set; }
        public bool IsCancelRequested { get; private set; }
        public bool TryBegin(out CancellationToken token) { lock (_lock) { if (IsPauseRequested || IsCancelRequested || _progress.State is DownloadState.Completed or DownloadState.Failed or DownloadState.Cancelled) { token = default; return false; } _runCts = CancellationTokenSource.CreateLinkedTokenSource(_owner._shutdown.Token); Set(DownloadState.Downloading); token = _runCts.Token; return true; } }
        public void Pause() { lock (_lock) { if (_progress.State is DownloadState.Completed or DownloadState.Failed or DownloadState.Cancelled) return; IsPauseRequested = true; Set(DownloadState.Paused); _runCts?.Cancel(); } }
        public void Resume() { lock (_lock) { if (!IsPauseRequested || IsCancelRequested) return; IsPauseRequested = false; Set(DownloadState.Queued); } _owner.EnqueueInternal(this); }
        public void Cancel() { lock (_lock) { if (_progress.State is DownloadState.Completed or DownloadState.Failed or DownloadState.Cancelled) return; IsCancelRequested = true; Set(DownloadState.Cancelled); _runCts?.Cancel(); if (_runCts is null) { _completion.TrySetCanceled(); _owner.ReleaseTarget(this); } } }
        public void SetAttempt(int attempt, DownloadState state) { lock (_lock) Set(state, attempt: attempt); }
        public void SetProgress(long bytes, long? total) { lock (_lock) Set(_progress.State, bytes, total); }
        public void SetRetrying(Exception exception) { lock (_lock) Set(DownloadState.Retrying, error: exception.Message); }
        public void SetPaused() { lock (_lock) Set(DownloadState.Paused); }
        public void SetCompleted() { lock (_lock) { Set(DownloadState.Completed); _completion.TrySetResult(); } }
        public void SetFailed(Exception exception) { lock (_lock) { Set(DownloadState.Failed, error: exception.Message); _completion.TrySetException(exception); } }
        public void SetCancelled() { lock (_lock) { Set(DownloadState.Cancelled); _completion.TrySetCanceled(); } }
        private void Set(DownloadState state, long? bytes = null, long? total = null, int? attempt = null, string? error = null) { _progress = new(Id, state, bytes ?? _progress.BytesReceived, total ?? _progress.TotalBytes, attempt ?? _progress.Attempt, error); ProgressChanged?.Invoke(this, _progress); }
    }
}
