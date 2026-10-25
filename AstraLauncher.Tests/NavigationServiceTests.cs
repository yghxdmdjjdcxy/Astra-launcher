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
        var navigation = new NavigationService(new WelcomePageViewModel(), new InstancesPageViewModel(new GameInstanceRepository(new LauncherPaths(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()))), new JavaRuntimeDetector(), NullLogger<InstancesPageViewModel>.Instance, new NoopDialogService()), new DownloadsPageViewModel(), new AccountsPageViewModel(), new ServersPageViewModel(), NullLogger<NavigationService>.Instance);
        navigation.Navigate(PageKey.Downloads);
        Assert.IsType<DownloadsPageViewModel>(navigation.CurrentPage);
    }

    private sealed class NoopDialogService : IDialogService
    {
        public bool Confirm(string message, string title = "Astra Launcher") => true;
        public void ShowError(string message, string title = "Astra Launcher") { }
    }
}
