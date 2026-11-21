using System.IO;
using AstraLauncher.Game.Loaders;
using AstraLauncher.Game.Modpacks;

namespace AstraLauncher.Tests;

public sealed class ModpackTests
{
    [Fact]
    public void ParseManifest_ReadsFabricDependenciesAndSafeFiles()
    {
        const string json = """
        {
          "formatVersion": 1,
          "name": "Test Pack",
          "dependencies": { "minecraft": "1.20.1", "fabric-loader": "0.15.0" },
          "files": [
            {
              "path": "mods/example.jar",
              "hashes": { "sha1": "abc" },
              "env": { "client": "required" },
              "downloads": ["https://cdn.modrinth.com/data/example.jar"],
              "fileSize": 123
            },
            {
              "path": "../escape.jar",
              "hashes": { "sha1": "bad" },
              "downloads": ["https://cdn.modrinth.com/data/escape.jar"]
            },
            {
              "path": "mods/server-only.jar",
              "env": { "client": "unsupported" },
              "downloads": ["https://cdn.modrinth.com/data/server.jar"]
            }
          ]
        }
        """;

        ModpackManifest manifest = ModpackImportService.ParseManifest(json);

        Assert.Equal("Test Pack", manifest.Name);
        Assert.Equal("1.20.1", manifest.MinecraftVersion);
        Assert.Equal(LoaderKind.Fabric, manifest.Loader);
        Assert.Equal("0.15.0", manifest.LoaderVersion);
        ModpackFile file = Assert.Single(manifest.Files);
        Assert.Equal("mods/example.jar", file.RelativePath);
        Assert.Equal("abc", file.Sha1);
        Assert.Equal(123, file.Size);
    }

    [Fact]
    public void ParseManifest_UsesForgeWhenForgeDependencyIsPresent()
    {
        const string json = """
        {
          "dependencies": { "minecraft": "1.20.1", "forge": "47.2.0" },
          "files": []
        }
        """;

        ModpackManifest manifest = ModpackImportService.ParseManifest(json);

        Assert.Equal(LoaderKind.Forge, manifest.Loader);
        Assert.Equal("47.2.0", manifest.LoaderVersion);
        Assert.Equal("导入的整合包", manifest.Name);
        Assert.Empty(manifest.Files);
    }

    [Fact]
    public void ParseManifest_RejectsMissingDependencies()
    {
        Assert.Throws<InvalidDataException>(() => ModpackImportService.ParseManifest("{\"files\":[]}"));
    }

    [Fact]
    public void ParseManifest_RequiresMinecraftVersion()
    {
        Assert.Throws<InvalidDataException>(() =>
            ModpackImportService.ParseManifest("{\"dependencies\":{\"fabric-loader\":\"0.15.0\"},\"files\":[]}"));
    }
}
