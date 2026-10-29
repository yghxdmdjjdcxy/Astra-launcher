using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;

namespace AstraLauncher.Game.Loaders;

/// <summary>
/// Runs an external executable while draining standard output and standard error line by line.
/// Standard streams are pumped concurrently so a chatty child process can never deadlock on a full pipe.
/// </summary>
public sealed class ExternalProcessRunner : IExternalProcessRunner
{
    private readonly ILogger<ExternalProcessRunner> _logger;

    public ExternalProcessRunner(ILogger<ExternalProcessRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public async Task<int> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        Action<string>? onOutput,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        cancellationToken.ThrowIfCancellationRequested();

        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = new() { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"无法启动外部进程“{executablePath}”。");
            }
        }
        catch (Exception exception) when (exception is Win32Exception or FileNotFoundException or InvalidOperationException)
        {
            _logger.LogError(exception, "Failed to start external process {ExecutablePath}.", executablePath);
            throw new InvalidOperationException($"无法启动外部进程“{executablePath}”：{exception.Message}", exception);
        }

        _logger.LogInformation("Started external process {ExecutablePath} in {WorkingDirectory}.", executablePath, workingDirectory);

        var outputGate = new object();
        void ReportLine(string line)
        {
            if (onOutput is null)
            {
                return;
            }

            // Progress handlers are third-party code; a throwing handler must not break output draining.
            lock (outputGate)
            {
                try
                {
                    onOutput(line);
                }
                catch (Exception)
                {
                    // Intentionally ignored: output callbacks are informational only.
                }
            }
        }

        Task stdoutPump = PumpAsync(process.StandardOutput, ReportLine, cancellationToken);
        Task stderrPump = PumpAsync(process.StandardError, ReportLine, cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            KillProcessTree(process);

            try
            {
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // The process exited between the kill request and the wait.
            }

            await IgnorePumpErrorsAsync(stdoutPump).ConfigureAwait(false);
            await IgnorePumpErrorsAsync(stderrPump).ConfigureAwait(false);
            _logger.LogInformation("External process {ExecutablePath} was canceled.", executablePath);
            throw;
        }

        await IgnorePumpErrorsAsync(stdoutPump).ConfigureAwait(false);
        await IgnorePumpErrorsAsync(stderrPump).ConfigureAwait(false);

        int exitCode = process.ExitCode;
        _logger.LogInformation("External process {ExecutablePath} exited with code {ExitCode}.", executablePath, exitCode);
        return exitCode;
    }

    private static async Task PumpAsync(StreamReader reader, Action<string> onOutput, CancellationToken cancellationToken)
    {
        try
        {
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                onOutput(line);
            }
        }
        catch (OperationCanceledException)
        {
            // Cancellation closes the pipes and ends the pump; the caller reports the cancellation.
        }
        catch (ObjectDisposedException)
        {
            // The process was disposed while the pump was waiting for data.
        }
        catch (IOException)
        {
            // A broken pipe is not a reason to change the exit code; the process exit code remains authoritative.
        }
    }

    private static async Task IgnorePumpErrorsAsync(Task pump)
    {
        try
        {
            await pump.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Output pumping is best effort; the process exit code is still valid.
        }
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The process already exited.
        }
        catch (NotSupportedException)
        {
            // Process-tree kill is unavailable on this platform; kill the direct child instead.
            try
            {
                process.Kill();
            }
            catch (InvalidOperationException)
            {
                // The process already exited.
            }
            catch (Win32Exception)
            {
                // The process already exited or cannot be terminated.
            }
        }
        catch (Win32Exception)
        {
            // The process already exited or cannot be terminated; the cancellation still propagates.
        }
        catch (AggregateException)
        {
            // Some descendants could not be killed; the cancellation still propagates.
        }
    }
}
