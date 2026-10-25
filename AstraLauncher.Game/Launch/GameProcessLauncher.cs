using System.Collections.Concurrent;
using System.Diagnostics;

namespace AstraLauncher.Game.Launch;

/// <summary>
/// A running Minecraft/Java process. Standard output and error are pumped continuously so the child
/// process can never block on a full pipe; the last 2000 lines are kept for diagnostics.
/// </summary>
public sealed class GameProcess : IAsyncDisposable
{
    private const int MaxRecentLines = 2000;

    private readonly Process _process;
    private readonly CancellationTokenSource _pumpCancellation = new();
    private readonly ConcurrentQueue<string> _recentOutput = new();
    private readonly Task _stdoutPump;
    private readonly Task _stderrPump;
    private bool _disposed;

    internal GameProcess(Process process, DateTimeOffset startedAt)
    {
        _process = process;
        StartedAt = startedAt;
        _stdoutPump = PumpAsync(process.StandardOutput);
        _stderrPump = PumpAsync(process.StandardError);
        Exited = WaitForExitAsync();
    }

    public Process Process => _process;

    public int Id => _process.Id;

    public DateTimeOffset StartedAt { get; }

    /// <summary>Completes after the process exited and all buffered output has been drained.</summary>
    public Task<GameExitInfo> Exited { get; }

    /// <summary>Raised for every line written by the process (stdout and stderr). Handlers run on a background thread.</summary>
    public event EventHandler<string>? OutputReceived;

    public IReadOnlyList<string> RecentOutput => _recentOutput.ToArray();

    public bool HasExited
    {
        get
        {
            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    public void Kill()
    {
        if (HasExited) return;
        try
        {
            _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited between the check and the call.
        }
        catch (NotSupportedException)
        {
            // Process tree kill is not supported on this platform; fall back to the process itself.
            try
            {
                _process.Kill();
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    private async Task PumpAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync(_pumpCancellation.Token).ConfigureAwait(false) is string line)
            {
                _recentOutput.Enqueue(line);
                while (_recentOutput.Count > MaxRecentLines) _recentOutput.TryDequeue(out _);
                EventHandler<string>? handler = OutputReceived;
                if (handler is null) continue;
                try
                {
                    handler(this, line);
                }
                catch (Exception)
                {
                    // A faulty log subscriber must never break process output draining.
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
        }
    }

    private async Task<GameExitInfo> WaitForExitAsync()
    {
        await _process.WaitForExitAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(_stdoutPump, _stderrPump).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Draining is best effort; the exit code is still valid.
        }

        int exitCode;
        try
        {
            exitCode = _process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            exitCode = -1;
        }

        return new GameExitInfo(exitCode, StartedAt, DateTimeOffset.UtcNow);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (!HasExited) Kill();
        try
        {
            await Exited.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        _pumpCancellation.Cancel();
        _pumpCancellation.Dispose();
        _process.Dispose();
    }
}

public sealed class GameProcessLauncher
{
    public GameProcess Start(GameLaunchCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var startInfo = new ProcessStartInfo(command.ExecutablePath)
        {
            UseShellExecute = false,
            WorkingDirectory = command.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in command.Arguments) startInfo.ArgumentList.Add(argument);
        var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new GameLaunchValidationException("The game process could not be started.");
        return new GameProcess(process, DateTimeOffset.UtcNow);
    }
}
