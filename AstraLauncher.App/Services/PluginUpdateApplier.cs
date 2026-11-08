using System.IO;
using System.IO.Compression;
using System.Text.Json;
using AstraLauncher.Plugin.Abstractions;
using AstraLauncher.Plugins;

namespace AstraLauncher.App.Services;

/// <summary>
/// Applies plugin updates staged by <see cref="PluginAutoUpdateWorker"/> before the loader starts.
/// Doing this before plugins are loaded avoids overwriting DLLs that Windows has already mapped.
/// </summary>
public static class PluginUpdateApplier
{
    public static void ApplyPending(string pluginRoot, Action<string> log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginRoot);
        string updates = Path.Combine(pluginRoot, ".updates");
        if (!Directory.Exists(updates))
        {
            return;
        }

        foreach (string zip in Directory.EnumerateFiles(updates, "*.zip").ToArray())
        {
            string temp = Path.Combine(Path.GetTempPath(), "AstraLauncher-PluginApply", Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(temp);
                ExtractSafely(zip, temp);
                string? manifestPath = Directory.EnumerateFiles(temp, PluginCatalog.ManifestFileName, SearchOption.AllDirectories)
                    .OrderBy(path => path.Length)
                    .FirstOrDefault();
                if (manifestPath is null)
                {
                    log("跳过无效插件更新：" + zip);
                    continue;
                }

                string source = Path.GetDirectoryName(manifestPath)!;
                PluginManifest? manifest = JsonSerializer.Deserialize<PluginManifest>(
                    File.ReadAllText(manifestPath), PluginApi.ManifestJsonOptions);
                if (manifest is null || !IsValidId(manifest.Id) || manifest.ApiVersion != PluginApi.Version)
                {
                    log("跳过 API/id 不兼容的插件更新：" + zip);
                    continue;
                }

                string? entry = manifest.EntryAssembly;
                string rootPrefix = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string? entryPath = string.IsNullOrWhiteSpace(entry) ? null : Path.GetFullPath(Path.Combine(source, entry));
                if (entryPath is null || !entryPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(entryPath))
                {
                    log("跳过入口程序集无效的插件更新：" + zip);
                    continue;
                }

                Directory.CreateDirectory(pluginRoot);
                string target = Path.Combine(pluginRoot, manifest.Id);
                string? dataBackup = null;
                if (Directory.Exists(target) && Directory.Exists(Path.Combine(target, "data")))
                {
                    dataBackup = Path.Combine(Path.GetTempPath(), "AstraLauncher-PluginData", Guid.NewGuid().ToString("N"));
                    CopyDirectory(Path.Combine(target, "data"), dataBackup);
                }

                if (Directory.Exists(target))
                {
                    Directory.Delete(target, recursive: true);
                }

                CopyDirectory(source, target);
                if (dataBackup is not null)
                {
                    CopyDirectory(dataBackup, Path.Combine(target, "data"));
                }

                TryDeleteDirectory(dataBackup);
                TryDeleteFile(zip);
                log($"已应用插件更新：{manifest.Id} {manifest.Version}");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
            {
                log($"应用插件更新失败（{zip}）：{exception.Message}");
            }
            finally
            {
                TryDeleteDirectory(temp);
            }
        }
    }

    private static void ExtractSafely(string zipPath, string destination)
    {
        string root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using ZipArchive archive = ZipFile.OpenRead(zipPath);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            string target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("ZIP entry escapes destination: " + entry.FullName);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static bool IsValidId(string id) =>
        !string.IsNullOrWhiteSpace(id)
        && id.Length <= 64
        && id.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private static void TryDeleteDirectory(string? path)
    {
        try
        {
            if (path is not null && Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort.
        }
    }
}
