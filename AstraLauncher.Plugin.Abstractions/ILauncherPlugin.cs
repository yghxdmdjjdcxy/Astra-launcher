using Microsoft.Extensions.DependencyInjection;

namespace AstraLauncher.Plugin.Abstractions;

/// <summary>
/// Entry point every plugin implements. The host discovers a public parameterless constructor on a type
/// implementing this interface inside the manifest's entry assembly.
/// </summary>
public interface ILauncherPlugin
{
    /// <summary>Must match <see cref="PluginManifest.Id"/>.</summary>
    string Id { get; }

    /// <summary>
    /// Called before the DI container is built. Register services (including <see cref="ILaunchHook"/>),
    /// subscribe to <see cref="ILauncherEvents"/> and read the manifest here. Do not resolve services yet.
    /// </summary>
    void Configure(IPluginContext context);

    /// <summary>Optional async initialization after the host starts. Default is a no-op.</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>Services and paths exposed to a plugin during <see cref="ILauncherPlugin.Configure"/>.</summary>
public interface IPluginContext
{
    /// <summary>The same service collection the host uses; registrations become available through normal DI.</summary>
    IServiceCollection Services { get; }

    /// <summary>Typed in-process event bus. Prefer contract or BCL event types so host and plugin agree on identity.</summary>
    ILauncherEvents Events { get; }

    /// <summary>Private writable directory owned by this plugin.</summary>
    string DataDirectory { get; }

    /// <summary>Directory containing this plugin's files.</summary>
    string PluginDirectory { get; }

    /// <summary>Value of <see cref="PluginApi.Version"/> compiled into the host.</summary>
    int HostApiVersion { get; }
}
