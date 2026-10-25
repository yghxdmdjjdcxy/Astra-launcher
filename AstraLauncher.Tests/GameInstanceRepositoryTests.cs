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
}
