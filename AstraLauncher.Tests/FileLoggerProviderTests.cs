using System.IO;
using AstraLauncher.Infrastructure.Logging;
using Microsoft.Extensions.Logging;

namespace AstraLauncher.Tests;

public sealed class FileLoggerProviderTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AstraLauncher.Tests", Guid.NewGuid().ToString("N"));

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public void Log_WritesUtcDatedFile()
    {
        using var provider = new FileLoggerProvider(_root);

        Log(provider.CreateLogger("AstraLauncher.Tests"), "hello from the logger");

        string[] files = Directory.GetFiles(_root, "astra-*.log");
        string file = Assert.Single(files);
        Assert.Matches(@"astra-\d{4}-\d{2}-\d{2}\.log", Path.GetFileName(file));
        Assert.Contains("hello from the logger", File.ReadAllText(file));
        Assert.Contains("[Information]", File.ReadAllText(file));
        Assert.False(provider.WriteFailureDetected);
    }

    [Fact]
    public void Log_RotatesWhenFileWouldExceedMaximumBytes_AndKeepsNewestFiles()
    {
        using var provider = new FileLoggerProvider(_root, maxFileBytes: 32, maxRotatedFiles: 2);
        ILogger log = provider.CreateLogger("Rotation");

        for (int index = 0; index < 6; index++)
        {
            Log(log, $"rotation line {index}");
        }

        string[] files = Directory.GetFiles(_root, "astra-*.log");
        Assert.Equal(3, files.Length);

        string current = Assert.Single(files, file => !file.EndsWith(".1.log", StringComparison.Ordinal) && !file.EndsWith(".2.log", StringComparison.Ordinal));
        string firstRotated = Assert.Single(files, file => file.EndsWith(".1.log", StringComparison.Ordinal));
        string secondRotated = Assert.Single(files, file => file.EndsWith(".2.log", StringComparison.Ordinal));

        Assert.Contains("rotation line 5", File.ReadAllText(current));
        Assert.Contains("rotation line 4", File.ReadAllText(firstRotated));
        Assert.Contains("rotation line 3", File.ReadAllText(secondRotated));
        Assert.False(provider.WriteFailureDetected);
    }

    [Fact]
    public void Log_DoesNotThrow_WhenLogDirectoryCannotBeCreated()
    {
        Directory.CreateDirectory(_root);
        string blockerPath = Path.Combine(_root, "blocked");
        File.WriteAllText(blockerPath, "this file prevents creating a directory below it");

        using var provider = new FileLoggerProvider(Path.Combine(blockerPath, "logs"));
        ILogger log = provider.CreateLogger("Broken");

        Log(log, "first message");
        Log(log, "second message");

        Assert.True(provider.WriteFailureDetected);
    }

    [Fact]
    public void Log_IncludesExceptionDetails()
    {
        using var provider = new FileLoggerProvider(_root);
        ILogger log = provider.CreateLogger("Exceptions");

        log.Log(LogLevel.Error, new EventId(7, "Boom"), "the operation failed", new InvalidOperationException("boom-detail"), (state, _) => state);

        string file = Assert.Single(Directory.GetFiles(_root, "astra-*.log"));
        string contents = File.ReadAllText(file);
        Assert.Contains("the operation failed", contents);
        Assert.Contains("boom-detail", contents);
        Assert.Contains("[Error]", contents);
    }

    private static void Log(ILogger logger, string message) =>
        logger.Log(LogLevel.Information, new EventId(0), message, null, (state, _) => state);
}
