using System.Reflection;
using System.Runtime.Loader;

namespace AstraLauncher.Plugins;

/// <summary>
/// Isolated, collectible load context for one plugin. Assemblies already loaded by the host are shared so
/// contract types (ILauncherPlugin, IServiceCollection, framework types) keep a single identity; everything
/// else is resolved from the plugin's own directory.
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _pluginDirectory;

    public PluginLoadContext(string pluginDirectory, string entryAssemblyPath)
        : base($"AstraPlugin:{Path.GetFileNameWithoutExtension(entryAssemblyPath)}", isCollectible: true)
    {
        _pluginDirectory = Path.GetFullPath(pluginDirectory);
        _resolver = new AssemblyDependencyResolver(entryAssemblyPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (string.IsNullOrEmpty(assemblyName.Name))
        {
            return null;
        }

        // Loading a second copy of the contract into this context would make typeof(ILauncherPlugin)
        // different from the host's, so every host-loaded assembly is deliberately shared.
        Assembly? hostAssembly = Default.Assemblies.FirstOrDefault(
            candidate => string.Equals(candidate.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase));
        if (hostAssembly is not null)
        {
            return hostAssembly;
        }

        string? resolved = _resolver.ResolveAssemblyToPath(assemblyName);
        if (resolved is null)
        {
            // Plugins built without a .deps.json still work when dependencies sit beside the entry DLL.
            string fallback = Path.Combine(_pluginDirectory, assemblyName.Name + ".dll");
            resolved = File.Exists(fallback) ? fallback : null;
        }

        if (resolved is null)
        {
            return null; // let the runtime fall back to the default probing path (framework assemblies)
        }

        string fullPath = Path.GetFullPath(resolved);
        string root = _pluginDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return null; // never load managed dependencies from outside the plugin directory
        }

        return LoadFromAssemblyPath(fullPath);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        string? resolved = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return resolved is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(resolved);
    }
}
