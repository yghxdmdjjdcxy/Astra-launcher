using System.Diagnostics;

namespace AstraLauncher.Game.Launch;

public sealed class GameProcess
{
    internal GameProcess(Process process, DateTimeOffset startedAt)
    {
        Process = process; StartedAt = startedAt; Exited = WaitForExitAsync();
    }
    public Process Process { get; }
    public DateTimeOffset StartedAt { get; }
    public Task<GameExitInfo> Exited { get; }
    private async Task<GameExitInfo> WaitForExitAsync() { await Process.WaitForExitAsync().ConfigureAwait(false); return new GameExitInfo(Process.ExitCode, StartedAt, DateTimeOffset.UtcNow); }
}

public sealed class GameProcessLauncher
{
    public GameProcess Start(GameLaunchCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var startInfo = new ProcessStartInfo(command.ExecutablePath) { UseShellExecute = false, WorkingDirectory = command.WorkingDirectory, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = false };
        foreach (string argument in command.Arguments) startInfo.ArgumentList.Add(argument);
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start()) throw new GameLaunchValidationException("Java process could not be started.");
        return new GameProcess(process, DateTimeOffset.UtcNow);
    }
}
