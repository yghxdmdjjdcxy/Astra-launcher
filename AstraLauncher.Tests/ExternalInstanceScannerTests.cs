using System.IO;
using AstraLauncher.App.Services;
using AstraLauncher.Core.Services;

namespace AstraLauncher.Tests;

public sealed class ExternalInstanceScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AstraLauncher.Tests", "scanner-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Scan_FindsVersionIsolationAndGlobalSaves()
    {
        string versionDirectory = Path.Combine(_root, "versions", "1.20.1-Forge_47.4.22");
        Directory.CreateDirectory(Path.Combine(versionDirectory, "mods"));
        Directory.CreateDirectory(Path.Combine(versionDirectory, "saves", "world"));
        File.WriteAllText(Path.Combine(versionDirectory, "1.20.1-Forge_47.4.22.json"), "{}");
        File.WriteAllText(Path.Combine(versionDirectory, "mods", "a.jar"), "jar");
        File.WriteAllText(Path.Combine(versionDirectory, "saves", "world", "level.dat"), "data");
        string globalSaves = Path.Combine(_root, "saves", "global");
        Directory.CreateDirectory(globalSaves);
        File.WriteAllText(Path.Combine(globalSaves, "level.dat"), "data");

        ExternalScanResult result = new ExternalInstanceScanner(new FakePaths(_root)).Scan();

        ExternalInstanceCandidate candidate = Assert.Single(result.Candidates, item => item.Name == "1.20.1-Forge_47.4.22");
        Assert.Equal(versionDirectory, candidate.DirectoryPath);
        Assert.Equal(1, candidate.ModCount);
        Assert.Equal(1, candidate.SaveCount);
        Assert.Equal(1, result.GlobalSaveCount);
        Assert.Equal(Path.Combine(_root, "saves"), result.GlobalSavesDirectory);
    }

    [Fact]
    public void Scan_SkipsDirectoriesWithoutVersionJsonOrGameData()
    {
        Directory.CreateDirectory(Path.Combine(_root, "versions", "empty"));

        ExternalScanResult result = new ExternalInstanceScanner(new FakePaths(_root)).Scan();

        Assert.DoesNotContain(result.Candidates, item => item.Name == "empty");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FakePaths(string root) : ILauncherPaths
    {
        public string ApplicationDataDirectory => root;
        public string LogsDirectory => Path.Combine(root, "logs");
        public string InstancesDirectory => Path.Combine(root, "instances");
        public string MinecraftDirectory => root;
        public string VersionsDirectory => Path.Combine(root, "versions");
        public string LibrariesDirectory => Path.Combine(root, "libraries");
        public string AssetsDirectory => Path.Combine(root, "assets");
        public string RuntimeDirectory => Path.Combine(root, "runtime");
        public string CacheDirectory => Path.Combine(root, "cache");
        public string TempDirectory => Path.Combine(root, "temp");
        public void SetMinecraftDirectory(string path) { }
        public void EnsureCreated() => Directory.CreateDirectory(root);
    }
}
