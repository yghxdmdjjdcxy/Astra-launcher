using System.Text.Json;
using AstraLauncher.Core.Models;
using AstraLauncher.Core.Services;

namespace AstraLauncher.Game.Instances;

public interface IGameInstanceRepository
{
    Task<IReadOnlyList<GameInstance>> ListAsync(CancellationToken cancellationToken = default);
    Task<GameInstance> CreateAsync(string name, string versionId, CancellationToken cancellationToken = default);
    Task<GameInstance> ImportAsync(string name, string versionId, string directoryPath, CancellationToken cancellationToken = default);
    Task<GameInstance> RenameAsync(Guid id, string name, CancellationToken cancellationToken = default);
    Task<GameInstance> DuplicateAsync(Guid id, string name, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, bool deleteManagedDirectory, CancellationToken cancellationToken = default);
}

public sealed class GameInstanceRepository(ILauncherPaths paths) : IGameInstanceRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _instancesDirectory = Path.GetFullPath(paths.InstancesDirectory);
    private readonly string _manifestPath = Path.Combine(Path.GetFullPath(paths.ApplicationDataDirectory), "instances.json");

    public async Task<IReadOnlyList<GameInstance>> ListAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await ReadAsync(cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task<GameInstance> CreateAsync(string name, string versionId, CancellationToken cancellationToken = default)
    {
        ValidateName(name); ValidateVersionId(versionId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Guid id = Guid.NewGuid();
            string directory = Path.Combine(_instancesDirectory, id.ToString("N"));
            Directory.CreateDirectory(directory);
            var instance = new GameInstance(id, name.Trim(), directory, versionId.Trim(), true);
            List<GameInstance> instances = await ReadAsync(cancellationToken).ConfigureAwait(false);
            instances.Add(instance);
            await WriteAsync(instances, cancellationToken).ConfigureAwait(false);
            return instance;
        }
        finally { _gate.Release(); }
    }

    public async Task<GameInstance> ImportAsync(string name, string versionId, string directoryPath, CancellationToken cancellationToken = default)
    {
        ValidateName(name); ValidateVersionId(versionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        string fullDirectory = Path.GetFullPath(directoryPath);
        if (!Directory.Exists(fullDirectory)) throw new DirectoryNotFoundException($"The instance directory does not exist: {fullDirectory}");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var instance = new GameInstance(Guid.NewGuid(), name.Trim(), fullDirectory, versionId.Trim(), false);
            List<GameInstance> instances = await ReadAsync(cancellationToken).ConfigureAwait(false);
            instances.Add(instance);
            await WriteAsync(instances, cancellationToken).ConfigureAwait(false);
            return instance;
        }
        finally { _gate.Release(); }
    }

    public async Task<GameInstance> RenameAsync(Guid id, string name, CancellationToken cancellationToken = default)
    {
        ValidateName(name);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<GameInstance> instances = await ReadAsync(cancellationToken).ConfigureAwait(false);
            int index = FindIndex(instances, id);
            GameInstance updated = instances[index] with { Name = name.Trim() };
            instances[index] = updated;
            await WriteAsync(instances, cancellationToken).ConfigureAwait(false);
            return updated;
        }
        finally { _gate.Release(); }
    }

    public async Task<GameInstance> DuplicateAsync(Guid id, string name, CancellationToken cancellationToken = default)
    {
        ValidateName(name);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<GameInstance> instances = await ReadAsync(cancellationToken).ConfigureAwait(false);
            GameInstance source = instances[FindIndex(instances, id)];
            if (!Directory.Exists(source.DirectoryPath)) throw new DirectoryNotFoundException($"The source instance directory does not exist: {source.DirectoryPath}");
            Guid newId = Guid.NewGuid();
            string targetDirectory = Path.Combine(_instancesDirectory, newId.ToString("N"));
            await CopyDirectoryAsync(source.DirectoryPath, targetDirectory, cancellationToken).ConfigureAwait(false);
            var duplicate = new GameInstance(newId, name.Trim(), targetDirectory, source.VersionId, true);
            instances.Add(duplicate);
            await WriteAsync(instances, cancellationToken).ConfigureAwait(false);
            return duplicate;
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteAsync(Guid id, bool deleteManagedDirectory, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<GameInstance> instances = await ReadAsync(cancellationToken).ConfigureAwait(false);
            int index = FindIndex(instances, id);
            GameInstance instance = instances[index];
            if (deleteManagedDirectory)
            {
                if (!instance.IsManagedDirectory) throw new InvalidOperationException("Imported instance directories are never deleted by Astra Launcher.");
                EnsureManagedDirectory(instance.DirectoryPath);
                if (Directory.Exists(instance.DirectoryPath)) Directory.Delete(instance.DirectoryPath, recursive: true);
            }
            instances.RemoveAt(index);
            await WriteAsync(instances, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task<List<GameInstance>> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_manifestPath)) return [];
        await using FileStream stream = new(_manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        List<GameInstance>? instances = await JsonSerializer.DeserializeAsync<List<GameInstance>>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        return instances ?? [];
    }

    private async Task WriteAsync(IReadOnlyList<GameInstance> instances, CancellationToken cancellationToken)
    {
        string? manifestDirectory = Path.GetDirectoryName(_manifestPath);
        if (manifestDirectory is null) throw new InvalidOperationException("The instance manifest does not have a parent directory.");
        Directory.CreateDirectory(manifestDirectory);
        string temporaryPath = $"{_manifestPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (FileStream stream = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, instances, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporaryPath, _manifestPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static async Task CopyDirectoryAsync(string source, string destination, CancellationToken cancellationToken)
    {
        DirectoryInfo sourceInfo = new(source);
        if ((sourceInfo.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Instance directories containing a reparse-point root cannot be duplicated.");
        Directory.CreateDirectory(destination);
        try
        {
            foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Instance directories containing reparse points cannot be duplicated.");
                Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
            }
            foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string target = Path.Combine(destination, Path.GetRelativePath(source, file));
                await using FileStream input = new(file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using FileStream output = new(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough);
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
            throw;
        }
    }

    private void EnsureManagedDirectory(string directory)
    {
        string relative = Path.GetRelativePath(_instancesDirectory, Path.GetFullPath(directory));
        if (Path.IsPathRooted(relative) || relative.Equals("..", StringComparison.Ordinal) || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing to delete a directory outside of the managed instances root.");
    }
    private static int FindIndex(IReadOnlyList<GameInstance> instances, Guid id) { for (int index = 0; index < instances.Count; index++) if (instances[index].Id == id) return index; throw new KeyNotFoundException($"The game instance '{id}' does not exist."); }
    private static void ValidateName(string name) { if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Instance name is required.", nameof(name)); }
    private static void ValidateVersionId(string versionId) { if (string.IsNullOrWhiteSpace(versionId)) throw new ArgumentException("Version ID is required.", nameof(versionId)); }
}
