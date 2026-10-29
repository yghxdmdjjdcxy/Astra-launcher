using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AstraLauncher.Core.Models;
using Microsoft.Extensions.Logging;

namespace AstraLauncher.Game.Mods;

/// <summary>
/// Default <see cref="ILocalModService"/>. Mods are plain jars in <c>&lt;instance&gt;\mods</c>; a disabled mod keeps its
/// jar on disk but receives a <c>.disabled</c> suffix so the game ignores it. Metadata is read from the Fabric, Quilt
/// or Forge/NeoForge manifest inside each jar.
/// </summary>
public sealed partial class LocalModService : ILocalModService
{
    private const long MaximumMetadataBytes = 2 * 1024 * 1024;

    private readonly ILogger<LocalModService> _logger;

    public LocalModService(ILogger<LocalModService> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public string GetModsDirectory(GameInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (string.IsNullOrWhiteSpace(instance.DirectoryPath))
        {
            throw new ArgumentException("实例没有游戏目录。", nameof(instance));
        }

        string instanceDirectory = Path.GetFullPath(instance.DirectoryPath);
        string modsDirectory = Path.GetFullPath(Path.Combine(instanceDirectory, "mods"));
        if (!modsDirectory.StartsWith(instanceDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(modsDirectory, instanceDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("实例的 mods 目录超出了实例目录，已拒绝访问。");
        }

        return modsDirectory;
    }

    public Task<IReadOnlyList<LocalMod>> ListAsync(GameInstance instance, CancellationToken cancellationToken = default)
    {
        string modsDirectory = GetModsDirectory(instance);
        if (!Directory.Exists(modsDirectory))
        {
            return Task.FromResult<IReadOnlyList<LocalMod>>([]);
        }

        var mods = new List<LocalMod>();
        foreach (string path in Directory.EnumerateFiles(modsDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsModJar(path))
            {
                continue;
            }

            mods.Add(ReadMod(path));
        }

        mods.Sort((left, right) => string.Compare(left.DisplayName, right.DisplayName, StringComparison.CurrentCultureIgnoreCase));
        return Task.FromResult<IReadOnlyList<LocalMod>>(mods);
    }

    public async Task<ModImportResult> ImportAsync(
        GameInstance instance,
        IReadOnlyList<string> sourceFilePaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(sourceFilePaths);
        string modsDirectory = GetModsDirectory(instance);
        Directory.CreateDirectory(modsDirectory);
        var imported = new List<LocalMod>();
        var errors = new List<string>();

        foreach (string sourcePath in sourceFilePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                errors.Add($"文件不存在：{sourcePath}");
                continue;
            }

            if (!string.Equals(Path.GetExtension(sourcePath), ".jar", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"不是 .jar 文件：{Path.GetFileName(sourcePath)}");
                continue;
            }

            try
            {
                string fileName = Path.GetFileName(sourcePath);
                string destination = ResolveUniqueDestination(modsDirectory, fileName, sourcePath);
                if (destination.Length == 0)
                {
                    errors.Add($"已存在同名模组：{fileName}");
                    continue;
                }

                File.Copy(sourcePath, destination, overwrite: false);
                imported.Add(ReadMod(destination));
                _logger.LogInformation("Imported mod {FileName} into {ModsDirectory}.", fileName, modsDirectory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                _logger.LogWarning(exception, "导入模组失败：{Path}", sourcePath);
                errors.Add($"导入失败：{Path.GetFileName(sourcePath)}（{exception.Message}）");
            }
        }

        return new ModImportResult(imported, errors);
    }

    public Task<LocalMod> SetEnabledAsync(
        GameInstance instance,
        LocalMod mod,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(mod);
        cancellationToken.ThrowIfCancellationRequested();
        string modsDirectory = GetModsDirectory(instance);
        string currentPath = EnsureInside(modsDirectory, mod.FilePath);
        if (mod.IsEnabled == enabled)
        {
            return Task.FromResult(mod);
        }

        string targetPath = enabled
            ? StripDisabledSuffix(currentPath)
            : currentPath + ".disabled";
        if (!string.Equals(currentPath, targetPath, StringComparison.OrdinalIgnoreCase) && File.Exists(targetPath))
        {
            throw new InvalidOperationException($"目标文件已存在，无法切换模组状态：{Path.GetFileName(targetPath)}");
        }

        File.Move(currentPath, targetPath);
        return Task.FromResult(ReadMod(targetPath));
    }

    public Task DeleteAsync(GameInstance instance, LocalMod mod, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(mod);
        cancellationToken.ThrowIfCancellationRequested();
        string modsDirectory = GetModsDirectory(instance);
        string path = EnsureInside(modsDirectory, mod.FilePath);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private static bool IsModJar(string path) =>
        path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase);

    private static string StripDisabledSuffix(string path) =>
        path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? path[..^".disabled".Length] : path;

    private static string EnsureInside(string modsDirectory, string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(modsDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("模组文件不在实例的 mods 目录中。");
        }

        return fullPath;
    }

    private static string ResolveUniqueDestination(string modsDirectory, string fileName, string sourcePath)
    {
        string destination = Path.Combine(modsDirectory, fileName);
        if (!File.Exists(destination))
        {
            return destination;
        }

        if (string.Equals(ComputeSha256(sourcePath), ComputeSha256(destination), StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        string stem = Path.GetFileNameWithoutExtension(fileName);
        for (int suffix = 2; suffix < 1000; suffix++)
        {
            destination = Path.Combine(modsDirectory, $"{stem}-{suffix}.jar");
            if (!File.Exists(destination))
            {
                return destination;
            }
        }

        throw new IOException("无法为模组生成不重复的文件名。");
    }

    private static LocalMod ReadMod(string path)
    {
        string fileName = Path.GetFileName(path);
        bool enabled = !fileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
        long size = new FileInfo(path).Length;
        try
        {
            using ZipArchive archive = ZipFile.OpenRead(path);
            if (TryReadFabric(archive) is { } fabric)
            {
                return Build(path, fileName, enabled, size, ModPlatform.Fabric, fabric.Id, fabric.Name, fabric.Version, fabric.Dependencies);
            }

            if (TryReadQuilt(archive) is { } quilt)
            {
                return Build(path, fileName, enabled, size, ModPlatform.Quilt, quilt.Id, quilt.Name, quilt.Version, quilt.Dependencies);
            }

            if (TryReadForgeToml(archive, "META-INF/neoforge.mods.toml") is { } neoForge)
            {
                return Build(path, fileName, enabled, size, ModPlatform.NeoForge, neoForge.Id, neoForge.Name, neoForge.Version, neoForge.Dependencies);
            }

            if (TryReadForgeToml(archive, "META-INF/mods.toml") is { } forge)
            {
                return Build(path, fileName, enabled, size, ModPlatform.Forge, forge.Id, forge.Name, forge.Version, forge.Dependencies);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            // A damaged jar is still listed (and can be disabled/deleted) instead of making the whole page fail.
        }

        string fallbackId = Path.GetFileNameWithoutExtension(StripDisabledSuffix(fileName));
        return Build(path, fileName, enabled, size, ModPlatform.Unknown, fallbackId, fallbackId, null, []);
    }

    private static LocalMod Build(
        string path,
        string fileName,
        bool enabled,
        long size,
        ModPlatform platform,
        string id,
        string? displayName,
        string? version,
        IReadOnlyList<ModDependency> dependencies) =>
        new(path, fileName, id, string.IsNullOrWhiteSpace(displayName) ? id : displayName, version, platform, enabled, size, dependencies);

    private static JarMetadata? TryReadFabric(ZipArchive archive)
    {
        string? json = ReadEntry(archive, "fabric.mod.json");
        if (json is null)
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? id = GetString(root, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var dependencies = new List<ModDependency>();
        if (root.TryGetProperty("depends", out JsonElement depends) && depends.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in depends.EnumerateObject())
            {
                if (IsBuiltInDependency(property.Name))
                {
                    continue;
                }

                string? range = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
                dependencies.Add(new ModDependency(property.Name, range));
            }
        }

        return new JarMetadata(id, GetString(root, "name"), GetString(root, "version"), dependencies);
    }

    private static JarMetadata? TryReadQuilt(ZipArchive archive)
    {
        string? json = ReadEntry(archive, "quilt.mod.json");
        if (json is null)
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("quilt_loader", out JsonElement loader) || loader.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? id = GetString(loader, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        string? name = loader.TryGetProperty("metadata", out JsonElement metadata) && metadata.ValueKind == JsonValueKind.Object
            ? GetString(metadata, "name")
            : null;
        var dependencies = new List<ModDependency>();
        if (loader.TryGetProperty("depends", out JsonElement depends) && depends.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement dependency in depends.EnumerateArray())
            {
                if (dependency.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string? depId = GetString(dependency, "id");
                if (string.IsNullOrWhiteSpace(depId) || IsBuiltInDependency(depId))
                {
                    continue;
                }

                string? range = GetString(dependency, "versions");
                dependencies.Add(new ModDependency(depId, range));
            }
        }

        return new JarMetadata(id, name, GetString(loader, "version"), dependencies);
    }

    private static JarMetadata? TryReadForgeToml(ZipArchive archive, string entryName)
    {
        string? toml = ReadEntry(archive, entryName);
        if (toml is null)
        {
            return null;
        }

        string? id = null;
        string? name = null;
        string? version = null;
        var dependencies = new List<ModDependency>();
        string? pendingDependencyId = null;
        string? pendingRange = null;
        bool pendingMandatory = true;
        bool inDependenciesSection = false;

        void FlushDependency()
        {
            if (!string.IsNullOrWhiteSpace(pendingDependencyId) && !IsBuiltInDependency(pendingDependencyId))
            {
                dependencies.Add(new ModDependency(pendingDependencyId, pendingRange, pendingMandatory));
            }

            pendingDependencyId = null;
            pendingRange = null;
            pendingMandatory = true;
        }

        foreach (string rawLine in toml.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith("[[dependencies.", StringComparison.OrdinalIgnoreCase))
            {
                FlushDependency();
                inDependenciesSection = true;
                continue;
            }

            if (line.StartsWith('['))
            {
                FlushDependency();
                inDependenciesSection = false;
                continue;
            }

            Match match = TomlAssignmentRegex().Match(line);
            if (!match.Success)
            {
                continue;
            }

            string key = match.Groups[1].Value;
            string value = match.Groups[2].Value.Trim().Trim('"', '\'').Trim();
            if (value.Length == 0 || value == "false" || value == "true")
            {
                if (string.Equals(key, "mandatory", StringComparison.OrdinalIgnoreCase))
                {
                    pendingMandatory = !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
                }

                continue;
            }

            if (!inDependenciesSection)
            {
                id ??= string.Equals(key, "modId", StringComparison.OrdinalIgnoreCase) ? value : id;
                name ??= string.Equals(key, "displayName", StringComparison.OrdinalIgnoreCase) ? value : name;
                version ??= string.Equals(key, "version", StringComparison.OrdinalIgnoreCase) ? value : version;
                continue;
            }

            switch (key.ToLowerInvariant())
            {
                case "modid":
                    pendingDependencyId = value;
                    break;
                case "versionrange":
                    pendingRange = value;
                    break;
                case "mandatory":
                    pendingMandatory = !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
                    break;
            }
        }

        FlushDependency();
        return string.IsNullOrWhiteSpace(id) ? null : new JarMetadata(id, name, version, dependencies);
    }

    private static bool IsBuiltInDependency(string id) =>
        id is "minecraft" or "java" or "fabricloader" or "fabric" or "quilt_loader" or "quilt" or "forge" or "neoforge";

    private static string? ReadEntry(ZipArchive archive, string entryName)
    {
        ZipArchiveEntry? entry = archive.Entries.FirstOrDefault(candidate =>
            string.Equals(candidate.FullName, entryName, StringComparison.OrdinalIgnoreCase));
        if (entry is null || entry.Length > MaximumMetadataBytes)
        {
            return null;
        }

        using Stream stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string ComputeSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    [GeneratedRegex("^([A-Za-z0-9_]+)\\s*=\\s*(.+)$")]
    private static partial Regex TomlAssignmentRegex();

    private sealed record JarMetadata(string Id, string? Name, string? Version, IReadOnlyList<ModDependency> Dependencies);
}
