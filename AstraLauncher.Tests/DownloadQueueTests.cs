using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using AstraLauncher.Downloader;

namespace AstraLauncher.Tests;

public sealed class DownloadQueueTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AstraLauncher.DownloadTests", Guid.NewGuid().ToString("N"));

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task DownloadsAndVerifiesSha1()
    {
        byte[] data = Encoding.UTF8.GetBytes("astra-content");
        await using DownloadQueue queue = Queue(new DelegateHandler(_ => Response(HttpStatusCode.OK, data)));
        string target = Path.Combine(_root, "success.bin");
        IDownloadHandle job = queue.Enqueue(DownloadRequest.Create(new Uri("https://unit.test/file"), target, Sha1(data), data.Length));
        await job.Completion;
        Assert.Equal(data, await File.ReadAllBytesAsync(target));
        Assert.Equal(DownloadState.Completed, job.Progress.State);
    }

    [Fact]
    public async Task DownloadsWithoutHashOrSize()
    {
        byte[] data = Encoding.UTF8.GetBytes("no-integrity-data");
        await using DownloadQueue queue = Queue(new DelegateHandler(_ => Response(HttpStatusCode.OK, data)));
        string target = Path.Combine(_root, "plain.bin");
        IDownloadHandle job = queue.Enqueue(DownloadRequest.Create(new Uri("https://unit.test/plain"), target));
        await job.Completion;
        Assert.Equal(data, await File.ReadAllBytesAsync(target));
    }

    [Fact]
    public async Task RetriesTransientHttpFailure()
    {
        int calls = 0;
        byte[] data = [1, 2, 3];
        await using DownloadQueue queue = Queue(new DelegateHandler(_ => Interlocked.Increment(ref calls) == 1 ? Response(HttpStatusCode.ServiceUnavailable, []) : Response(HttpStatusCode.OK, data)));
        IDownloadHandle job = queue.Enqueue(DownloadRequest.Create(new Uri("https://unit.test/retry"), Path.Combine(_root, "retry.bin"), maximumAttempts: 2));
        await job.Completion;
        Assert.Equal(2, calls);
        Assert.Equal(DownloadState.Completed, job.Progress.State);
    }

    [Fact]
    public async Task FailsFastOnNonRetryableHttpStatus()
    {
        int calls = 0;
        await using DownloadQueue queue = Queue(new DelegateHandler(_ =>
        {
            Interlocked.Increment(ref calls);
            return Response(HttpStatusCode.NotFound, []);
        }));
        IDownloadHandle job = queue.Enqueue(DownloadRequest.Create(new Uri("https://unit.test/missing"), Path.Combine(_root, "missing.bin"), maximumAttempts: 3));
        await Assert.ThrowsAsync<DownloadHttpException>(() => job.Completion);
        Assert.Equal(1, calls);
        Assert.Equal(DownloadState.Failed, job.Progress.State);
    }

    [Fact]
    public async Task ResumesExistingPartWhenServerReturnsPartialContent()
    {
        string target = Path.Combine(_root, "resume.bin");
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(target + ".astra.part", "hello");
        await using DownloadQueue queue = Queue(new DelegateHandler(request =>
        {
            Assert.Equal("bytes=5-", request.Headers.Range!.ToString());
            HttpResponseMessage response = Response(HttpStatusCode.PartialContent, Encoding.UTF8.GetBytes(" world"));
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(5, 10, 11);
            return response;
        }));
        IDownloadHandle job = queue.Enqueue(DownloadRequest.Create(new Uri("https://unit.test/resume"), target));
        await job.Completion;
        Assert.Equal("hello world", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task RestartsFromScratchWhenServerIgnoresRange()
    {
        string target = Path.Combine(_root, "ignored-range.bin");
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(target + ".astra.part", "OLD");
        await using DownloadQueue queue = Queue(new DelegateHandler(request =>
        {
            Assert.NotNull(request.Headers.Range);
            return Response(HttpStatusCode.OK, Encoding.UTF8.GetBytes("NEWDATA"));
        }));
        IDownloadHandle job = queue.Enqueue(DownloadRequest.Create(new Uri("https://unit.test/ignored"), target));
        await job.Completion;
        Assert.Equal("NEWDATA", await File.ReadAllTextAsync(target));
        Assert.False(File.Exists(target + ".astra.part"));
    }

    [Fact]
    public async Task RejectsUnexpectedResumeOffset()
    {
        string target = Path.Combine(_root, "bad-offset.bin");
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(target + ".astra.part", "hello");
        await using DownloadQueue queue = Queue(new DelegateHandler(_ =>
        {
            HttpResponseMessage response = Response(HttpStatusCode.PartialContent, Encoding.UTF8.GetBytes(" world"));
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 10, 11);
            return response;
        }));
        IDownloadHandle job = queue.Enqueue(DownloadRequest.Create(new Uri("https://unit.test/bad-offset"), target, maximumAttempts: 1));
        await Assert.ThrowsAsync<DownloadIntegrityException>(() => job.Completion);
        Assert.False(File.Exists(target + ".astra.part"));
    }

    [Fact]
    public async Task FailsWhenServerSizeDoesNotMatchExpected()
    {
        byte[] data = [1, 2, 3];
        await using DownloadQueue queue = Queue(new DelegateHandler(_ => Response(HttpStatusCode.OK, data)));
        IDownloadHandle job = queue.Enqueue(DownloadRequest.Create(new Uri("https://unit.test/size"), Path.Combine(_root, "size.bin"), expectedSize: 10, maximumAttempts: 1));
        await Assert.ThrowsAsync<DownloadIntegrityException>(() => job.Completion);
        Assert.Equal(DownloadState.Failed, job.Progress.State);
    }

    [Fact]
    public async Task RetriesIdleTimeoutDuringBody()
    {
        byte[] data = [7, 8, 9];
        int calls = 0;
        await using DownloadQueue queue = Queue(
            new DelegateHandler(_ => Interlocked.Increment(ref calls) == 1
                ? StreamResponse(new SlowStream(data, data.Length, TimeSpan.FromMilliseconds(400)))
                : Response(HttpStatusCode.OK, data)),
            new DownloadQueueOptions
            {
                MaximumConcurrentDownloads = 1,
                MaximumConcurrentDownloadsPerHost = 1,
                InitialRetryDelay = TimeSpan.FromMilliseconds(1),
                MaximumRetryDelay = TimeSpan.FromMilliseconds(5),
                ReadIdleTimeout = TimeSpan.FromMilliseconds(80),
            });
        IDownloadHandle job = queue.Enqueue(DownloadRequest.Create(new Uri("https://unit.test/idle-timeout"), Path.Combine(_root, "idle-timeout.bin"), maximumAttempts: 2));
        await job.Completion;
        Assert.Equal(2, calls);
        Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(_root, "idle-timeout.bin")));
    }

    [Fact]
    public async Task SucceedsWhenBodyOutlivesHeaderTimeout()
    {
        byte[] data = Encoding.UTF8.GetBytes(new string('x', 6 * 32));
        await using DownloadQueue queue = Queue(
            new DelegateHandler(_ => StreamResponse(new SlowStream(data, 32, TimeSpan.FromMilliseconds(40)))),
            new DownloadQueueOptions
            {
                MaximumConcurrentDownloads = 1,
                MaximumConcurrentDownloadsPerHost = 1,
                RequestHeadersTimeout = TimeSpan.FromMilliseconds(50),
                ReadIdleTimeout = TimeSpan.FromSeconds(5),
                InitialRetryDelay = TimeSpan.FromMilliseconds(1),
            });
        string target = Path.Combine(_root, "slow-body.bin");
        IDownloadHandle job = queue.Enqueue(DownloadRequest.Create(new Uri("https://unit.test/slow-body"), target));
        await job.Completion;
        Assert.Equal(data, await File.ReadAllBytesAsync(target));
    }

    [Fact]
    public async Task PauseAndResumeCompletes()
    {
        byte[] data = new byte[2000];
        Random.Shared.NextBytes(data);
        await using DownloadQueue queue = Queue(new RangeHandler(data, chunkSize: 64, chunkDelay: TimeSpan.FromMilliseconds(15)));
        string target = Path.Combine(_root, "pause-resume.bin");
        IDownloadHandle job = queue.Enqueue(DownloadRequest.Create(new Uri("https://unit.test/pause"), target));
        await WaitUntilAsync(() => job.Progress.BytesReceived > 0);

        job.Pause();
        await WaitUntilAsync(() => job.Progress.State == DownloadState.Paused);

        job.Resume();
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(DownloadState.Completed, job.Progress.State);
        Assert.Equal(data, await File.ReadAllBytesAsync(target));
    }

    [Fact]
    public async Task CancelMarksHandleCancelled()
    {
        byte[] data = new byte[4000];
        await using DownloadQueue queue = Queue(new RangeHandler(data, chunkSize: 64, chunkDelay: TimeSpan.FromMilliseconds(10)));
        string target = Path.Combine(_root, "cancel.bin");
        IDownloadHandle job = queue.Enqueue(DownloadRequest.Create(new Uri("https://unit.test/cancel"), target));
        await WaitUntilAsync(() => job.Progress.BytesReceived > 0);

        job.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => job.Completion);
        Assert.Equal(DownloadState.Cancelled, job.Progress.State);
    }

    [Fact]
    public async Task DisposeCancelsOutstandingDownloads()
    {
        DownloadQueue queue = Queue(new BlockingHandler());
        IDownloadHandle first = queue.Enqueue(DownloadRequest.Create(new Uri("https://unit.test/blocked-1"), Path.Combine(_root, "blocked-1.bin")));
        IDownloadHandle second = queue.Enqueue(DownloadRequest.Create(new Uri("https://unit.test/blocked-2"), Path.Combine(_root, "blocked-2.bin")));
        await WaitUntilAsync(() => first.Progress.State == DownloadState.Downloading && second.Progress.State == DownloadState.Downloading);

        await queue.DisposeAsync();

        Assert.True(first.Completion.IsCanceled);
        Assert.True(second.Completion.IsCanceled);
    }

    [Fact]
    public async Task ReportsProgressThroughCompletion()
    {
        byte[] data = Encoding.UTF8.GetBytes("progress");
        var states = new List<DownloadState>();
        // Delay the response so the test can subscribe to ProgressChanged before the tiny download completes;
        // without it the "Downloading" event can fire before the handler is attached.
        await using DownloadQueue queue = Queue(new AsyncDelegateHandler(async _ =>
        {
            await Task.Delay(50).ConfigureAwait(false);
            return Response(HttpStatusCode.OK, data);
        }));
        IDownloadHandle job = queue.Enqueue(DownloadRequest.Create(new Uri("https://unit.test/progress"), Path.Combine(_root, "progress.bin")));
        job.ProgressChanged += (_, progress) =>
        {
            lock (states) states.Add(progress.State);
        };
        await job.Completion;
        Assert.Equal(DownloadState.Completed, job.Progress.State);
        lock (states)
        {
            Assert.Contains(DownloadState.Downloading, states);
        }
    }

    [Fact]
    public async Task RejectsConcurrentWritersForSameTarget()
    {
        var blocker = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using DownloadQueue queue = Queue(new AsyncDelegateHandler(_ => blocker.Task));
        string target = Path.Combine(_root, "same.bin");
        IDownloadHandle job = queue.Enqueue(DownloadRequest.Create(new Uri("https://unit.test/one"), target));
        Assert.Throws<DownloadConflictException>(() => queue.Enqueue(DownloadRequest.Create(new Uri("https://unit.test/two"), target)));
        blocker.SetResult(Response(HttpStatusCode.OK, [1]));
        await job.Completion;
    }

    [Fact]
    public async Task RejectsInvalidRequests()
    {
        await using DownloadQueue queue = Queue(new DelegateHandler(_ => Response(HttpStatusCode.OK, [])));
        Assert.Throws<ArgumentException>(() => queue.Enqueue(DownloadRequest.Create(new Uri("https://unit.test/x"), Path.Combine(_root, "x.bin"), "0000")));
        Assert.Throws<ArgumentException>(() => queue.Enqueue(DownloadRequest.Create(new Uri("ftp://unit.test/x"), Path.Combine(_root, "x.bin"))));
        Assert.Throws<ArgumentOutOfRangeException>(() => queue.Enqueue(DownloadRequest.Create(new Uri("https://unit.test/x"), Path.Combine(_root, "x.bin"), maximumAttempts: 0)));
    }

    [Fact]
    public async Task SegmentedDownloadUsesParallelRangeRequests()
    {
        byte[] data = new byte[2_621_440]; // 2.5 MiB: two segments with the 2 MiB minimum segment size.
        Random.Shared.NextBytes(data);
        SegmentedHandler handler = new(data);
        await using DownloadQueue queue = Queue(handler, SegmentedOptions());
        string target = Path.Combine(_root, "segmented.bin");
        string sha1 = Sha1(data);

        IDownloadHandle job = queue.Enqueue(DownloadRequest.Create(new Uri("https://unit.test/segmented"), target, sha1, data.Length));
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(DownloadState.Completed, job.Progress.State);
        Assert.Equal(data, await File.ReadAllBytesAsync(target));
        Assert.Equal(sha1, Sha1(await File.ReadAllBytesAsync(target)));
        Assert.False(File.Exists(target + ".astra.part"));

        List<(long From, long To)> ranges = handler.Ranges.Where(range => range.To > range.From).ToList();
        Assert.True(ranges.Count >= 2, $"Expected at least two range requests but saw {ranges.Count}.");
        Assert.Equal(data.Length, ranges.Sum(range => range.To - range.From + 1));
    }

    [Fact]
    public async Task FallsBackToSingleStreamWhenServerIgnoresRanges()
    {
        byte[] data = new byte[2_621_440];
        Random.Shared.NextBytes(data);
        SegmentedHandler handler = new(data, honorRanges: false);
        await using DownloadQueue queue = Queue(handler, SegmentedOptions());
        string target = Path.Combine(_root, "no-ranges.bin");

        IDownloadHandle job = queue.Enqueue(DownloadRequest.Create(new Uri("https://unit.test/no-ranges"), target, expectedSize: data.Length));
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(DownloadState.Completed, job.Progress.State);
        Assert.Equal(data, await File.ReadAllBytesAsync(target));
        Assert.NotEmpty(handler.Ranges); // The segmented path probed the server with a Range request first.
        Assert.InRange(handler.RequestCount, 1, 2); // The probe response body is reused instead of requesting the file again.
    }

    [Fact]
    public async Task SmallFilesUseASinglePlainRequest()
    {
        byte[] data = new byte[512 * 1024];
        Random.Shared.NextBytes(data);
        SegmentedHandler handler = new(data);
        await using DownloadQueue queue = Queue(handler, SegmentedOptions());
        string target = Path.Combine(_root, "small-download.bin");

        IDownloadHandle job = queue.Enqueue(DownloadRequest.Create(new Uri("https://unit.test/small"), target, expectedSize: data.Length));
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(DownloadState.Completed, job.Progress.State);
        Assert.Equal(data, await File.ReadAllBytesAsync(target));
        Assert.Empty(handler.Ranges);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task FailsWhenSegmentContentRangeDoesNotMatch()
    {
        byte[] data = new byte[2_621_440];
        Random.Shared.NextBytes(data);
        SegmentedHandler handler = new(data, corruptContentRange: true);
        await using DownloadQueue queue = Queue(handler, SegmentedOptions());
        string target = Path.Combine(_root, "bad-segment.bin");

        IDownloadHandle job = queue.Enqueue(DownloadRequest.Create(new Uri("https://unit.test/bad-segment"), target, expectedSize: data.Length, maximumAttempts: 1));
        await Assert.ThrowsAsync<DownloadIntegrityException>(() => job.Completion);

        Assert.Equal(DownloadState.Failed, job.Progress.State);
        Assert.False(File.Exists(target));
        Assert.False(File.Exists(target + ".astra.part"));
        Assert.True(handler.Ranges.Count(range => range.To > range.From) >= 4, "Expected the segment requests to be retried.");
    }

    [Fact]
    public async Task CancellingSegmentedDownloadLeavesNoPartialFile()
    {
        byte[] data = new byte[3 * 1024 * 1024];
        Random.Shared.NextBytes(data);
        SegmentedHandler handler = new(data, chunkSize: 8 * 1024, chunkDelay: TimeSpan.FromMilliseconds(5));
        await using DownloadQueue queue = Queue(handler, SegmentedOptions());
        string target = Path.Combine(_root, "segmented-cancel.bin");
        IDownloadHandle job = queue.Enqueue(DownloadRequest.Create(new Uri("https://unit.test/segmented-cancel"), target, expectedSize: data.Length));
        await WaitUntilAsync(() => job.Progress.BytesReceived > 0);

        job.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => job.Completion);
        Assert.Equal(DownloadState.Cancelled, job.Progress.State);
        Assert.False(File.Exists(target));
        Assert.False(File.Exists(target + ".astra.part"));
    }

    [Fact]
    public async Task SegmentedDownloadHonoursMaximumConcurrentSegments()
    {
        byte[] data = new byte[5 * 1024 * 1024];
        Random.Shared.NextBytes(data);
        SegmentedHandler handler = new(data, headerDelay: TimeSpan.FromMilliseconds(25));
        await using DownloadQueue queue = Queue(handler, SegmentedOptions(segmentsPerFile: 8, minimumSegmentSizeBytes: 1024 * 1024, maximumConcurrentSegments: 2));
        string target = Path.Combine(_root, "segmented-limit.bin");

        IDownloadHandle job = queue.Enqueue(DownloadRequest.Create(new Uri("https://unit.test/segmented-limit"), target, expectedSize: data.Length));
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(data, await File.ReadAllBytesAsync(target));
        Assert.InRange(handler.MaximumConcurrentSegmentsSeen, 1, 2);
        Assert.Equal(6, handler.RequestCount); // One probe plus five segments of 1 MiB each.
    }

    private static DownloadQueue Queue(HttpMessageHandler handler, DownloadQueueOptions? options = null)
        => new(new HttpClient(handler), options ?? new DownloadQueueOptions
        {
            MaximumConcurrentDownloads = 2,
            MaximumConcurrentDownloadsPerHost = 1,
            InitialRetryDelay = TimeSpan.FromMilliseconds(1),
            MaximumRetryDelay = TimeSpan.FromMilliseconds(5),
        });

    private static DownloadQueueOptions SegmentedOptions(
        int segmentsPerFile = 4,
        long minimumSegmentSizeBytes = 2 * 1024 * 1024,
        int maximumConcurrentSegments = 8)
        => new()
        {
            MaximumConcurrentDownloads = 1,
            MaximumConcurrentDownloadsPerHost = 1,
            InitialRetryDelay = TimeSpan.FromMilliseconds(1),
            MaximumRetryDelay = TimeSpan.FromMilliseconds(5),
            SegmentsPerFile = segmentsPerFile,
            MinimumSegmentSizeBytes = minimumSegmentSizeBytes,
            MaximumConcurrentSegments = maximumConcurrentSegments,
        };

    private static HttpResponseMessage Response(HttpStatusCode code, byte[] data) => new(code) { Content = new ByteArrayContent(data) };

    private static HttpResponseMessage StreamResponse(Stream stream) => new(HttpStatusCode.OK) { Content = new StreamContent(stream) };

    private static string Sha1(byte[] data) => Convert.ToHexString(SHA1.HashData(data));

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMilliseconds = 10000)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.ElapsedMilliseconds > timeoutMilliseconds) throw new TimeoutException("The expected state was not reached in time.");
            await Task.Delay(25);
        }
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(send(request));
    }

    private sealed class AsyncDelegateHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            throw new UnreachableException();
        }
    }

    private sealed class RangeHandler(byte[] data, int chunkSize, TimeSpan chunkDelay) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            long start = 0;
            RangeHeaderValue? range = request.Headers.Range;
            if (range is { Ranges.Count: > 0 }) start = range.Ranges.First().From ?? 0;
            int offset = (int)start;
            byte[] slice = data[offset..];
            var response = new HttpResponseMessage(start > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                Content = new StreamContent(new SlowStream(slice, chunkSize, chunkDelay)),
            };
            response.Content.Headers.ContentLength = slice.Length;
            if (start > 0) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, data.Length - 1, data.Length);
            return Task.FromResult(response);
        }
    }

    private sealed class SegmentedHandler : HttpMessageHandler
    {
        private readonly byte[] _data;
        private readonly bool _honorRanges;
        private readonly bool _corruptContentRange;
        private readonly int _chunkSize;
        private readonly TimeSpan _chunkDelay;
        private readonly TimeSpan _headerDelay;
        private readonly List<(long From, long To)> _ranges = [];
        private int _requestCount;
        private int _inFlightSegments;
        private int _maximumConcurrentSegments;

        public SegmentedHandler(byte[] data, bool honorRanges = true, bool corruptContentRange = false, int chunkSize = 0, TimeSpan chunkDelay = default, TimeSpan headerDelay = default)
        {
            _data = data;
            _honorRanges = honorRanges;
            _corruptContentRange = corruptContentRange;
            _chunkSize = chunkSize;
            _chunkDelay = chunkDelay;
            _headerDelay = headerDelay;
        }

        public int RequestCount => Volatile.Read(ref _requestCount);

        public int MaximumConcurrentSegmentsSeen => Volatile.Read(ref _maximumConcurrentSegments);

        public IReadOnlyList<(long From, long To)> Ranges
        {
            get
            {
                lock (_ranges) return _ranges.ToArray();
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            long from = 0;
            long to = _data.Length - 1;
            bool hasRange = false;
            if (request.Headers.Range is { Ranges.Count: > 0 } range)
            {
                RangeItemHeaderValue item = range.Ranges.First();
                from = item.From ?? 0;
                to = item.To ?? _data.Length - 1;
                hasRange = true;
                lock (_ranges) _ranges.Add((from, to));
            }

            if (!hasRange || !_honorRanges)
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_data) };
            }

            bool isSegment = to > from;
            if (isSegment && _headerDelay > TimeSpan.Zero)
            {
                int inFlight = Interlocked.Increment(ref _inFlightSegments);
                UpdateMaximumConcurrentSegments(inFlight);
                try
                {
                    await Task.Delay(_headerDelay, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Decrement(ref _inFlightSegments);
                }
            }

            if (from < 0 || to < from || to >= _data.Length)
            {
                return new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable);
            }

            int offset = (int)from;
            int length = (int)(to - from + 1);
            byte[] slice = _data[offset..(offset + length)];
            HttpResponseMessage response = new(HttpStatusCode.PartialContent)
            {
                Content = isSegment && _chunkSize > 0
                    ? new StreamContent(new SlowStream(slice, _chunkSize, _chunkDelay))
                    : new ByteArrayContent(slice),
            };
            response.Content.Headers.ContentLength = slice.Length;
            long reportedFrom = _corruptContentRange && isSegment ? from + 1 : from;
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(reportedFrom, to, _data.Length);
            return response;
        }

        private void UpdateMaximumConcurrentSegments(int candidate)
        {
            int current = Volatile.Read(ref _maximumConcurrentSegments);
            while (candidate > current)
            {
                int observed = Interlocked.CompareExchange(ref _maximumConcurrentSegments, candidate, current);
                if (observed == current) return;
                current = observed;
            }
        }
    }

    private sealed class SlowStream(byte[] data, int chunkSize, TimeSpan delay) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            int count = Math.Min(Math.Min(chunkSize, buffer.Length), data.Length - _position);
            if (count <= 0) return 0;
            data.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
