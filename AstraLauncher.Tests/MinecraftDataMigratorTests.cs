using System.IO;
using AstraLauncher.Infrastructure.FileSystem;
using Microsoft.Extensions.Logging.Abstractions;

namespace AstraLauncher.Tests;

public sealed class MinecraftDataMigratorTests
{
    [Fact]
    public void MigrateLegacyMinecraftData_MovesLegacyVersionsLibrariesAssets()
    {
        string root = CreateRoot();
        try
        {
            string legacyRoot = Path.Combine(root, "AstraLauncher");
            string minecraftRoot = Path.Combine(root, ".minecraft");
            WriteFile(Path.Combine(legacyRoot, "versions", "1.21.1", "1.21.1.json"), "{\"id\":\"1.21.1\"}");
            WriteFile(Path.Combine(legacyRoot, "libraries", "example", "lib.jar"), "jar");
            WriteFile(Path.Combine(legacyRoot, "assets", "objects", "ab", "abc"), "asset");

            var paths = new LauncherPaths(legacyRoot, minecraftRoot);
            bool migrated = MinecraftDataMigrator.MigrateLegacyMinecraftData(paths, NullLogger.Instance);

            Assert.True(migrated);
            Assert.True(File.Exists(Path.Combine(minecraftRoot, "versions", "1.21.1", "1.21.1.json")));
            Assert.True(File.Exists(Path.Combine(minecraftRoot, "libraries", "example", "lib.jar")));
            Assert.True(File.Exists(Path.Combine(minecraftRoot, "assets", "objects", "ab", "abc")));
            Assert.False(Directory.Exists(Path.Combine(legacyRoot, "versions")));
            Assert.False(Directory.Exists(Path.Combine(legacyRoot, "libraries")));
            Assert.False(Directory.Exists(Path.Combine(legacyRoot, "assets")));

            // Second run is a no-op.
            Assert.False(MinecraftDataMigrator.MigrateLegacyMinecraftData(paths, NullLogger.Instance));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void MigrateLegacyMinecraftData_SkipsWhenTargetAlreadyHasVersions()
    {
        string root = CreateRoot();
        try
        {
            string legacyRoot = Path.Combine(root, "AstraLauncher");
            string minecraftRoot = Path.Combine(root, ".minecraft");
            WriteFile(Path.Combine(legacyRoot, "versions", "1.21.1", "1.21.1.json"), "{}");
            WriteFile(Path.Combine(minecraftRoot, "versions", "1.20.1", "1.20.1.json"), "{}");

            var paths = new LauncherPaths(legacyRoot, minecraftRoot);
            bool migrated = MinecraftDataMigrator.MigrateLegacyMinecraftData(paths, NullLogger.Instance);

            Assert.False(migrated);
            Assert.True(File.Exists(Path.Combine(legacyRoot, "versions", "1.21.1", "1.21.1.json")));
            Assert.False(Directory.Exists(Path.Combine(minecraftRoot, "versions", "1.21.1")));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void MigrateLegacyMinecraftData_DoesNothingWhenRootsAreEqual()
    {
        string root = CreateRoot();
        try
        {
            WriteFile(Path.Combine(root, "versions", "1.21.1", "1.21.1.json"), "{}");
            var paths = new LauncherPaths(root);
            Assert.False(MinecraftDataMigrator.MigrateLegacyMinecraftData(paths, NullLogger.Instance));
            Assert.True(File.Exists(Path.Combine(root, "versions", "1.21.1", "1.21.1.json")));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void MigrateLegacyMinecraftData_DoesNothingWithoutLegacyVersions()
    {
        string root = CreateRoot();
        try
        {
            string legacyRoot = Path.Combine(root, "AstraLauncher");
            Directory.CreateDirectory(legacyRoot);
            var paths = new LauncherPaths(legacyRoot, Path.Combine(root, ".minecraft"));
            Assert.False(MinecraftDataMigrator.MigrateLegacyMinecraftData(paths, NullLogger.Instance));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static string CreateRoot() =>
        Path.Combine(Path.GetTempPath(), "AstraLauncher.Tests", "Migrator", Guid.NewGuid().ToString("N"));

    private static void WriteFile(string path, string content)
    {
        string? directory = Path.GetDirectoryName(path);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, content);
    }

    private static void DeleteRoot(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
