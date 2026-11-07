namespace AstraLauncher.Plugin.Abstractions;

/// <summary>
/// Mutable snapshot of a launch passed to plugins immediately before the Java command line is built.
/// A hook returns a modified copy (use <c>with</c>) or the original instance unchanged.
/// </summary>
public sealed record PluginLaunchRequest(
    string MinecraftDirectory,
    string GameDirectory,
    string VersionId,
    string InstanceName,
    string PlayerName,
    string JavaExecutablePath,
    int MaximumMemoryMiB,
    IReadOnlyList<string> ExtraJvmArguments,
    IReadOnlyList<string> ExtraGameArguments,
    string? ServerAddress = null,
    int? ServerPort = null);

/// <summary>
/// Extension point around the game launch. Register implementations through
/// <see cref="IPluginContext.Services"/>; the host resolves every registered hook in order.
/// </summary>
public interface ILaunchHook
{
    /// <summary>Runs before command-line construction. Changes to the returned request are applied to the launch.</summary>
    Task<PluginLaunchRequest> BeforeBuildAsync(PluginLaunchRequest request, CancellationToken cancellationToken);

    /// <summary>Runs after the game process started.</summary>
    Task OnGameStartedAsync(int processId, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Runs after the game process exited. Cancellation is deliberately not propagated here.</summary>
    Task OnGameExitedAsync(int processId, int exitCode, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Host-side pipeline shared by the launcher and its tests. A misbehaving hook is reported through
/// <paramref name="onError"/> and skipped, so a broken plugin can never prevent Minecraft from starting.
/// </summary>
public static class LaunchHookPipeline
{
    public static async Task<PluginLaunchRequest> ApplyBeforeBuildAsync(
        IEnumerable<ILaunchHook> hooks,
        PluginLaunchRequest request,
        Action<ILaunchHook, Exception>? onError = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hooks);
        ArgumentNullException.ThrowIfNull(request);

        PluginLaunchRequest current = request;
        foreach (ILaunchHook hook in hooks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                PluginLaunchRequest? updated = await hook.BeforeBuildAsync(current, cancellationToken).ConfigureAwait(false);
                if (updated is not null)
                {
                    current = updated;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                onError?.Invoke(hook, exception);
            }
        }

        return current;
    }

    public static async Task NotifyAsync(
        IEnumerable<ILaunchHook> hooks,
        Func<ILaunchHook, Task> action,
        Action<ILaunchHook, Exception>? onError = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hooks);
        ArgumentNullException.ThrowIfNull(action);

        foreach (ILaunchHook hook in hooks)
        {
            try
            {
                await action(hook).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                onError?.Invoke(hook, exception);
            }
        }
    }
}
