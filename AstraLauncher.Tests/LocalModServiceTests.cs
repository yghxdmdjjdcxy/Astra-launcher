using System.IO;
using System.IO.Compression;
using AstraLauncher.Core.Models;
using AstraLauncher.Game.Mods;
using Microsoft.Extensions.Logging.Abstractions;

namespace AstraLauncher.Tests;

public sealed class LocalModServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AstraLauncher.Tests", Guid.NewGuid().ToString("N"));
    private readonly LocalModService _service = new(NullLogger<LocalModService>.Instance);
    private readonly GameInstance _instance;

    public LocalModServiceTests()
    {
        _instance = new GameInstance(Guid.NewGuid(), "Mods Test", _root, "1.21.1", true);
    }

    [Fact]
    public async Task ListAsync_ParsesFabricAndForgeMetadataAndDisabledState()
    {
        Directory.CreateDirectory(Path.Combine(_root, "mods"));
        CreateJar(Path.Combine(_root, "mods", "example.jar"), new Dictionary<string, string>
        {
            ["fabric.mod.json"] = """
            {"id":"examplemod","name":"Example Mod","version":"1.2.3","depends":{"minecraft":"1.21.1","fabricloader":">=0.15","othermod":"*"}}
            """,
        });
        CreateJar(Path.Combine(_root, "mods", "forge.jar.disabled"), new Dictionary<string, string>
        {
            ["META-INF/mods.toml"] = """
            modLoader="javafml"
            [[mods]]
            modId="forgemod"
            displayName="Forge Mod"
            version="2.0"
            [[dependencies.forgemod]]
            modId="minecraft"
            versionRange="[1.21.1,1.22)"
            mandatory=true
            [[dependencies.forgemod]]
            modId="missinglib"
            versionRange="[1.0,)"
            mandatory=true
            """,
        });

        IReadOnlyList<LocalMod> mods = await _service.ListAsync(_instance);

        Assert.Equal(2, mods.Count);
        LocalMod fabric = Assert.Single(mods, mod => mod.ModId == "examplemod");
        Assert.Equal("Example Mod", fabric.DisplayName);
        Assert.Equal("1.2.3", fabric.Version);
        Assert.Equal(ModPlatform.Fabric, fabric.Platform);
        Assert.True(fabric.IsEnabled);
        Assert.Contains(fabric.Dependencies, dependency => dependency.ModId == "othermod");
        Assert.DoesNotContain(fabric.Dependencies, dependency => dependency.ModId == "minecraft");

        LocalMod forge = Assert.Single(mods, mod => mod.ModId == "forgemod");
        Assert.Equal(ModPlatform.Forge, forge.Platform);
        Assert.False(forge.IsEnabled);
        Assert.Equal("forge.jar.disabled", forge.FileName);
        Assert.Contains(forge.Dependencies, dependency => dependency.ModId == "missinglib" && dependency.Mandatory);
        Assert.DoesNotContain(forge.Dependencies, dependency => dependency.ModId == "minecraft");
    }

    [Fact]
    public async Task ImportAsync_CopiesJarsAndRejectsNonJarFiles()
    {
        string sourceDirectory = Path.Combine(_root, "downloads");
        Directory.CreateDirectory(sourceDirectory);
        string first = Path.Combine(sourceDirectory, "first.jar");
        CreateJar(first, new Dictionary<string, string> { ["fabric.mod.json"] = """{"id":"first","version":"1.0"}""" });
        string second = Path.Combine(sourceDirectory, "second.jar");
        CreateJar(second, new Dictionary<string, string> { ["fabric.mod.json"] = """{"id":"second","version":"1.0"}""" });
        string text = Path.Combine(sourceDirectory, "readme.txt");
        await File.WriteAllTextAsync(text, "not a mod");

        ModImportResult result = await _service.ImportAsync(_instance, [first, second, text, Path.Combine(sourceDirectory, "missing.jar")]);

        Assert.Equal(2, result.Imported.Count);
        Assert.Equal(2, result.Errors.Count);
        Assert.True(File.Exists(Path.Combine(_root, "mods", "first.jar")));

        ModImportResult duplicate = await _service.ImportAsync(_instance, [first]);
        Assert.Empty(duplicate.Imported);
        Assert.Single(duplicate.Errors);
    }

    [Fact]
    public async Task SetEnabledAsync_RenamesBetweenJarAndDisabledSuffix()
    {
        Directory.CreateDirectory(Path.Combine(_root, "mods"));
        string path = Path.Combine(_root, "mods", "toggle.jar");
        CreateJar(path, new Dictionary<string, string> { ["fabric.mod.json"] = """{"id":"toggle","version":"1.0"}""" });
        LocalMod mod = Assert.Single(await _service.ListAsync(_instance));

        LocalMod disabled = await _service.SetEnabledAsync(_instance, mod, enabled: false);

        Assert.False(disabled.IsEnabled);
        Assert.EndsWith(".disabled", disabled.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(disabled.FilePath));
        Assert.False(File.Exists(path));

        LocalMod enabled = await _service.SetEnabledAsync(_instance, disabled, enabled: true);

        Assert.True(enabled.IsEnabled);
        Assert.Equal("toggle.jar", enabled.FileName);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task DeleteAsync_RemovesTheJarAndListReturnsEmpty()
    {
        Directory.CreateDirectory(Path.Combine(_root, "mods"));
        string path = Path.Combine(_root, "mods", "remove.jar");
        CreateJar(path, new Dictionary<string, string> { ["fabric.mod.json"] = """{"id":"remove","version":"1.0"}""" });
        LocalMod mod = Assert.Single(await _service.ListAsync(_instance));

        await _service.DeleteAsync(_instance, mod);

        Assert.False(File.Exists(path));
        Assert.Empty(await _service.ListAsync(_instance));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    internal static void CreateJar(string path, IReadOnlyDictionary<string, string> entries)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach ((string name, string content) in entries)
        {
            ZipArchiveEntry entry = archive.CreateEntry(name);
            using StreamWriter writer = new(entry.Open());
            writer.Write(content);
        }
    }
}
