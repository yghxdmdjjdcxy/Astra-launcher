using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

#pragma warning disable CA1031 // Logging is diagnostic-only: no write failure may ever terminate the launcher.

namespace AstraLauncher.Infrastructure.Logging;

/// <summary>Writes launcher diagnostics to a size-rotated daily text file without allowing logging failures to stop the application.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const long DefaultMaxFileBytes = 10L * 1024 * 1024;
    private const int DefaultMaxRotatedFiles = 5;
    private const string FilePrefix = "astra-";
    private const string FileExtension = ".log";

    private readonly object _writeLock = new();
    private readonly string _logsDirectory;
    private readonly long _maxFileBytes;
    private readonly int _maxRotatedFiles;
    private int _writeFailure;

    public FileLoggerProvider(string logsDirectory, long maxFileBytes = DefaultMaxFileBytes, int maxRotatedFiles = DefaultMaxRotatedFiles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logsDirectory);
        _logsDirectory = logsDirectory;
        _maxFileBytes = maxFileBytes > 0 ? maxFileBytes : DefaultMaxFileBytes;
        _maxRotatedFiles = maxRotatedFiles > 0 ? maxRotatedFiles : DefaultMaxRotatedFiles;
    }

    /// <summary>Gets a value indicating whether at least one log write failed since the provider was created.</summary>
    public bool WriteFailureDetected => Volatile.Read(ref _writeFailure) != 0;

    public ILogger CreateLogger(string categoryName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(categoryName);
        return new FileLogger(this, categoryName);
    }

    /// <summary>Nothing to flush: every line is appended and closed synchronously by each log call.</summary>
    public void Dispose()
    {
    }

    private void Write<TState>(string categoryName, LogLevel logLevel, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        try
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            string timestamp = now.ToString("O", CultureInfo.InvariantCulture);
            string dateStamp = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string line = $"{timestamp} [{logLevel}] {categoryName}: {formatter(state, exception)}{Environment.NewLine}";
            if (exception is not null)
            {
                line = string.Concat(line, exception.ToString(), Environment.NewLine);
            }

            lock (_writeLock)
            {
                Directory.CreateDirectory(_logsDirectory);
                string currentPath = Path.Combine(_logsDirectory, $"{FilePrefix}{dateStamp}{FileExtension}");
                RotateIfNeeded(currentPath, dateStamp, Encoding.UTF8.GetByteCount(line));
                File.AppendAllText(currentPath, line);
            }
        }
        catch (Exception)
        {
            Interlocked.Exchange(ref _writeFailure, 1);
            // A locked, missing, or full log destination must not crash the launcher; later writes still attempt to log.
        }
    }

    private void RotateIfNeeded(string currentPath, string dateStamp, int appendedByteCount)
    {
        FileInfo current = new(currentPath);
        if (!current.Exists || current.Length + appendedByteCount <= _maxFileBytes)
        {
            return;
        }

        string oldestPath = RotatedPath(dateStamp, _maxRotatedFiles);
        if (File.Exists(oldestPath))
        {
            File.Delete(oldestPath);
        }

        for (int index = _maxRotatedFiles - 1; index >= 1; index--)
        {
            string sourcePath = RotatedPath(dateStamp, index);
            if (File.Exists(sourcePath))
            {
                File.Move(sourcePath, RotatedPath(dateStamp, index + 1), overwrite: true);
            }
        }

        File.Move(currentPath, RotatedPath(dateStamp, 1), overwrite: true);
    }

    private string RotatedPath(string dateStamp, int index) => Path.Combine(_logsDirectory, $"{FilePrefix}{dateStamp}.{index}{FileExtension}");

    private sealed class FileLogger(FileLoggerProvider provider, string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            provider.Write(categoryName, logLevel, state, exception, formatter);
        }
    }
}

#pragma warning restore CA1031
