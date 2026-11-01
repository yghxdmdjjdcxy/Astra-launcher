using System.IO;
using AstraLauncher.Core.Services;

namespace AstraLauncher.App.Services;

/// <summary>A version/instance directory discovered outside Astra Launcher (PCL, HMCL, official launcher, ...).</summary>
public sealed record ExternalInstanceCandidate(
    string Name,
    string DirectoryPath,
    string VersionId,
    int ModCount,
    int SaveCount,
    string Source);

/// <summary>Result of scanning for foreign launcher data.</summary>
public sealed record ExternalScanResult(
    IReadOnlyList<ExternalInstanceCandidate> Candidates,
    string? GlobalSavesDirectory,
    int GlobalSaveCount);

/// <summary>Finds versions and saves created by other Minecraft launchers.</summary>
public interface IExternalInstanceScanner
{
    ExternalScanResult Scan();
}

/// <summary>
/// Scans the current Minecraft directory (and %APPDATA%\.minecraft) for version-isolation directories used by PCL / HMCL
/// and for the shared saves folder. Version directories that already carry mods/config/saves become importable instances.
/// </summary>
public sealed class ExternalInstanceScanner : IExternalInstanceScanner
{
    private readonly ILauncherPaths _paths;

    public ExternalInstanceScanner(ILauncherPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
    }

    /// <inheritdoc />
    public ExternalScanResult Scan()
    {
        var roots = new List<string> { _paths.MinecraftDirectory };
        string appDataMinecraft = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");
        if (!string.Equals(appDataMinecraft, _paths.MinecraftDirectory, StringComparison.OrdinalIgnoreCase))
        {
            roots.Add(appDataMinecraft);
        }

        var candidates = new List<ExternalInstanceCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? globalSaves = null;
        int globalSaveCount = 0;
        foreach (string root in roots)
        {
            string versionsDirectory = Path.Combine(root, "versions");
            if (Directory.Exists(versionsDirectory))
            {
                foreach (string versionDirectory in Directory.EnumerateDirectories(versionsDirectory))
                {
                    string fullPath = Path.GetFullPath(versionDirectory);
                    if (!seen.Add(fullPath))
                    {
                        continue;
                    }

                    string versionId = Path.GetFileName(versionDirectory);
                    if (string.IsNullOrWhiteSpace(versionId) ||
                        (!File.Exists(Path.Combine(versionDirectory, versionId + ".json")) &&
                         !HasGameData(versionDirectory)))
                    {
                        continue;
                    }

                    int mods = CountFiles(Path.Combine(versionDirectory, "mods"), "*.jar");
                    int saves = CountDirectories(Path.Combine(versionDirectory, "saves"));
                    candidates.Add(new ExternalInstanceCandidate(
                        versionId,
                        fullPath,
                        versionId,
                        mods,
                        saves,
                        root));
                }
            }

            string savesDirectory = Path.Combine(root, "saves");
            if (globalSaves is null && Directory.Exists(savesDirectory))
            {
                int count = CountDirectories(savesDirectory);
                if (count > 0)
                {
                    globalSaves = savesDirectory;
                    globalSaveCount = count;
                }
            }
        }

        return new ExternalScanResult(
            candidates.OrderBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
            globalSaves,
            globalSaveCount);
    }

    private static bool HasGameData(string directory) =>
        Directory.Exists(Path.Combine(directory, "mods")) ||
        Directory.Exists(Path.Combine(directory, "saves")) ||
        Directory.Exists(Path.Combine(directory, "config"));

    private static int CountFiles(string directory, string pattern)
    {
        try
        {
            return Directory.Exists(directory) ? Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly).Count() : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static int CountDirectories(string directory)
    {
        try
        {
            return Directory.Exists(directory) ? Directory.EnumerateDirectories(directory).Count() : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }
}
