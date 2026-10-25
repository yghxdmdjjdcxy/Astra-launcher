using System.IO;
using System.Text.Json;
using AstraLauncher.Core.Models;
using AstraLauncher.Infrastructure.FileSystem;
using AstraLauncher.Infrastructure.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AstraLauncher.Tests;

public sealed class SettingsServiceTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AstraLauncher.Tests", Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_root, "settings.json");

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
    public async Task GetAsync_WhenFileMissing_ReturnsDefaultsAndCachesInstance()
    {
        SettingsService service = CreateService();

        LauncherSettings settings = await service.GetAsync();

        Assert.Equal(4096, settings.MemoryMiB);
        Assert.Equal(4, settings.MaxConcurrentDownloads);
        Assert.Equal(DownloadSourceMode.OfficialWithFallback, settings.DownloadSourceMode);
        Assert.Equal("https://bmclapi2.bangbang93.com", settings.MirrorBaseUrl);
        Assert.Null(settings.JavaExecutablePath);
        Assert.Null(settings.GameArguments);
        Assert.Null(settings.JvmArguments);
        Assert.False(settings.ShowSnapshots);
        Assert.Null(settings.LastSelectedInstanceId);
        Assert.Null(settings.SelectedAccountId);
        Assert.False(File.Exists(SettingsPath));

        LauncherSettings secondRead = await service.GetAsync();
        Assert.Same(settings, secondRead);
    }

    [Fact]
    public async Task SaveAsync_ThenNewServiceLoads_RoundTripsNormalizedValues()
    {
        SettingsService service = CreateService();
        LauncherSettings settings = await service.GetAsync();
        settings.MemoryMiB = 8192;
        settings.MaxConcurrentDownloads = 6;
        settings.DownloadSourceMode = DownloadSourceMode.Mirror;
        settings.MirrorBaseUrl = "  https://mirror.example.com/  ";
        settings.JavaExecutablePath = @"  C:\Java\bin\javaw.exe  ";
        settings.GameArguments = "  --demo  ";
        settings.JvmArguments = "  -XX:+UseG1GC  ";
        settings.ShowSnapshots = true;
        settings.LastSelectedInstanceId = Guid.NewGuid();
        settings.SelectedAccountId = Guid.NewGuid();

        LauncherSettings? notified = null;
        service.Changed += (_, value) => notified = value;
        await service.SaveAsync(settings);

        Assert.True(File.Exists(SettingsPath));
        Assert.NotNull(notified);
        Assert.Same(settings, notified);

        SettingsService reloadedService = CreateService();
        LauncherSettings reloaded = await reloadedService.GetAsync();
        Assert.Equal(8192, reloaded.MemoryMiB);
        Assert.Equal(6, reloaded.MaxConcurrentDownloads);
        Assert.Equal(DownloadSourceMode.Mirror, reloaded.DownloadSourceMode);
        Assert.Equal("https://mirror.example.com", reloaded.MirrorBaseUrl);
        Assert.Equal(@"C:\Java\bin\javaw.exe", reloaded.JavaExecutablePath);
        Assert.Equal("--demo", reloaded.GameArguments);
        Assert.Equal("-XX:+UseG1GC", reloaded.JvmArguments);
        Assert.True(reloaded.ShowSnapshots);
        Assert.Equal(settings.LastSelectedInstanceId, reloaded.LastSelectedInstanceId);
        Assert.Equal(settings.SelectedAccountId, reloaded.SelectedAccountId);
    }

    [Fact]
    public async Task SaveAsync_ClampsAndRepairsInvalidValues()
    {
        SettingsService service = CreateService();
        var invalid = new LauncherSettings
        {
            MemoryMiB = 1,
            MaxConcurrentDownloads = 0,
            DownloadSourceMode = (DownloadSourceMode)99,
            MirrorBaseUrl = "not a mirror url",
            JavaExecutablePath = "   ",
            GameArguments = string.Empty,
            JvmArguments = "\t",
        };

        await service.SaveAsync(invalid);
        LauncherSettings settings = await service.GetAsync();

        Assert.Equal(512, settings.MemoryMiB);
        Assert.Equal(1, settings.MaxConcurrentDownloads);
        Assert.Equal(DownloadSourceMode.OfficialWithFallback, settings.DownloadSourceMode);
        Assert.Equal("https://bmclapi2.bangbang93.com", settings.MirrorBaseUrl);
        Assert.Null(settings.JavaExecutablePath);
        Assert.Null(settings.GameArguments);
        Assert.Null(settings.JvmArguments);

        invalid.MemoryMiB = 999_999;
        invalid.MaxConcurrentDownloads = 1000;
        await service.SaveAsync(invalid);
        Assert.Equal(65536, invalid.MemoryMiB);
        Assert.Equal(32, invalid.MaxConcurrentDownloads);
    }

    [Fact]
    public async Task GetAsync_WhenDocumentIsCorrupt_QuarantinesFileAndReturnsDefaults()
    {
        Directory.CreateDirectory(_root);
        const string corruptDocument = "{ \"MemoryMiB\": 4096, this is not json";
        await File.WriteAllTextAsync(SettingsPath, corruptDocument);

        SettingsService service = CreateService();
        LauncherSettings settings = await service.GetAsync();

        Assert.Equal(4096, settings.MemoryMiB);
        Assert.False(File.Exists(SettingsPath));
        string[] backups = Directory.GetFiles(_root, "settings.corrupt-*.json");
        Assert.Single(backups);
        Assert.Equal(corruptDocument, await File.ReadAllTextAsync(backups[0]));
    }

    [Fact]
    public async Task GetAsync_WhenDocumentIsEmpty_TreatsItAsCorrupt()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(SettingsPath, string.Empty);

        SettingsService service = CreateService();
        LauncherSettings settings = await service.GetAsync();

        Assert.Equal(4096, settings.MemoryMiB);
        Assert.Single(Directory.GetFiles(_root, "settings.corrupt-*.json"));
    }

    [Fact]
    public async Task SaveAsync_ConcurrentWrites_LeaveNoTemporaryFiles()
    {
        SettingsService service = CreateService();

        await Task.WhenAll(Enumerable.Range(0, 16).Select(index => service.SaveAsync(new LauncherSettings { MemoryMiB = 1024 + (index * 64) })));

        Assert.Empty(Directory.GetFiles(_root, "*.tmp", SearchOption.AllDirectories));
        Assert.True(File.Exists(SettingsPath));

        string document = await File.ReadAllTextAsync(SettingsPath);
        LauncherSettings? persisted = JsonSerializer.Deserialize<LauncherSettings>(document);
        Assert.NotNull(persisted);
        Assert.InRange(persisted!.MemoryMiB, 512, 65536);
    }

    private SettingsService CreateService() => new(new LauncherPaths(_root), NullLogger<SettingsService>.Instance);
}
