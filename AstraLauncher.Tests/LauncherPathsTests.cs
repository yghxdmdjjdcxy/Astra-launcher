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
    }
}
