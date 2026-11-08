using System.Runtime.Loader;
using AstraLauncher.Plugin.Abstractions;

namespace AstraLauncher.Plugins;

/// <summary>A plugin that was loaded and configured successfully.</summary>
public sealed class LoadedPlugin
{
    public required PluginManifest Manifest { get; init; }
    public required ILauncherPlugin Instance { get; init; }
    public required string Directory { get; init; }
    public required string DataDirectory { get; init; }
    internal AssemblyLoadContext? LoadContext { get; init; }

    public override string ToString() => $"{Manifest.Id} {Manifest.Version}";
}

/// <summary>A plugin that was skipped or failed. Loading never aborts because of one bad plugin.</summary>
public sealed record PluginLoadError(string Directory, string PluginId, string Reason, Exception? Exception = null);

/// <summary>Outcome of a catalog scan, recorded before the host starts so failures can be logged later.</summary>
public sealed class PluginLoadResult
{
    public PluginLoadResult(IReadOnlyList<LoadedPlugin> plugins, IReadOnlyList<PluginLoadError> errors)
    {
        Plugins = plugins;
        Errors = errors;
    }

    public IReadOnlyList<LoadedPlugin> Plugins { get; }
    public IReadOnlyList<PluginLoadError> Errors { get; }

    public static PluginLoadResult Empty { get; } = new([], []);
}
