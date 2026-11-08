using AstraLauncher.App.ViewModels;
using Microsoft.Extensions.Logging;

namespace AstraLauncher.App.Services;

public enum PageKey { Welcome, Instances, Downloads, Mods, Accounts, Servers, Toolbox, CloudDrive, Settings, Multiplayer, Plugins }

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
    private readonly ToolboxPageViewModel _toolbox;
    private readonly CloudDrivePageViewModel _cloudDrive;
    private readonly SettingsPageViewModel _settings;
    private readonly MultiplayerPageViewModel? _multiplayer;
    private readonly PluginsPageViewModel? _plugins;
    private readonly ILogger<NavigationService> _logger;
    private PageViewModel _currentPage;

    public NavigationService(
        WelcomePageViewModel welcome,
        InstancesPageViewModel instances,
        DownloadsPageViewModel downloads,
        ModsPageViewModel mods,
        AccountsPageViewModel accounts,
        ServersPageViewModel servers,
        ToolboxPageViewModel toolbox,
        CloudDrivePageViewModel cloudDrive,
        SettingsPageViewModel settings,
        ILogger<NavigationService> logger,
        MultiplayerPageViewModel? multiplayer = null,
        PluginsPageViewModel? plugins = null)
    {
        _welcome = welcome;
        _instances = instances;
        _downloads = downloads;
        _mods = mods;
        _accounts = accounts;
        _servers = servers;
        _toolbox = toolbox;
        _cloudDrive = cloudDrive;
        _settings = settings;
        _multiplayer = multiplayer;
        _plugins = plugins;
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
            PageKey.Toolbox => _toolbox,
            PageKey.CloudDrive => _cloudDrive,
            PageKey.Settings => _settings,
            PageKey.Multiplayer => _multiplayer ?? throw new InvalidOperationException("联机页面未注册到导航服务。"),
            PageKey.Plugins => _plugins ?? throw new InvalidOperationException("插件页面未注册到导航服务。"),
            _ => throw new ArgumentOutOfRangeException(nameof(page), page, "Unknown application page.")
        };
        _logger.LogInformation("Navigated to {Page}", page);
    }
}
