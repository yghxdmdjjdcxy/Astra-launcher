using System.Windows;
using System.Windows.Threading;
using AstraLauncher.App.Services;
using AstraLauncher.App.ViewModels;
using AstraLauncher.Core.Services;
using AstraLauncher.Infrastructure.FileSystem;
using AstraLauncher.Infrastructure.Logging;
using AstraLauncher.Game.Instances;
using AstraLauncher.Game.Java;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AstraLauncher.App;

public partial class App : Application
{
    private IHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _host = CreateHost();
        _host.Start();
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        _host.Services.GetRequiredService<MainWindow>().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            _host.StopAsync().GetAwaiter().GetResult();
            _host.Dispose();
        }

        base.OnExit(e);
    }

    private static IHost CreateHost()
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        var paths = new LauncherPaths();
        builder.Logging.ClearProviders();
        builder.Logging.AddDebug();
        builder.Logging.AddProvider(new FileLoggerProvider(paths.LogsDirectory));
        builder.Services.AddSingleton<ILauncherPaths>(paths);
        builder.Services.AddSingleton<IGameInstanceRepository, GameInstanceRepository>();
        builder.Services.AddSingleton<IJavaRuntimeDetector, JavaRuntimeDetector>();
        builder.Services.AddSingleton<INavigationService, NavigationService>();
        builder.Services.AddSingleton<WelcomePageViewModel>();
        builder.Services.AddSingleton<InstancesPageViewModel>();
        builder.Services.AddSingleton<DownloadsPageViewModel>();
        builder.Services.AddSingleton<AccountsPageViewModel>();
        builder.Services.AddSingleton<ServersPageViewModel>();
        builder.Services.AddSingleton<ShellViewModel>();
        builder.Services.AddSingleton<MainWindow>();
        return builder.Build();
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
