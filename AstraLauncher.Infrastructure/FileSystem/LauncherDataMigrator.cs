using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AstraLauncher.Infrastructure.FileSystem;

/// <summary>
/// Moves launcher-owned data (settings, accounts, logs, instances, caches) from the legacy
/// <c>%LOCALAPPDATA%\AstraLauncher</c> location into <c>&lt;MinecraftDirectory&gt;\AstraLauncher</c>, so a portable
/// <c>.minecraft</c> folder contains everything the launcher needs. Instance paths stored in <c>instances.json</c>
/// are rewritten to the new location.
/// </summary>
public static class LauncherDataMigrator
{
    private const string SettingsFileName = "settings.json";
    private const string InstancesFileName = "instances.json";

    /// <summary>Migrates <paramref name="sourceDataDirectory"/> into <paramref name="targetDataDirectory"/> when the
    /// target does not already contain settings. Returns <see langword="true"/> when data was moved.</summary>
    public static bool MigrateIfNeeded(string sourceDataDirectory, string targetDataDirectory, out string? error)
    {
        error = null;
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDataDirectory);

        string source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDataDirectory));
        string target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDataDirectory));
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (File.Exists(Path.Combine(target, SettingsFileName)))
        {
            return false;
        }

        if (!Directory.Exists(source))
        {
            return false;
        }

        try
        {
            string? parent = Path.GetDirectoryName(target);
            if (parent is not null)
            {
                Directory.CreateDirectory(parent);
            }

            if (Directory.Exists(target))
            {
                CopyDirectoryRecursively(source, target);
                Directory.Delete(source, recursive: true);
            }
            else if (AreSameVolume(source, target))
            {
                Directory.Move(source, target);
            }
            else
            {
                CopyDirectoryRecursively(source, target);
                Directory.Delete(source, recursive: true);
            }

            RewriteInstancePaths(target, source, target);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            error = exception.Message;
            return false;
        }
    }

    private static void RewriteInstancePaths(string dataDirectory, string oldBaseDirectory, string newBaseDirectory)
    {
        string oldPath = EscapeJsonPath(oldBaseDirectory);
        string newPath = EscapeJsonPath(newBaseDirectory);
        foreach (string fileName in new[] { InstancesFileName, InstancesFileName + ".bak" })
        {
            string path = Path.Combine(dataDirectory, fileName);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                string json = File.ReadAllText(path);
                if (!json.Contains(oldPath, StringComparison.Ordinal))
                {
                    continue;
                }

                string updated = json.Replace(oldPath, newPath, StringComparison.Ordinal);
                string temporary = path + ".migration.tmp";
                File.WriteAllText(temporary, updated);
                File.Move(temporary, path, overwrite: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A stale path only affects the affected instance entry; keep the migration best-effort.
            }
        }
    }

    private static string EscapeJsonPath(string path) => path.Replace("\\", "\\\\", StringComparison.Ordinal);

    private static bool AreSameVolume(string left, string right) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(left)), Path.GetPathRoot(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);

    private static void CopyDirectoryRecursively(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        foreach (string directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectoryRecursively(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }
}
