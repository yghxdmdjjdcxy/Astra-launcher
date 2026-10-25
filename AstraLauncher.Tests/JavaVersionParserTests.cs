using AstraLauncher.Game.Java;

namespace AstraLauncher.Tests;

public sealed class JavaVersionParserTests
{
    [Theory]
    [InlineData("openjdk version \"1.8.0_452\"", 8)]
    [InlineData("openjdk version \"17.0.12\" 2024-07-16", 17)]
    [InlineData("java version \"21.0.4\"", 21)]
    public void TryParse_RecognizesSupportedJavaVersionFormats(string output, int expectedMajor)
    {
        bool parsed = JavaVersionParser.TryParse(output, @"C:\Java\bin\java.exe", out JavaRuntimeInfo? runtime);

        Assert.True(parsed);
        Assert.NotNull(runtime);
        Assert.Equal(expectedMajor, runtime.MajorVersion);
    }

    [Fact]
    public void TryParse_ReturnsFalseForUnrecognizedOutput()
    {
        Assert.False(JavaVersionParser.TryParse("not a java executable", "java.exe", out _));
    }
}
