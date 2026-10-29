using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using AstraLauncher.Game.Loaders;
using Microsoft.Extensions.Logging.Abstractions;

namespace AstraLauncher.Tests;

public sealed class ExternalProcessRunnerTests
{
    private static string CommandShell =>
        Environment.GetEnvironmentVariable("COMSPEC") ?? Path.Combine(Environment.SystemDirectory, "cmd.exe");

    [Fact]
    public async Task RunAsync_CapturesStandardOutputAndExitCode()
    {
        var lines = new ConcurrentQueue<string>();

        int exitCode = await CreateRunner().RunAsync(CommandShell, ["/c", "echo hello"], Path.GetTempPath(), lines.Enqueue);

        Assert.Equal(0, exitCode);
        Assert.Contains(lines, line => line.Contains("hello", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RunAsync_CapturesStandardError()
    {
        var lines = new ConcurrentQueue<string>();

        int exitCode = await CreateRunner().RunAsync(CommandShell, ["/c", "echo boom 1>&2"], Path.GetTempPath(), lines.Enqueue);

        Assert.Equal(0, exitCode);
        Assert.Contains(lines, line => line.Contains("boom", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RunAsync_ReturnsNonZeroExitCode()
    {
        int exitCode = await CreateRunner().RunAsync(CommandShell, ["/c", "exit 3"], Path.GetTempPath(), onOutput: null);

        Assert.Equal(3, exitCode);
    }

    [Fact]
    public async Task RunAsync_ThrowsInvalidOperationExceptionWhenExecutableIsMissing()
    {
        string missingPath = Path.Combine(Path.GetTempPath(), $"astra-missing-{Guid.NewGuid():N}.exe");

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateRunner().RunAsync(missingPath, [], Path.GetTempPath(), onOutput: null));

        Assert.Contains("无法启动", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_IgnoresFaultyOutputCallback()
    {
        int exitCode = await CreateRunner().RunAsync(
            CommandShell,
            ["/c", "echo hello"],
            Path.GetTempPath(),
            _ => throw new InvalidOperationException("A faulty progress handler must not break the runner."));

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task RunAsync_KillsProcessAndThrowsOperationCanceledException()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateRunner().RunAsync(
                CommandShell,
                ["/c", "ping -n 30 127.0.0.1 > nul"],
                Path.GetTempPath(),
                onOutput: null,
                cancellationToken: cancellation.Token));
    }

    private static ExternalProcessRunner CreateRunner() => new(NullLogger<ExternalProcessRunner>.Instance);
}
