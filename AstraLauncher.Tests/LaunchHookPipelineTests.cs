using AstraLauncher.Plugin.Abstractions;

namespace AstraLauncher.Tests;

public sealed class LaunchHookPipelineTests
{
    private sealed class TransformHook(Func<PluginLaunchRequest, PluginLaunchRequest> transform) : ILaunchHook
    {
        public Task<PluginLaunchRequest> BeforeBuildAsync(PluginLaunchRequest request, CancellationToken cancellationToken)
            => Task.FromResult(transform(request));
    }

    private sealed class ThrowingHook : ILaunchHook
    {
        public Task<PluginLaunchRequest> BeforeBuildAsync(PluginLaunchRequest request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("broken plugin");
    }

    private sealed class NullReturningHook : ILaunchHook
    {
        public Task<PluginLaunchRequest> BeforeBuildAsync(PluginLaunchRequest request, CancellationToken cancellationToken)
            => Task.FromResult<PluginLaunchRequest>(null!);
    }

    private sealed class RecordingHook(int id, List<int> calls) : ILaunchHook
    {
        public Task<PluginLaunchRequest> BeforeBuildAsync(PluginLaunchRequest request, CancellationToken cancellationToken)
            => Task.FromResult(request);

        public Task OnGameStartedAsync(int processId, CancellationToken cancellationToken)
        {
            calls.Add(id);
            return Task.CompletedTask;
        }
    }

    private static PluginLaunchRequest CreateRequest() => new(
        @"C:\minecraft", @"C:\instances\demo", "1.21.1", "demo", "Steve",
        @"C:\java\bin\java.exe", 4096, [], []);

    [Fact]
    public async Task ApplyBeforeBuild_ChainsHooksInOrder()
    {
        var first = new TransformHook(request => request with { ServerAddress = "10.77.0.1", ServerPort = 25565 });
        var second = new TransformHook(request => request with
        {
            ExtraGameArguments = [.. request.ExtraGameArguments, "--from-second"],
            MaximumMemoryMiB = 8192,
        });

        PluginLaunchRequest result = await LaunchHookPipeline.ApplyBeforeBuildAsync([first, second], CreateRequest());

        Assert.Equal("10.77.0.1", result.ServerAddress);
        Assert.Equal(25565, result.ServerPort);
        Assert.Equal(8192, result.MaximumMemoryMiB);
        Assert.Equal(["--from-second"], result.ExtraGameArguments);
    }

    [Fact]
    public async Task ApplyBeforeBuild_IsolatesFailingHook_AndKeepsGoing()
    {
        var failures = new List<Exception>();
        var working = new TransformHook(request => request with { MaximumMemoryMiB = 6144 });

        PluginLaunchRequest result = await LaunchHookPipeline.ApplyBeforeBuildAsync(
            [new ThrowingHook(), working], CreateRequest(), (_, exception) => failures.Add(exception));

        Assert.Single(failures);
        Assert.IsType<InvalidOperationException>(failures[0]);
        Assert.Equal(6144, result.MaximumMemoryMiB);
    }

    [Fact]
    public async Task ApplyBeforeBuild_NullResultKeepsPreviousRequest()
    {
        PluginLaunchRequest original = CreateRequest();
        PluginLaunchRequest result = await LaunchHookPipeline.ApplyBeforeBuildAsync([new NullReturningHook()], original);
        Assert.Same(original, result);
    }

    [Fact]
    public async Task ApplyBeforeBuild_HonoursCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await LaunchHookPipeline.ApplyBeforeBuildAsync(
                [new TransformHook(request => request)], CreateRequest(), null, cancellation.Token));
    }

    [Fact]
    public async Task NotifyAsync_CallsEveryHook()
    {
        var calls = new List<int>();
        ILaunchHook[] hooks = [new RecordingHook(1, calls), new RecordingHook(2, calls)];

        await LaunchHookPipeline.NotifyAsync(hooks, hook => hook.OnGameStartedAsync(42, CancellationToken.None));

        Assert.Equal([1, 2], calls);
    }
}
