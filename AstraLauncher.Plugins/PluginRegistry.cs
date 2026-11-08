namespace AstraLauncher.Plugins;

/// <summary>
/// Read-only view of the plugins active in this session, exposed through DI for a future plugin
/// management page. Plugins are fixed at startup; enabling/disabling takes effect after a restart.
/// </summary>
public sealed class PluginRegistry
{
    private readonly object _gate = new();
    private LoadedPlugin[] _plugins = [];

    public IReadOnlyList<LoadedPlugin> Plugins
    {
        get
        {
            lock (_gate)
            {
                return _plugins;
            }
        }
    }

    public void Register(PluginLoadResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (_gate)
        {
            _plugins = result.Plugins.ToArray();
        }
    }
}
