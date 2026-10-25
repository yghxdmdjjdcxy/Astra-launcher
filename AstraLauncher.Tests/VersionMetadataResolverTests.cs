using AstraLauncher.Game.Metadata;
using System.IO;

namespace AstraLauncher.Tests;

public sealed class VersionMetadataResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AstraLauncher.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ResolveAsync_MergesInheritedFieldsAndOverridesLibraries()
    {
        await WriteVersionAsync("parent", """
        {"id":"parent","mainClass":"parent.Main","javaVersion":{"component":"jre-legacy","majorVersion":8},"assetIndex":{"id":"legacy","sha1":"a","size":1,"totalSize":2,"url":"https://example.test/index"},"libraries":[{"name":"com.example:library:1.0"},{"name":"com.example:other:1.0"}],"arguments":{"game":["--parent"],"jvm":["-Dparent=true"]},"downloads":{"client":{"sha1":"abc","size":10,"url":"https://example.test/client"}}}
        """);
        await WriteVersionAsync("child", """
        {"id":"child","inheritsFrom":"parent","mainClass":"child.Main","libraries":[{"name":"com.example:library:2.0"}],"arguments":{"game":[{"rules":[{"action":"allow","os":{"name":"windows"}}],"value":["--child","value"]}]},"downloads":{"client":{"sha1":"def","size":11,"url":"https://example.test/child"}}}
        """);

        MinecraftVersionMetadata metadata = await new VersionMetadataResolver(_root).ResolveAsync("child");

        Assert.Equal("child.Main", metadata.MainClass);
        Assert.Equal(8, metadata.JavaVersion!.MajorVersion);
        Assert.Equal("legacy", metadata.AssetIndex!.Id);
        Assert.Equal(["--parent", "--child", "value"], metadata.Arguments.Game.SelectMany(argument => argument.Values));
        Assert.Equal(2, metadata.Libraries.Count);
        Assert.Contains(metadata.Libraries, library => library.Name == "com.example:library:2.0");
        Assert.Equal("def", metadata.Downloads["client"].Sha1);
    }

    [Fact]
    public async Task ResolveAsync_RejectsInheritanceCycles()
    {
        await WriteVersionAsync("one", """{"id":"one","inheritsFrom":"two"}""");
        await WriteVersionAsync("two", """{"id":"two","inheritsFrom":"one"}""");

        VersionMetadataException exception = await Assert.ThrowsAsync<VersionMetadataException>(() => new VersionMetadataResolver(_root).ResolveAsync("one"));

        Assert.Contains("cycle", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private async Task WriteVersionAsync(string id, string content)
    {
        string directory = Path.Combine(_root, "versions", id);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, $"{id}.json"), content);
    }
}
