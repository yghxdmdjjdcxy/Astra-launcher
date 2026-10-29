using AstraLauncher.App.ViewModels;
using Microsoft.Extensions.Logging;

namespace AstraLauncher.App.Services;

public enum PageKey { Welcome, Instances, Downloads, Mods, Accounts, Servers, Settings }

public interface INavigationService
{
    PageViewModel CurrentPage { get; }
    void Navigate(PageKey page);
}

public sealed class NavigationService : ObservableObject, INavigationService
{
    private readonly WelcomePageViewModel _welcome;
    private readonly InstancesPageViewModel _instances;
    private readonly DownloadsPageViewModel _downloads;
    private readonly ModsPageViewModel _mods;
    private readonly AccountsPageViewModel _accounts;
    private readonly ServersPageViewModel _servers;
    private readonly SettingsPageViewModel _settings;
    private readonly ILogger<NavigationService> _logger;
    private PageViewModel _currentPage;

    public NavigationService(
        WelcomePageViewModel welcome,
        InstancesPageViewModel instances,
        DownloadsPageViewModel downloads,
        ModsPageViewModel mods,
        AccountsPageViewModel accounts,
        ServersPageViewModel servers,
        SettingsPageViewModel settings,
        ILogger<NavigationService> logger)
    {
        _welcome = welcome;
        _instances = instances;
        _downloads = downloads;
        _mods = mods;
        _accounts = accounts;
        _servers = servers;
        _settings = settings;
        _logger = logger;
        _currentPage = welcome;
    }

    public PageViewModel CurrentPage { get => _currentPage; private set => SetProperty(ref _currentPage, value); }

    public void Navigate(PageKey page)
    {
        CurrentPage = page switch
        {
            PageKey.Welcome => _welcome,
            PageKey.Instances => _instances,
            PageKey.Downloads => _downloads,
            PageKey.Mods => _mods,
            PageKey.Accounts => _accounts,
            PageKey.Servers => _servers,
            PageKey.Settings => _settings,
            _ => throw new ArgumentOutOfRangeException(nameof(page), page, "Unknown application page.")
        };
        _logger.LogInformation("Navigated to {Page}", page);
    }
}
