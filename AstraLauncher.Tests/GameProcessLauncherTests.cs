using System.IO;
using System.Collections.Concurrent;
using System.Diagnostics;
using AstraLauncher.Game.Launch;

namespace AstraLauncher.Tests;

public sealed class GameProcessLauncherTests
{
    private static string ComSpec => Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";

    [Fact]
    public async Task CapturesStandardOutputAndExitCode()
    {
        var lines = new ConcurrentQueue<string>();
        var launcher = new GameProcessLauncher();
        await using GameProcess process = launcher.Start(new GameLaunchCommand(ComSpec, ["/c", "echo ASTRA-OUTPUT"], Path.GetTempPath()));
        process.OutputReceived += (_, line) => lines.Enqueue(line);

        GameExitInfo exitInfo = await process.Exited.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(0, exitInfo.ExitCode);
        Assert.True(exitInfo.ExitedAt >= exitInfo.StartedAt);
        Assert.Contains(process.RecentOutput, line => line.Contains("ASTRA-OUTPUT", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("ASTRA-OUTPUT", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CapturesStandardError()
    {
        var lines = new ConcurrentQueue<string>();
        var launcher = new GameProcessLauncher();
        await using GameProcess process = launcher.Start(new GameLaunchCommand(ComSpec, ["/c", "echo BOOM 1>&2"], Path.GetTempPath()));
        process.OutputReceived += (_, line) => lines.Enqueue(line);

        await process.Exited.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Contains(process.RecentOutput, line => line.Contains("BOOM", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DrainsLargeOutputWithoutDeadlocking()
    {
        string directory = Path.Combine(Path.GetTempPath(), "AstraLauncher.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string batch = Path.Combine(directory, "big-output.cmd");
        try
        {
            await File.WriteAllTextAsync(batch, "@echo off\r\n@for /L %%i in (1,1,4000) do @echo LINE%%i\r\n");
            var lines = new ConcurrentQueue<string>();
            var launcher = new GameProcessLauncher();
            await using GameProcess process = launcher.Start(new GameLaunchCommand(ComSpec, ["/c", batch], directory));
            process.OutputReceived += (_, line) => lines.Enqueue(line);

            GameExitInfo exitInfo = await process.Exited.WaitAsync(TimeSpan.FromSeconds(60));

            Assert.Equal(0, exitInfo.ExitCode);
            Assert.True(lines.Count >= 3000, $"Expected at least 3000 output lines but received {lines.Count}.");
            Assert.Contains(process.RecentOutput, line => line == "LINE4000");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task KillTerminatesRunningProcess()
    {
        var launcher = new GameProcessLauncher();
        await using GameProcess process = launcher.Start(new GameLaunchCommand(ComSpec, ["/c", "ping -n 30 127.0.0.1 > nul"], Path.GetTempPath()));
        Assert.False(process.HasExited);

        Stopwatch stopwatch = Stopwatch.StartNew();
        process.Kill();
        GameExitInfo exitInfo = await process.Exited.WaitAsync(TimeSpan.FromSeconds(15));
        stopwatch.Stop();

        Assert.NotEqual(0, exitInfo.ExitCode);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15));
    }
}
