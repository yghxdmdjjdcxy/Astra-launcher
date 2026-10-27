using System.Windows.Input;
using AstraLauncher.App.Commands;
using AstraLauncher.App.Services;
using AstraLauncher.Core.Models;
using AstraLauncher.Core.Services;
using Microsoft.Extensions.Logging;

namespace AstraLauncher.App.ViewModels;

public sealed class ShellViewModel
{
    private readonly InstancesPageViewModel _instances;
    private readonly DownloadsPageViewModel _downloads;
    private readonly SettingsPageViewModel _settings;
    private readonly AccountsPageViewModel _accounts;
    private readonly IVersionManifestService _manifest;
    private readonly ILogger<ShellViewModel> _logger;

    public ShellViewModel(INavigationService navigation, InstancesPageViewModel instances, DownloadsPageViewModel downloads, SettingsPageViewModel settings, AccountsPageViewModel accounts, IVersionManifestService manifest, ILogger<ShellViewModel> logger)
    {
        Navigation = navigation;
        _instances = instances;
        _downloads = downloads;
        _settings = settings;
        _accounts = accounts;
        _manifest = manifest;
        _logger = logger;
        NavigateCommand = new RelayCommand(Navigate);
    }

    public INavigationService Navigation { get; }

    public ICommand NavigateCommand { get; }

    public async Task InitializeAsync()
    {
        await _instances.InitializeAsync();
        await _downloads.InitializeAsync();
        try
        {
            await _settings.InitializeAsync();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "设置页初始化失败；设置页将使用默认值。");
        }

        try
        {
            await _accounts.InitializeAsync();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "账户页初始化失败；账户页稍后可手动刷新。");
        }

        try
        {
            IReadOnlyList<VersionSummary> versions = await _manifest.GetVersionsAsync(includeSnapshots: false);
            _instances.SetAvailableVersions(versions.Select(version => version.Id));
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "版本列表加载失败；实例页仍可手动输入版本号。");
        }
    }

    private void Navigate(object? parameter)
    {
        if (parameter is not string pageName || !Enum.TryParse(pageName, out PageKey page))
            throw new ArgumentException("The navigation command requires a known page name.", nameof(parameter));
        Navigation.Navigate(page);
    }
}
