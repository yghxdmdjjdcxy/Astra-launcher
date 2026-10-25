using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using AstraLauncher.Downloader;
using System.IO;

namespace AstraLauncher.Tests;

public sealed class DownloadQueueTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AstraLauncher.DownloadTests", Guid.NewGuid().ToString("N"));
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() { if (Directory.Exists(_root)) Directory.Delete(_root, true); return Task.CompletedTask; }

    [Fact]
    public async Task DownloadsAndVerifiesSha1()
    {
        byte[] data = Encoding.UTF8.GetBytes("astra-content");
        await using var queue = Queue(new DelegateHandler(_ => Response(HttpStatusCode.OK, data)));
        string target = Path.Combine(_root, "success.bin");
        IDownloadHandle job = queue.Enqueue(DownloadRequest.Create(new Uri("https://unit.test/file"), target, Sha1(data)));
        await job.Completion;
        Assert.Equal(data, await File.ReadAllBytesAsync(target)); Assert.Equal(DownloadState.Completed, job.Progress.State);
    }

    [Fact]
    public async Task RetriesTransientHttpFailure()
    {
        int calls = 0; byte[] data = [1, 2, 3];
        await using var queue = Queue(new DelegateHandler(_ => ++calls == 1 ? Response(HttpStatusCode.ServiceUnavailable, []) : Response(HttpStatusCode.OK, data)));
        IDownloadHandle job = queue.Enqueue(DownloadRequest.Create(new Uri("https://unit.test/retry"), Path.Combine(_root, "retry.bin"), maximumAttempts: 2));
        await job.Completion;
        Assert.Equal(2, calls); Assert.Equal(DownloadState.Completed, job.Progress.State);
    }

    [Fact]
    public async Task ResumesExistingPartWhenServerReturnsPartialContent()
    {
        string target = Path.Combine(_root, "resume.bin"); Directory.CreateDirectory(_root); await File.WriteAllTextAsync(target + ".astra.part", "hello");
        await using var queue = Queue(new DelegateHandler(request => { Assert.Equal("bytes=5-", request.Headers.Range!.ToString()); var response = Response(HttpStatusCode.PartialContent, Encoding.UTF8.GetBytes(" world")); response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(5, 10, 11); return response; }));
        IDownloadHandle job = queue.Enqueue(DownloadRequest.Create(new Uri("https://unit.test/resume"), target)); await job.Completion;
        Assert.Equal("hello world", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task FailsOnHashMismatch()
    {
        await using var queue = Queue(new DelegateHandler(_ => Response(HttpStatusCode.OK, [1, 2, 3])));
        IDownloadHandle job = queue.Enqueue(DownloadRequest.Create(new Uri("https://unit.test/hash"), Path.Combine(_root, "hash.bin"), "0000", maximumAttempts: 1));
        await Assert.ThrowsAsync<DownloadIntegrityException>(() => job.Completion); Assert.Equal(DownloadState.Failed, job.Progress.State);
    }

    [Fact]
    public async Task PreventsConcurrentWritersForSameTarget()
    {
        var blocker = new TaskCompletionSource<HttpResponseMessage>();
        await using var queue = Queue(new AsyncDelegateHandler(_ => blocker.Task));
        string target = Path.Combine(_root, "same.bin"); queue.Enqueue(DownloadRequest.Create(new Uri("https://unit.test/one"), target));
        Assert.Throws<DownloadConflictException>(() => queue.Enqueue(DownloadRequest.Create(new Uri("https://unit.test/two"), target)));
        blocker.SetResult(Response(HttpStatusCode.OK, [1]));
    }

    private static DownloadQueue Queue(HttpMessageHandler handler) => new(new HttpClient(handler), new DownloadQueueOptions { MaximumConcurrentDownloads = 2, MaximumConcurrentDownloadsPerHost = 1, InitialRetryDelay = TimeSpan.FromMilliseconds(1) });
    private static HttpResponseMessage Response(HttpStatusCode code, byte[] data) => new(code) { Content = new ByteArrayContent(data) };
    private static string Sha1(byte[] data) => Convert.ToHexString(SHA1.HashData(data));
    private sealed class AsyncDelegateHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request); }
    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(send(request)); }
}
