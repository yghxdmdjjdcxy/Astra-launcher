using AstraLauncher.Plugin.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace AstraLauncher.SamplePlugin;

/// <summary>
/// Minimal third-party plugin template. Copy this project's output folder into
/// %LOCALAPPDATA%\AstraLauncher\plugins\&lt;id&gt;\ (together with plugin.json) and restart the launcher.
/// The host loads the DLL into its own collectible AssemblyLoadContext and shares the contract assembly.
/// </summary>
public sealed class SamplePlugin : ILauncherPlugin
{
    public string Id => "astra.sample";

    public void Configure(IPluginContext context)
    {
        // Any ILaunchHook registration is picked up by GameLaunchService automatically.
        context.Services.AddSingleton<ILaunchHook, SampleLaunchHook>();
    }
}

/// <summary>Demonstrates that a hook can rewrite launch parameters with a `with` expression.</summary>
public sealed class SampleLaunchHook : ILaunchHook
{
    public Task<PluginLaunchRequest> BeforeBuildAsync(PluginLaunchRequest request, CancellationToken cancellationToken)
    {
        // No-op on purpose: the sample plugin must not change how the user's game starts.
        // Replace with: return Task.FromResult(request with { ServerAddress = "127.0.0.1", ServerPort = 25566 });
        return Task.FromResult(request);
    }
}
