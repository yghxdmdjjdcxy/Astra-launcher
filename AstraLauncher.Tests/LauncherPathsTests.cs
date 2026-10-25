using System.IO;
using AstraLauncher.Infrastructure.FileSystem;

namespace AstraLauncher.Tests;

public sealed class LauncherPathsTests
{
    [Fact]
    public void UsesProvidedApplicationDataDirectory()
    {
        var paths = new LauncherPaths(@"C:\AstraTest");
        Assert.Equal(@"C:\AstraTest", paths.ApplicationDataDirectory);
        Assert.Equal(@"C:\AstraTest\logs", paths.LogsDirectory);
        Assert.Equal(@"C:\AstraTest\instances", paths.InstancesDirectory);
        Assert.Equal(@"C:\AstraTest\versions", paths.VersionsDirectory);
        Assert.Equal(@"C:\AstraTest\libraries", paths.LibrariesDirectory);
        Assert.Equal(@"C:\AstraTest\assets", paths.AssetsDirectory);
        Assert.Equal(@"C:\AstraTest\java-runtime", paths.RuntimeDirectory);
        Assert.Equal(@"C:\AstraTest\cache", paths.CacheDirectory);
        Assert.Equal(@"C:\AstraTest\temp", paths.TempDirectory);
    }

    [Fact]
    public void EnsureCreated_CreatesEveryDirectory_Idempotently()
    {
        string root = Path.Combine(Path.GetTempPath(), "AstraLauncher.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new LauncherPaths(root);
            paths.EnsureCreated();
            paths.EnsureCreated();

            Assert.True(Directory.Exists(paths.ApplicationDataDirectory));
            Assert.True(Directory.Exists(paths.LogsDirectory));
            Assert.True(Directory.Exists(paths.InstancesDirectory));
            Assert.True(Directory.Exists(paths.VersionsDirectory));
            Assert.True(Directory.Exists(paths.LibrariesDirectory));
            Assert.True(Directory.Exists(paths.AssetsDirectory));
            Assert.True(Directory.Exists(paths.RuntimeDirectory));
            Assert.True(Directory.Exists(paths.CacheDirectory));
            Assert.True(Directory.Exists(paths.TempDirectory));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
