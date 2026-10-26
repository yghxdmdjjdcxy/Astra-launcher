using AstraLauncher.App.Services;
using AstraLauncher.App.ViewModels;
using AstraLauncher.Core.Models;
using AstraLauncher.Core.Services;
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
        NavigationService navigation = CreateNavigation();
        navigation.Navigate(PageKey.Downloads);
        Assert.IsType<DownloadsPageViewModel>(navigation.CurrentPage);
    }

    [Fact]
    public void Navigate_ToSettings_ChangesCurrentPage()
    {
        NavigationService navigation = CreateNavigation();
        navigation.Navigate(PageKey.Settings);
        Assert.IsType<SettingsPageViewModel>(navigation.CurrentPage);
    }

    private static NavigationService CreateNavigation()
    {
        var dialogs = new NoopDialogService();
        return new NavigationService(
            new WelcomePageViewModel(),
            new InstancesPageViewModel(new GameInstanceRepository(new LauncherPaths(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()))), new FakeJavaRuntimeDetector(), NullLogger<InstancesPageViewModel>.Instance, dialogs),
            new DownloadsPageViewModel(new EmptyManifestService(), new NoopInstallService(), NullLogger<DownloadsPageViewModel>.Instance, dialogs),
            new AccountsPageViewModel(),
            new ServersPageViewModel(),
            new SettingsPageViewModel(new FakeSettingsService(), new FakeJavaRuntimeDetector(), NullLogger<SettingsPageViewModel>.Instance, dialogs),
            NullLogger<NavigationService>.Instance);
    }

    private sealed class NoopDialogService : IDialogService
    {
        public bool Confirm(string message, string title = "Astra Launcher") => true;
        public void ShowError(string message, string title = "Astra Launcher") { }
    }

    private sealed class FakeSettingsService : ISettingsService
    {
        public event EventHandler<LauncherSettings>? Changed
        {
            add { }
            remove { }
        }

        public Task<LauncherSettings> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(new LauncherSettings());

        public Task SaveAsync(LauncherSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeJavaRuntimeDetector : IJavaRuntimeDetector
    {
        public Task<IReadOnlyList<JavaRuntimeInfo>> DetectAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<JavaRuntimeInfo>>([]);

        public Task<JavaRuntimeInfo?> InspectAsync(string executablePath, CancellationToken cancellationToken = default) =>
            Task.FromResult<JavaRuntimeInfo?>(null);
    }

    private sealed class EmptyManifestService : IVersionManifestService
    {
        public Task<VersionManifest> GetManifestAsync(bool forceRefresh = false, CancellationToken cancellationToken = default) =>
            Task.FromResult(new VersionManifest(new VersionManifestLatest("1.21.1", "24w44a"), []));

        public Task<IReadOnlyList<VersionSummary>> GetVersionsAsync(bool includeSnapshots, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<VersionSummary>>([]);

        public Task<VersionSummary> GetVersionSummaryAsync(string versionId, CancellationToken cancellationToken = default) =>
            throw new KeyNotFoundException(versionId);
    }

    private sealed class NoopInstallService : IVersionInstallService
    {
        public Task<VersionInstallPlan> CreatePlanAsync(string versionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new VersionInstallPlan(versionId, [], 0));

        public Task InstallAsync(string versionId, IProgress<VersionInstallProgress>? progress = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> IsVersionInstalledAsync(string versionId, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }
}
