using System.IO;
using System.Runtime.Loader;
using System.Text.Json;
using AstraLauncher.Plugin.Abstractions;
using AstraLauncher.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace AstraLauncher.Tests;

public sealed class PluginCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "astra-plugin-tests-" + Guid.NewGuid().ToString("N"));

    public PluginCatalogTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A collectible load context may still hold a mapped DLL on Windows; the temp folder is disposable.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private string CreatePlugin(string id, PluginManifest? manifest = null, string? rawManifest = null)
    {
        string directory = Path.Combine(_root, id);
        Directory.CreateDirectory(directory);
        string json = rawManifest ?? JsonSerializer.Serialize(manifest ?? new PluginManifest
        {
            Id = id,
            Name = id,
            EntryAssembly = "Plugin.dll",
            ApiVersion = PluginApi.Version,
        });
        File.WriteAllText(Path.Combine(directory, PluginCatalog.ManifestFileName), json);
        return directory;
    }

    private PluginLoadResult Load() => PluginCatalog.Load([_root], new ServiceCollection(), new LauncherEventBus());

    [Fact]
    public void Load_WithNoPluginDirectories_ReturnsEmpty()
    {
        PluginLoadResult result = Load();
        Assert.Empty(result.Plugins);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Load_IgnoresDirectoryWithoutManifest()
    {
        Directory.CreateDirectory(Path.Combine(_root, "not-a-plugin"));
        PluginLoadResult result = Load();
        Assert.Empty(result.Plugins);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Load_RejectsInvalidId()
    {
        CreatePlugin("invalid-id-folder", new PluginManifest
        {
            Id = "bad id/../escape", EntryAssembly = "Plugin.dll", ApiVersion = PluginApi.Version,
        });

        PluginLoadResult result = Load();

        Assert.Empty(result.Plugins);
        Assert.Single(result.Errors);
        Assert.Contains("id 非法", result.Errors[0].Reason);
    }

    [Fact]
    public void Load_RejectsApiVersionMismatch()
    {
        CreatePlugin("old-plugin", new PluginManifest
        {
            Id = "old-plugin", EntryAssembly = "Plugin.dll", ApiVersion = PluginApi.Version + 1,
        });

        PluginLoadResult result = Load();

        Assert.Empty(result.Plugins);
        Assert.Contains("API 版本", result.Errors[0].Reason);
    }

    [Fact]
    public void Load_SkipsDisabledPluginSilently()
    {
        CreatePlugin("disabled", new PluginManifest
        {
            Id = "disabled", EntryAssembly = "Plugin.dll", ApiVersion = PluginApi.Version, Enabled = false,
        });

        PluginLoadResult result = Load();

        Assert.Empty(result.Plugins);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Load_RejectsMissingEntryAssembly()
    {
        CreatePlugin("missing-dll");
        PluginLoadResult result = Load();
        Assert.Single(result.Errors);
        Assert.Contains("入口程序集", result.Errors[0].Reason);
    }

    [Fact]
    public void Load_RejectsEntryAssemblyOutsidePluginDirectory()
    {
        CreatePlugin("traversal", new PluginManifest
        {
            Id = "traversal", EntryAssembly = @"..\..\outside.dll", ApiVersion = PluginApi.Version,
        });
        File.WriteAllBytes(Path.Combine(_root, "outside.dll"), [1, 2, 3]);

        PluginLoadResult result = Load();

        Assert.Empty(result.Plugins);
        Assert.Single(result.Errors);
        Assert.Equal("traversal", result.Errors[0].PluginId);
    }

    [Fact]
    public void Load_RejectsSha256Mismatch()
    {
        string directory = CreatePlugin("signed", new PluginManifest
        {
            Id = "signed", EntryAssembly = "Plugin.dll", ApiVersion = PluginApi.Version, Sha256 = "00",
        });
        File.WriteAllBytes(Path.Combine(directory, "Plugin.dll"), [1, 2, 3]);

        PluginLoadResult result = Load();

        Assert.Empty(result.Plugins);
        Assert.Contains("SHA-256", result.Errors[0].Reason);
    }

    [Fact]
    public void Load_RejectsMalformedManifest()
    {
        CreatePlugin("broken", rawManifest: "{ not json");
        PluginLoadResult result = Load();
        Assert.Single(result.Errors);
        Assert.Contains("无法解析", result.Errors[0].Reason);
    }

    [Fact]
    public void Load_DuplicateIdIsOnlyEvaluatedOnce()
    {
        CreatePlugin("first", new PluginManifest { Id = "shared", EntryAssembly = "Plugin.dll", ApiVersion = PluginApi.Version });
        CreatePlugin("second", new PluginManifest { Id = "shared", EntryAssembly = "Plugin.dll", ApiVersion = PluginApi.Version });

        PluginLoadResult result = Load();

        Assert.Empty(result.Plugins);
        Assert.Single(result.Errors); // the second directory is skipped without another error
    }

    [Fact]
    public void Load_RealPlugin_RegistersHookInAnIsolatedCollectibleContext()
    {
        string directory = CreatePlugin("astra.sample", new PluginManifest
        {
            Id = "astra.sample",
            Name = "sample",
            EntryAssembly = "AstraLauncher.SamplePlugin.dll",
            ApiVersion = PluginApi.Version,
        });
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "AstraLauncher.SamplePlugin.dll"),
            Path.Combine(directory, "AstraLauncher.SamplePlugin.dll"));

        var services = new ServiceCollection();
        PluginLoadResult result = PluginCatalog.Load([_root], services, new LauncherEventBus());

        Assert.Empty(result.Errors);
        LoadedPlugin loaded = Assert.Single(result.Plugins);
        Assert.Equal("astra.sample", loaded.Instance.Id);
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(ILaunchHook));
        Assert.True(Directory.Exists(loaded.DataDirectory));

        AssemblyLoadContext? pluginContext = AssemblyLoadContext.GetLoadContext(loaded.Instance.GetType().Assembly);
        Assert.NotNull(pluginContext);
        Assert.True(pluginContext.IsCollectible);
        Assert.NotSame(AssemblyLoadContext.Default, pluginContext);
        Assert.Same(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(typeof(ILauncherPlugin).Assembly));
    }
}
