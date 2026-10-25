using Microsoft.Extensions.Logging;

namespace AstraLauncher.Infrastructure.Logging;

/// <summary>Writes launcher diagnostics to a daily text file without allowing logging failures to stop the application.</summary>
public sealed class FileLoggerProvider(string logsDirectory) : ILoggerProvider
{
    private readonly object _writeLock = new();

    public ILogger CreateLogger(string categoryName) => new FileLogger(logsDirectory, categoryName, _writeLock);

    public void Dispose()
    {
    }

    private sealed class FileLogger(string logsDirectory, string categoryName, object writeLock) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            string path = Path.Combine(logsDirectory, $"astra-{DateTime.UtcNow:yyyy-MM-dd}.log");
            string line = $"{DateTimeOffset.UtcNow:O} [{logLevel}] {categoryName}: {formatter(state, exception)}{Environment.NewLine}";
            if (exception is not null) line += $"{exception}{Environment.NewLine}";

            try
            {
                lock (writeLock)
                {
                    Directory.CreateDirectory(logsDirectory);
                    File.AppendAllText(path, line);
                }
            }
            catch (IOException)
            {
                // Logging is diagnostic-only; a locked/full log destination must not crash the launcher.
            }
            catch (UnauthorizedAccessException)
            {
                // The user can still run the launcher even if the selected profile directory is protected.
            }
        }
    }
}
