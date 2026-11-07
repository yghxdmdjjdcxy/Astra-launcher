using System.ComponentModel;
using System.Diagnostics;

namespace AstraLauncher.Multiplayer;

/// <summary>
/// Owns the long-lived easytier-core child process. FullTun starts it elevated (Verb=runas) because
/// creating a TUN device needs administrator rights; NoTun stays user-mode and its output is captured.
/// </summary>
public sealed class EasyTierProcess : IAsyncDisposable
{
    private readonly Action<string>? _onOutput;
    private readonly object _gate = new();
    private Process? _process;

    public EasyTierProcess(Action<string>? onOutput = null)
    {
        _onOutput = onOutput;
    }

    public int? ProcessId { get; private set; }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _process is { HasExited: false };
            }
        }
    }

    public event EventHandler? Exited;

    public void Start(
        EasyTierBinaries binaries,
        string configPath,
        bool runAsAdmin,
        IReadOnlyList<string>? extraArguments = null)
    {
        ArgumentNullException.ThrowIfNull(binaries);
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);

        var startInfo = new ProcessStartInfo(binaries.CoreExecutable)
        {
            WorkingDirectory = binaries.RootDirectory,
            UseShellExecute = runAsAdmin,
            CreateNoWindow = !runAsAdmin,
        };
        if (runAsAdmin)
        {
            startInfo.Verb = "runas";
        }
        else
        {
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
        }

        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(configPath);
        if (extraArguments is not null)
        {
            foreach (string argument in extraArguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!runAsAdmin)
        {
            process.OutputDataReceived += (_, args) => { if (args.Data is not null) _onOutput?.Invoke(args.Data); };
            process.ErrorDataReceived += (_, args) => { if (args.Data is not null) _onOutput?.Invoke(args.Data); };
        }

        process.Exited += (_, _) => Exited?.Invoke(this, EventArgs.Empty);
        if (!process.Start())
        {
            process.Dispose();
            throw new EasyTierException("无法启动 easytier-core.exe。");
        }

        if (!runAsAdmin)
        {
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        lock (_gate)
        {
            _process = process;
            ProcessId = process.Id;
        }
    }

    public async Task StopAsync()
    {
        Process? process;
        lock (_gate)
        {
            process = _process;
            _process = null;
            ProcessId = null;
        }

        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException or TimeoutException)
        {
            // Best effort: the process may already be gone or protected by elevation.
        }
        finally
        {
            process.Dispose();
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
