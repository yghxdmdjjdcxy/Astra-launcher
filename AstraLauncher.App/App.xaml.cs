using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using AstraLauncher.App.Services;
using AstraLauncher.App.ViewModels;
using AstraLauncher.Authentication;
using AstraLauncher.Core.Models;
using AstraLauncher.Core.Services;
using AstraLauncher.Downloader;
using AstraLauncher.Game.Install;
using AstraLauncher.Game.Instances;
using AstraLauncher.Game.Java;
using AstraLauncher.Game.Launch;
using AstraLauncher.Game.Loaders;
using AstraLauncher.Game.Metadata;
using AstraLauncher.Game.Mods;
using AstraLauncher.Game.Diagnostics;
using AstraLauncher.Infrastructure.FileSystem;
using AstraLauncher.Infrastructure.Logging;
using AstraLauncher.Infrastructure.Net;
using AstraLauncher.Infrastructure.Servers;
using AstraLauncher.Infrastructure.Settings;
using AstraLauncher.Infrastructure.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AstraLauncher.App;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\AstraLauncher.SingleInstance";
    private IHost? _host;
    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        if (!TryAcquireSingleInstance())
        {
            MessageBox.Show("Astra Launcher 已在运行。", "Astra Launcher", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        try
        {
            LauncherBootstrapResult bootstrap = CreateLauncherPaths();
            _host = CreateHost(bootstrap.Paths);
            _host.Start();
            ILogger<App> logger = _host.Services.GetRequiredService<ILogger<App>>();
            if (!string.IsNullOrWhiteSpace(bootstrap.MigrationWarning))
            {
                logger.LogWarning("Launcher data migration failed: {Error}. The legacy data directory is still in use.", bootstrap.MigrationWarning);
            }

            logger.LogInformation(
                "Minecraft directory resolved to {MinecraftDirectory}; launcher data directory is {LauncherDataDirectory}.",
                bootstrap.Paths.MinecraftDirectory,
                bootstrap.Paths.ApplicationDataDirectory);
            MinecraftDataMigrator.MigrateLegacyMinecraftData(bootstrap.Paths, logger);
            _host.Services.GetRequiredService<MainWindow>().Show();
        }
        catch (Exception exception)
        {
            LogCritical(exception, "Launcher startup failed");
            MessageBox.Show($"Astra Launcher 启动失败，请查看日志。{Environment.NewLine}{exception.Message}", "Astra Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            try
            {
                // Run the host shutdown on the thread pool so a hosted service that needs the UI thread cannot deadlock the UI thread.
                Task.Run(() => _host.StopAsync()).GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                // Shutdown is best-effort; the process is exiting anyway.
            }

            _host.Dispose();
        }

        ReleaseSingleInstance();
        base.OnExit(e);
    }

    private static IHost CreateHost(LauncherPaths paths)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.Logging.AddDebug();
        builder.Logging.AddProvider(new FileLoggerProvider(paths.LogsDirectory));
        builder.Services.AddSingleton<ILauncherPaths>(paths);
        builder.Services.AddSingleton<ISettingsService, SettingsService>();
        builder.Services.AddSingleton<IDownloadSourceResolver>(_ => new DownloadSourceResolver(LauncherSettings.DefaultMirrorBaseUrl));
        builder.Services.AddSingleton<IDialogService, MessageBoxDialogService>();
        builder.Services.AddSingleton(_ => CreateHttpClient());
        builder.Services.AddSingleton(sp => new DownloadQueue(sp.GetRequiredService<HttpClient>(), new DownloadQueueOptions
        {
            MaximumConcurrentDownloads = 8,
            MaximumConcurrentDownloadsPerHost = 6,
            EnableSegmentedDownloads = true,
            SegmentsPerFile = 4,
            MinimumSegmentSizeBytes = 2 * 1024 * 1024,
            MaximumConcurrentSegments = 8,
        }));
        builder.Services.AddSingleton<IVersionManifestService, VersionManifestService>();
        builder.Services.AddSingleton<VersionMetadataStore>();
        builder.Services.AddSingleton<IVersionInstallService, VersionInstallService>();
        builder.Services.AddSingleton<ILoaderCatalog, LoaderCatalog>();
        builder.Services.AddSingleton<IFabricLikeLoaderInstaller, FabricLikeLoaderInstaller>();
        builder.Services.AddSingleton<IExternalProcessRunner, ExternalProcessRunner>();
        builder.Services.AddSingleton<IForgeLikeLoaderInstaller, ForgeLikeLoaderInstaller>();
        builder.Services.AddSingleton<ILoaderInstallService, LoaderInstallService>();
        builder.Services.AddSingleton<GameCommandBuilder>();
        builder.Services.AddSingleton<IGameProcessLauncher, GameProcessLauncher>();
        builder.Services.AddSingleton<IGameLaunchService, GameLaunchService>();
        builder.Services.AddSingleton<IGameInstanceRepository, GameInstanceRepository>();
        builder.Services.AddSingleton<IJavaRuntimeDetector, JavaRuntimeDetector>();
        builder.Services.AddSingleton<ITokenProtector, DpapiTokenProtector>();
        builder.Services.AddSingleton<IAccountStore, AccountStore>();
        builder.Services.AddSingleton<IDeviceCodeOAuthClient, DeviceCodeOAuthClient>();
        builder.Services.AddSingleton<IMicrosoftInteractiveLoginClient, WebViewMicrosoftLoginClient>();
        builder.Services.AddSingleton<IXboxLiveAuthenticator, XboxLiveAuthenticator>();
        builder.Services.AddSingleton<IMinecraftAuthenticator, MinecraftAuthenticator>();
        builder.Services.AddSingleton<IAccountService, AccountService>();
        builder.Services.AddSingleton<IServerRepository, JsonServerRepository>();
        builder.Services.AddSingleton<ILocalModService, LocalModService>();
        builder.Services.AddSingleton<IModrinthClient, ModrinthClient>();
        builder.Services.AddSingleton<IFilePickerService, OpenFileDialogService>();
        builder.Services.AddSingleton<ICrashAnalyzer, CrashAnalyzer>();
        builder.Services.AddSingleton<ICrashRepairService, CrashRepairService>();
        builder.Services.AddSingleton<ICrashDialogService, CrashAnalysisDialogService>();
        builder.Services.AddSingleton<INavigationService, NavigationService>();
        builder.Services.AddSingleton<WelcomePageViewModel>();
        builder.Services.AddSingleton<InstancesPageViewModel>();
        builder.Services.AddSingleton<DownloadsPageViewModel>();
        builder.Services.AddSingleton<ModsPageViewModel>();
        builder.Services.AddSingleton<AccountsPageViewModel>();
        builder.Services.AddSingleton<ServersPageViewModel>();
        builder.Services.AddSingleton<SettingsPageViewModel>();
        builder.Services.AddSingleton<ShellViewModel>();
        builder.Services.AddSingleton<MainWindow>();
        return builder.Build();
    }

    /// <summary>
    /// Resolves the Minecraft root and the launcher data directory. Launcher-owned data (settings, accounts, logs,
    /// instances, caches) lives in <c>&lt;MinecraftDirectory&gt;\AstraLauncher</c> so a portable <c>.minecraft</c> folder
    /// contains everything; data from the legacy <c>%LOCALAPPDATA%\AstraLauncher</c> layout is moved on first start.
    /// </summary>
    private static LauncherBootstrapResult CreateLauncherPaths()
    {
        string defaultMinecraft = LauncherPaths.ResolveDefaultMinecraftDirectory();
        string legacyData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AstraLauncher");
        string defaultData = Path.Combine(defaultMinecraft, "AstraLauncher");

        string sourceData = File.Exists(Path.Combine(defaultData, "settings.json"))
            ? defaultData
            : (File.Exists(Path.Combine(legacyData, "settings.json")) ? legacyData : defaultData);

        LauncherSettings? settings = TryReadSettings(Path.Combine(sourceData, "settings.json"));
        string minecraft = defaultMinecraft;
        if (!string.IsNullOrWhiteSpace(settings?.MinecraftDirectory))
        {
            try
            {
                minecraft = Path.GetFullPath(settings.MinecraftDirectory.Trim());
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
            {
                // Keep the resolved default when the configured path is unusable.
            }
        }

        string launcherData = Path.Combine(minecraft, "AstraLauncher");
        string? migrationWarning = null;
        bool targetHasSettings = File.Exists(Path.Combine(launcherData, "settings.json"));
        if (!targetHasSettings && !AreSamePath(sourceData, launcherData) && Directory.Exists(sourceData))
        {
            if (!LauncherDataMigrator.MigrateIfNeeded(sourceData, launcherData, out string? error))
            {
                migrationWarning = error;
            }
        }
        else if (!targetHasSettings && !Directory.Exists(sourceData) && Directory.Exists(legacyData) && !AreSamePath(legacyData, launcherData))
        {
            if (!LauncherDataMigrator.MigrateIfNeeded(legacyData, launcherData, out string? error))
            {
                migrationWarning = error;
            }
        }

        var paths = new LauncherPaths(launcherData, minecraft);
        paths.EnsureCreated();
        return new LauncherBootstrapResult(paths, migrationWarning);
    }

    private static LauncherSettings? TryReadSettings(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            return JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(path), SettingsJsonOptions);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static bool AreSamePath(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions SettingsJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed record LauncherBootstrapResult(LauncherPaths Paths, string? MigrationWarning);

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AstraLauncher/1.0");
        return client;
    }

    private bool TryAcquireSingleInstance()
    {
        var mutex = new Mutex(initiallyOwned: false, SingleInstanceMutexName);
        try
        {
            if (mutex.WaitOne(0))
            {
                _singleInstanceMutex = mutex;
                return true;
            }
        }
        catch (AbandonedMutexException)
        {
            // The previous launcher crashed without releasing the mutex; take ownership.
            _singleInstanceMutex = mutex;
            return true;
        }

        mutex.Dispose();
        return false;
    }

    private void ReleaseSingleInstance()
    {
        if (_singleInstanceMutex is null) return;
        try
        {
            _singleInstanceMutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // The mutex was abandoned or already released; disposing below is enough.
        }

        _singleInstanceMutex.Dispose();
        _singleInstanceMutex = null;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogCritical(e.Exception, "Unhandled dispatcher exception");
        MessageBox.Show("Astra Launcher 遇到了未处理的错误。请查看日志并重试。", "Astra Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e) => LogCritical(e.ExceptionObject as Exception, "Unhandled application-domain exception");

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogCritical(e.Exception, "Unobserved task exception");
        e.SetObserved();
    }

    private void LogCritical(Exception? exception, string message) => _host?.Services.GetRequiredService<ILogger<App>>().LogCritical(exception, "{Message}", message);
}
