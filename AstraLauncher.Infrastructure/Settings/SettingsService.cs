using System.Globalization;
using System.Text.Json;
using AstraLauncher.Core.Models;
using AstraLauncher.Core.Services;
using Microsoft.Extensions.Logging;

namespace AstraLauncher.Infrastructure.Settings;

/// <summary>Persists <see cref="LauncherSettings"/> to <c>settings.json</c> and keeps the loaded instance cached.</summary>
public sealed class SettingsService : ISettingsService
{
    private const string SettingsFileName = "settings.json";
    private const string CorruptFilePrefix = "settings.corrupt-";
    private const string JsonFileExtension = ".json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly ILogger<SettingsService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _settingsDirectory;
    private readonly string _settingsFilePath;
    private LauncherSettings? _cached;

    public SettingsService(ILauncherPaths paths, ILogger<SettingsService> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _settingsDirectory = paths.ApplicationDataDirectory;
        _settingsFilePath = Path.Combine(_settingsDirectory, SettingsFileName);
    }

    public event EventHandler<LauncherSettings>? Changed;

    public async Task<LauncherSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        LauncherSettings? cached = _cached;
        if (cached is not null)
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cached = _cached;
            if (cached is null)
            {
                cached = await LoadAsync(cancellationToken).ConfigureAwait(false);
                _cached = cached;
            }

            return cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(LauncherSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            settings.Normalize();
            Directory.CreateDirectory(_settingsDirectory);
            string temporaryPath = $"{_settingsFilePath}.{Guid.NewGuid():N}.tmp";
            try
            {
                await using (FileStream stream = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                File.Move(temporaryPath, _settingsFilePath, overwrite: true);
            }
            finally
            {
                TryDelete(temporaryPath);
            }

            _cached = settings;
        }
        finally
        {
            _gate.Release();
        }

        EventHandler<LauncherSettings>? handler = Changed;
        handler?.Invoke(this, settings);
    }

    private async Task<LauncherSettings> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_settingsFilePath))
            {
                return new LauncherSettings();
            }

            LauncherSettings? loaded;
            await using (FileStream stream = new(_settingsFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                loaded = await JsonSerializer.DeserializeAsync<LauncherSettings>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            }

            if (loaded is null)
            {
                return new LauncherSettings();
            }

            loaded.Normalize();
            return loaded;
        }
        catch (JsonException exception)
        {
            QuarantineCorruptSettingsFile(exception);
            return new LauncherSettings();
        }
        catch (IOException exception)
        {
            _logger.LogWarning(exception, "Failed to read settings from {SettingsPath}; defaults are used for this session.", _settingsFilePath);
            return new LauncherSettings();
        }
        catch (UnauthorizedAccessException exception)
        {
            _logger.LogWarning(exception, "Access to settings file {SettingsPath} was denied; defaults are used for this session.", _settingsFilePath);
            return new LauncherSettings();
        }
    }

    /// <summary>Moves a corrupt settings document aside so the evidence is preserved instead of silently overwritten.</summary>
    private void QuarantineCorruptSettingsFile(JsonException exception)
    {
        string timestamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        string corruptPath = Path.Combine(_settingsDirectory, $"{CorruptFilePrefix}{timestamp}{JsonFileExtension}");

        try
        {
            File.Move(_settingsFilePath, corruptPath, overwrite: true);
            _logger.LogWarning(exception, "Settings file {SettingsPath} was corrupt; it was moved to {CorruptPath} and defaults are used.", _settingsFilePath, corruptPath);
        }
        catch (IOException quarantineException)
        {
            _logger.LogWarning(quarantineException, "Settings file {SettingsPath} is corrupt and could not be quarantined; defaults are used.", _settingsFilePath);
        }
        catch (UnauthorizedAccessException quarantineException)
        {
            _logger.LogWarning(quarantineException, "Settings file {SettingsPath} is corrupt and could not be quarantined; defaults are used.", _settingsFilePath);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // The temporary document is best-effort cleanup; stale files are ignored on the next save.
        }
        catch (UnauthorizedAccessException)
        {
            // Same as above: a locked temporary file must not turn a successful save into a failure.
        }
    }
}
