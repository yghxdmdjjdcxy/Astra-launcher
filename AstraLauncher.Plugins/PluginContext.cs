using AstraLauncher.Plugin.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace AstraLauncher.Plugins;

internal sealed class PluginContext : IPluginContext
{
    public PluginContext(IServiceCollection services, ILauncherEvents events, string pluginDirectory, string dataDirectory)
    {
        Services = services;
        Events = events;
        PluginDirectory = pluginDirectory;
        DataDirectory = dataDirectory;
    }

    public IServiceCollection Services { get; }
    public ILauncherEvents Events { get; }
    public string DataDirectory { get; }
    public string PluginDirectory { get; }
    public int HostApiVersion => PluginApi.Version;
}
