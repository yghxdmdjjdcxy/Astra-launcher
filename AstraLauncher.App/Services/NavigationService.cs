using AstraLauncher.App.ViewModels;
using Microsoft.Extensions.Logging;

namespace AstraLauncher.App.Services;

public enum PageKey { Welcome, Instances, Downloads, Accounts, Servers }

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
    private readonly AccountsPageViewModel _accounts;
    private readonly ServersPageViewModel _servers;
    private readonly ILogger<NavigationService> _logger;
    private PageViewModel _currentPage;

    public NavigationService(WelcomePageViewModel welcome, InstancesPageViewModel instances, DownloadsPageViewModel downloads, AccountsPageViewModel accounts, ServersPageViewModel servers, ILogger<NavigationService> logger)
    {
        _welcome = welcome;
        _instances = instances;
        _downloads = downloads;
        _accounts = accounts;
        _servers = servers;
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
            PageKey.Accounts => _accounts,
            PageKey.Servers => _servers,
            _ => throw new ArgumentOutOfRangeException(nameof(page), page, "Unknown application page.")
        };
        _logger.LogInformation("Navigated to {Page}", page);
    }
}
