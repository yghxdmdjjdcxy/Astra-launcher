using System.IO;
using AstraLauncher.Infrastructure.FileSystem;

namespace AstraLauncher.Tests;

public sealed class LauncherDataMigratorTests
{
    [Fact]
    public void MigrateIfNeeded_MovesDataAndRewritesInstancePaths()
    {
        string root = CreateRoot();
        try
        {
            string legacy = Path.Combine(root, "AppData", "AstraLauncher");
            string target = Path.Combine(root, ".minecraft", "AstraLauncher");
            string instanceDirectory = Path.Combine(legacy, "instances", "abc123");
            WriteFile(Path.Combine(legacy, "settings.json"), "{\"MemoryMiB\":4096}");
            WriteFile(Path.Combine(legacy, "accounts.json"), "{\"Accounts\":[]}");
            WriteFile(Path.Combine(legacy, "logs", "astra.log"), "log");
            WriteFile(Path.Combine(instanceDirectory, "options.txt"), "options");
            string legacyEscaped = legacy.Replace("\\", "\\\\", StringComparison.Ordinal);
            WriteFile(
                Path.Combine(legacy, "instances.json"),
                $"[{{\"Id\":\"abc\",\"Name\":\"Test\",\"DirectoryPath\":\"{legacyEscaped}\\\\instances\\\\abc123\",\"VersionId\":\"1.21.1\",\"IsManagedDirectory\":true}}]");
            WriteFile(Path.Combine(legacy, "instances.json.bak"), "{\"DirectoryPath\":\"" + legacyEscaped + "\\\\instances\\\\abc123\"}");

            bool migrated = LauncherDataMigrator.MigrateIfNeeded(legacy, target, out string? error);

            Assert.True(migrated, error);
            Assert.Null(error);
            Assert.False(Directory.Exists(legacy));
            Assert.True(File.Exists(Path.Combine(target, "settings.json")));
            Assert.True(File.Exists(Path.Combine(target, "instances", "abc123", "options.txt")));
            string instancesJson = File.ReadAllText(Path.Combine(target, "instances.json"));
            Assert.Contains(target.Replace("\\", "\\\\", StringComparison.Ordinal), instancesJson, StringComparison.Ordinal);
            Assert.DoesNotContain(legacyEscaped, instancesJson, StringComparison.Ordinal);
            string backupJson = File.ReadAllText(Path.Combine(target, "instances.json.bak"));
            Assert.Contains(target.Replace("\\", "\\\\", StringComparison.Ordinal), backupJson, StringComparison.Ordinal);

            // The target now contains settings.json, so a second run is a no-op.
            Assert.False(LauncherDataMigrator.MigrateIfNeeded(legacy, target, out _));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void MigrateIfNeeded_SkipsWhenTargetAlreadyHasSettings()
    {
        string root = CreateRoot();
        try
        {
            string legacy = Path.Combine(root, "legacy");
            string target = Path.Combine(root, "target");
            WriteFile(Path.Combine(legacy, "settings.json"), "{}");
            WriteFile(Path.Combine(target, "settings.json"), "{}");

            bool migrated = LauncherDataMigrator.MigrateIfNeeded(legacy, target, out _);

            Assert.False(migrated);
            Assert.True(File.Exists(Path.Combine(legacy, "settings.json")));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void MigrateIfNeeded_DoesNothingWhenSourceIsMissing()
    {
        string root = CreateRoot();
        try
        {
            Assert.False(LauncherDataMigrator.MigrateIfNeeded(Path.Combine(root, "missing"), Path.Combine(root, "target"), out _));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static string CreateRoot() =>
        Path.Combine(Path.GetTempPath(), "AstraLauncher.Tests", "LauncherDataMigrator", Guid.NewGuid().ToString("N"));

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
