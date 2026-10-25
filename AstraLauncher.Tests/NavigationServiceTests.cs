using AstraLauncher.App.Services;
using AstraLauncher.App.ViewModels;
using AstraLauncher.Game.Instances;
using AstraLauncher.Game.Java;
using AstraLauncher.Infrastructure.FileSystem;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO;

namespace AstraLauncher.Tests;

public sealed class NavigationServiceTests
{
    [Fact]
    public void Navigate_ChangesCurrentPage()
    {
        var navigation = new NavigationService(new WelcomePageViewModel(), new InstancesPageViewModel(new GameInstanceRepository(new LauncherPaths(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()))), new JavaRuntimeDetector()), new DownloadsPageViewModel(), new AccountsPageViewModel(), new ServersPageViewModel(), NullLogger<NavigationService>.Instance);
        navigation.Navigate(PageKey.Downloads);
        Assert.IsType<DownloadsPageViewModel>(navigation.CurrentPage);
    }
}
