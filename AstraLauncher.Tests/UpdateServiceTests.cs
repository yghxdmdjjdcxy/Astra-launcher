using AstraLauncher.App.Services;

namespace AstraLauncher.Tests;

public sealed class UpdateServiceTests
{
    [Theory]
    [InlineData("v1.0.1", "1.0.0", true)]
    [InlineData("v1.0.0", "1.0.0", false)]
    [InlineData("v1.2.0", "1.1.9", true)]
    [InlineData("1.0.1", "1.0.0", true)]
    [InlineData("v2.0.0-beta", "1.9.9", true)]
    [InlineData("v1.0.0", "1.0.1", false)]
    [InlineData("not-a-version", "1.0.0", false)]
    public void IsNewerTag_ComparesSemanticVersions(string tag, string current, bool expected)
    {
        Assert.Equal(expected, UpdateService.IsNewerTag(tag, current));
    }

    [Fact]
    public void IsNewerTag_HandlesBuildRevision()
    {
        Assert.True(UpdateService.IsNewerTag("v1.0.1", new Version(1, 0, 0, 5)));
        Assert.False(UpdateService.IsNewerTag("v1.0.0", new Version(1, 0, 0, 5)));
    }
}
