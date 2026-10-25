using AstraLauncher.Core.Models;
using AstraLauncher.Game.Instances;
using AstraLauncher.Infrastructure.FileSystem;
using System.IO;

namespace AstraLauncher.Tests;

public sealed class GameInstanceRepositoryTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AstraLauncher.Tests", Guid.NewGuid().ToString("N"));

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task CreateRenameDuplicateAndDelete_ManageOnlyLauncherDirectories()
    {
        var repository = new GameInstanceRepository(new LauncherPaths(_root));
        GameInstance created = await repository.CreateAsync("Vanilla", "1.21.1");
        await File.WriteAllTextAsync(Path.Combine(created.DirectoryPath, "options.txt"), "lang:en_us");

        GameInstance renamed = await repository.RenameAsync(created.Id, "My Vanilla");
        GameInstance duplicate = await repository.DuplicateAsync(created.Id, "Copy");

        Assert.Equal("My Vanilla", renamed.Name);
        Assert.True(duplicate.IsManagedDirectory);
        Assert.Equal("lang:en_us", await File.ReadAllTextAsync(Path.Combine(duplicate.DirectoryPath, "options.txt")));

        await repository.DeleteAsync(duplicate.Id, deleteManagedDirectory: true);

        Assert.False(Directory.Exists(duplicate.DirectoryPath));
        Assert.Single(await repository.ListAsync());
    }

    [Fact]
    public async Task DeleteAsync_NeverDeletesImportedDirectory()
    {
        string externalDirectory = Path.Combine(_root, "external");
        Directory.CreateDirectory(externalDirectory);
        var repository = new GameInstanceRepository(new LauncherPaths(_root));
        GameInstance imported = await repository.ImportAsync("Imported", "1.20.4", externalDirectory);

        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.DeleteAsync(imported.Id, deleteManagedDirectory: true));
        await repository.DeleteAsync(imported.Id, deleteManagedDirectory: false);

        Assert.True(Directory.Exists(externalDirectory));
        Assert.Empty(await repository.ListAsync());
    }

    [Fact]
    public async Task CorruptManifestIsQuarantinedAndRecoveredFromBackup()
    {
        var repository = new GameInstanceRepository(new LauncherPaths(_root));
        GameInstance created = await repository.CreateAsync("Vanilla", "1.21.1");
        // A second write produces the backup of the previous (valid) manifest.
        await repository.RenameAsync(created.Id, "Renamed");
        string manifest = Path.Combine(_root, "instances.json");
        Assert.True(File.Exists(manifest + ".bak"));

        await File.WriteAllTextAsync(manifest, "{ this is not valid json");

        IReadOnlyList<GameInstance> recovered = await repository.ListAsync();

        Assert.Single(recovered);
        Assert.Equal("Renamed", recovered[0].Name);
        Assert.NotEmpty(Directory.GetFiles(_root, "instances.json.corrupt-*.json"));
    }

    [Fact]
    public async Task DuplicateAsync_RefusesCopyIntoSourceDirectory()
    {
        var repository = new GameInstanceRepository(new LauncherPaths(_root));
        GameInstance created = await repository.CreateAsync("Vanilla", "1.21.1");
        string instancesDirectory = Path.GetFullPath(Path.Combine(_root, "instances"));
        GameInstance importedRoot = await repository.ImportAsync("InstancesRoot", "1.21.1", instancesDirectory);

        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.DuplicateAsync(importedRoot.Id, "CopyOfRoot"));

        await repository.DeleteAsync(importedRoot.Id, deleteManagedDirectory: false);
        Assert.True(Directory.Exists(created.DirectoryPath));
    }

    [Fact]
    public async Task DeleteAsync_FailedDirectoryDeleteKeepsManifestConsistent()
    {
        var repository = new GameInstanceRepository(new LauncherPaths(_root));
        GameInstance created = await repository.CreateAsync("Vanilla", "1.21.1");
        string lockedFile = Path.Combine(created.DirectoryPath, "locked.dat");
        await File.WriteAllTextAsync(lockedFile, "lock");

        using (FileStream _ = new(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAsync<IOException>(() => repository.DeleteAsync(created.Id, deleteManagedDirectory: true));
        }

        // The manifest was written before the directory deletion, so the instance list stays consistent.
        Assert.Empty(await repository.ListAsync());
        Assert.True(Directory.Exists(created.DirectoryPath));
    }
}
