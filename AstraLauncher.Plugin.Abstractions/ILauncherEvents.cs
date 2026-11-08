namespace AstraLauncher.Plugin.Abstractions;

/// <summary>
/// Minimal typed event bus shared by the host and plugins. Event types should come from this contract
/// assembly or the BCL: types declared inside a plugin's own load context are not visible to host publishers.
/// </summary>
public interface ILauncherEvents
{
    /// <summary>Subscribes a handler and returns a token that removes it when disposed.</summary>
    IDisposable Subscribe<T>(Action<T> handler);

    /// <summary>Publishes a message to all current subscribers. A failing handler never blocks the others.</summary>
    void Publish<T>(T message);
}
