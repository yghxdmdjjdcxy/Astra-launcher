using System.IO;
using AstraLauncher.Core.Services;
using Microsoft.Extensions.Logging;

namespace AstraLauncher.Infrastructure.FileSystem;

/// <summary>
/// Moves Minecraft data that earlier Astra Launcher versions kept under the launcher-private directory
/// (<c>%LOCALAPPDATA%\AstraLauncher\versions|libraries|assets</c>) into the shared Minecraft root
/// (<c>.minecraft</c>), so existing installations stay usable and other launchers can see them.
/// </summary>
public static class MinecraftDataMigrator
{
    private static readonly string[] MigratedDirectories = ["versions", "libraries", "assets"];

    /// <summary>
    /// Best-effort one-time migration of the legacy single-root layout. Does nothing when both roots point to the
    /// same directory, when there is no legacy data, or when the target already contains versions.
    /// </summary>
    /// <returns><see langword="true"/> when data was migrated.</returns>
    public static bool MigrateLegacyMinecraftData(ILauncherPaths paths, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(logger);

        string legacyRoot = paths.ApplicationDataDirectory;
        string targetRoot = paths.MinecraftDirectory;
        if (AreSameDirectory(legacyRoot, targetRoot))
        {
            return false;
        }

        string legacyVersions = Path.Combine(legacyRoot, "versions");
        if (!HasVersionDirectories(legacyVersions))
        {
            return false;
        }

        if (HasVersionDirectories(paths.VersionsDirectory))
        {
            logger.LogInformation(
                "Minecraft directory {Target} already contains versions; legacy launcher data under {Legacy} is left untouched.",
                targetRoot,
                legacyRoot);
            return false;
        }

        bool migrated = false;
        foreach (string directoryName in MigratedDirectories)
        {
            string source = Path.Combine(legacyRoot, directoryName);
            if (!Directory.Exists(source))
            {
                continue;
            }

            string destination = Path.Combine(targetRoot, directoryName);
            try
            {
                MoveOrCopyDirectory(source, destination);
                migrated = true;
                logger.LogInformation("Moved legacy {Directory} data to {Destination}.", directoryName, destination);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Could not migrate legacy {Directory} data to {Destination}; it will be re-downloaded when needed.", directoryName, destination);
            }
        }

        if (migrated)
        {
            logger.LogInformation("Legacy Minecraft data migrated from {Legacy} to {Target}.", legacyRoot, targetRoot);
        }

        return migrated;
    }

    private static bool HasVersionDirectories(string versionsDirectory)
    {
        try
        {
            return Directory.Exists(versionsDirectory)
                && Directory.EnumerateDirectories(versionsDirectory).Any();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void MoveOrCopyDirectory(string source, string destination)
    {
        string? parent = Path.GetDirectoryName(destination);
        if (parent is not null)
        {
            Directory.CreateDirectory(parent);
        }

        // The target was created empty by EnsureCreated; remove it so Directory.Move can take over the name.
        if (Directory.Exists(destination))
        {
            if (Directory.EnumerateFileSystemEntries(destination).Any())
            {
                throw new IOException($"Target directory '{destination}' is not empty.");
            }

            Directory.Delete(destination);
        }

        if (AreSameVolume(source, destination))
        {
            Directory.Move(source, destination);
            return;
        }

        CopyDirectoryRecursively(source, destination);
        Directory.Delete(source, recursive: true);
    }

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

    private static bool AreSameDirectory(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);

    private static bool AreSameVolume(string left, string right) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(left)), Path.GetPathRoot(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);
}
