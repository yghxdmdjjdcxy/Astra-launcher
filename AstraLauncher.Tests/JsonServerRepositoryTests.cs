using System.IO;
using AstraLauncher.Core.Models;
using AstraLauncher.Infrastructure.FileSystem;
using AstraLauncher.Infrastructure.Servers;
using Microsoft.Extensions.Logging.Abstractions;

namespace AstraLauncher.Tests;

public sealed class JsonServerRepositoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AstraLauncher.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SaveAndList_PersistsNewServersAndUpdatesExistingOnes()
    {
        JsonServerRepository repository = CreateRepository();
        var first = new SavedServer(Guid.NewGuid(), "生存服", "mc.example.com", 25565);
        var second = new SavedServer(Guid.NewGuid(), "创造服", "creative.example.com");

        await repository.SaveAsync(first);
        await repository.SaveAsync(second);
        IReadOnlyList<SavedServer> listed = await repository.ListAsync();

        Assert.Equal(2, listed.Count);
        Assert.Contains(listed, server => server == second);
        Assert.Contains(listed, server => server.Name == "生存服" && server.Port == 25565);

        var updated = first with { Name = "生存服（新）", Port = 25566 };
        await repository.SaveAsync(updated);
        IReadOnlyList<SavedServer> afterUpdate = await repository.ListAsync();

        Assert.Equal(2, afterUpdate.Count);
        SavedServer saved = Assert.Single(afterUpdate, server => server.Id == first.Id);
        Assert.Equal("生存服（新）", saved.Name);
        Assert.Equal(25566, saved.Port);
        Assert.Equal("mc.example.com:25566", saved.DisplayAddress);
    }

    [Fact]
    public async Task Delete_RemovesOnlyTheRequestedServer()
    {
        JsonServerRepository repository = CreateRepository();
        var keep = new SavedServer(Guid.NewGuid(), "Keep", "keep.example.com");
        var remove = new SavedServer(Guid.NewGuid(), "Remove", "remove.example.com");
        await repository.SaveAsync(keep);
        await repository.SaveAsync(remove);

        await repository.DeleteAsync(remove.Id);
        IReadOnlyList<SavedServer> listed = await repository.ListAsync();

        Assert.Single(listed);
        Assert.Equal(keep.Id, listed[0].Id);
    }

    [Fact]
    public async Task ListAsync_MalformedFile_ReturnsEmptyListInsteadOfThrowing()
    {
        JsonServerRepository repository = CreateRepository();

        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "servers.json"), "{ not valid json");

        IReadOnlyList<SavedServer> listed = await repository.ListAsync();

        Assert.Empty(listed);
    }

    [Fact]
    public async Task ListAsync_MissingFile_ReturnsEmptyList()
    {
        JsonServerRepository repository = CreateRepository();

        IReadOnlyList<SavedServer> listed = await repository.ListAsync();

        Assert.Empty(listed);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private JsonServerRepository CreateRepository() =>
        new(new LauncherPaths(_root), NullLogger<JsonServerRepository>.Instance);
}
