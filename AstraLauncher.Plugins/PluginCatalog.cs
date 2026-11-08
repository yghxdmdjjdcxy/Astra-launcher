using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using AstraLauncher.Plugin.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace AstraLauncher.Plugins;

/// <summary>
/// Scans plugin roots, validates each <c>plugin.json</c>, loads the entry assembly into an isolated
/// collectible context and calls <see cref="ILauncherPlugin.Configure"/>. One broken plugin is reported
/// and skipped; it can never stop the launcher from starting.
/// </summary>
public static class PluginCatalog
{
    public const string ManifestFileName = "plugin.json";

    /// <summary>
    /// Roots are scanned in order; the first plugin with a given id wins, which lets a portable
    /// <c>plugins\</c> folder be overridden by the per-user directory (or vice versa).
    /// </summary>
    public static PluginLoadResult Load(
        IReadOnlyList<string> roots,
        IServiceCollection services,
        ILauncherEvents events)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(events);

        var plugins = new List<LoadedPlugin>();
        var errors = new List<PluginLoadError>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            string[] directories;
            try
            {
                directories = Directory.EnumerateDirectories(root)
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                errors.Add(new PluginLoadError(root, string.Empty, "无法读取插件目录", exception));
                continue;
            }

            foreach (string directory in directories)
            {
                try
                {
                    TryLoad(directory, services, events, seenIds, plugins, errors);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    errors.Add(new PluginLoadError(directory, string.Empty, "加载插件时发生未预期的异常", exception));
                }
            }
        }

        return new PluginLoadResult(plugins, errors);
    }

    private static void TryLoad(
        string directory,
        IServiceCollection services,
        ILauncherEvents events,
        HashSet<string> seenIds,
        List<LoadedPlugin> plugins,
        List<PluginLoadError> errors)
    {
        string manifestPath = Path.Combine(directory, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return; // an ordinary folder, not a plugin
        }

        PluginManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<PluginManifest>(
                File.ReadAllText(manifestPath), PluginApi.ManifestJsonOptions);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            errors.Add(new PluginLoadError(directory, string.Empty, "plugin.json 无法解析", exception));
            return;
        }

        if (manifest is null)
        {
            errors.Add(new PluginLoadError(directory, string.Empty, "plugin.json 内容为空"));
            return;
        }

        if (!IsValidId(manifest.Id))
        {
            errors.Add(new PluginLoadError(directory, manifest.Id, "插件 id 非法（只允许字母、数字、. _ -，最长 64 字符）"));
            return;
        }

        if (!manifest.Enabled)
        {
            return;
        }

        if (manifest.ApiVersion != PluginApi.Version)
        {
            errors.Add(new PluginLoadError(directory, manifest.Id,
                $"插件 API 版本 {manifest.ApiVersion} 与宿主 {PluginApi.Version} 不匹配"));
            return;
        }

        if (!seenIds.Add(manifest.Id))
        {
            return; // already loaded from a higher-priority root
        }

        string? entryPath = ResolveEntryPath(directory, manifest.EntryAssembly);
        if (entryPath is null || !File.Exists(entryPath))
        {
            errors.Add(new PluginLoadError(directory, manifest.Id, "入口程序集缺失或路径越出插件目录"));
            return;
        }

        if (!VerifyHash(entryPath, manifest.Sha256, out string? hashError))
        {
            errors.Add(new PluginLoadError(directory, manifest.Id, hashError ?? "入口程序集校验失败"));
            return;
        }

        var loadContext = new PluginLoadContext(directory, entryPath);
        bool loaded = false;
        try
        {
            Assembly assembly = loadContext.LoadFromAssemblyPath(entryPath);

            Type? pluginType;
            try
            {
                pluginType = assembly.GetExportedTypes().FirstOrDefault(type =>
                    typeof(ILauncherPlugin).IsAssignableFrom(type)
                    && !type.IsAbstract
                    && !type.IsInterface
                    && type.GetConstructor(Type.EmptyTypes) is not null);
            }
            catch (ReflectionTypeLoadException exception)
            {
                errors.Add(new PluginLoadError(directory, manifest.Id, "枚举插件类型失败", exception));
                return;
            }

            if (pluginType is null)
            {
                errors.Add(new PluginLoadError(directory, manifest.Id, "入口程序集中未找到带无参构造的 ILauncherPlugin 实现"));
                return;
            }

            var plugin = (ILauncherPlugin)Activator.CreateInstance(pluginType)!;
            if (!string.Equals(plugin.Id, manifest.Id, StringComparison.Ordinal))
            {
                errors.Add(new PluginLoadError(directory, manifest.Id, $"插件 id 与实现不一致（实现为 {plugin.Id}）"));
                return;
            }

            string dataDirectory = Path.Combine(directory, "data");
            Directory.CreateDirectory(dataDirectory);

            plugin.Configure(new PluginContext(services, events, directory, dataDirectory));
            plugins.Add(new LoadedPlugin
            {
                Manifest = manifest,
                Instance = plugin,
                Directory = directory,
                DataDirectory = dataDirectory,
                LoadContext = loadContext,
            });
            loaded = true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            errors.Add(new PluginLoadError(directory, manifest.Id, "初始化插件失败", exception));
        }
        finally
        {
            if (!loaded)
            {
                loadContext.Unload();
            }
        }
    }

    /// <summary>Resolves the entry DLL while rejecting path traversal outside the plugin directory.</summary>
    private static string? ResolveEntryPath(string directory, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string fullPath = Path.GetFullPath(Path.Combine(directory, relativePath));
        return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? fullPath : null;
    }

    private static bool VerifyHash(string entryPath, string? expected, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(expected))
        {
            return true;
        }

        string actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(entryPath)));
        if (string.Equals(actual, expected.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        error = "入口程序集 SHA-256 校验失败";
        return false;
    }

    private static bool IsValidId(string id) =>
        !string.IsNullOrWhiteSpace(id)
        && id.Length <= 64
        && id.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');
}
