using AstraLauncher.App.Services;
using AstraLauncher.App.ViewModels;
using AstraLauncher.Core.Models;
using AstraLauncher.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO;

namespace AstraLauncher.Tests;

public sealed class DownloadsPageViewModelTests
{
    [Fact]
    public async Task Initialize_LoadsReleaseVersions()
    {
        FakeManifestService manifest = new()
        {
            Versions =
            [
                Version("1.21.1", "release", new DateTimeOffset(2024, 8, 8, 0, 0, 0, TimeSpan.Zero)),
                Version("1.21", "release", new DateTimeOffset(2024, 6, 13, 0, 0, 0, TimeSpan.Zero)),
            ],
        };
        DownloadsPageViewModel viewModel = CreateViewModel(manifest, new RecordingInstallService(), new RecordingDialogService());

        await viewModel.InitializeAsync();

        Assert.Equal(2, viewModel.Versions.Count);
        Assert.Equal("1.21.1", viewModel.SelectedVersion?.Id);
        Assert.Contains("已加载 2 个正式版本", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task Install_ReportsProgressAndCompletes()
    {
        RecordingInstallService install = new() { ReportProgress = true };
        DownloadsPageViewModel viewModel = CreateViewModel(new FakeManifestService { Versions = [Version("1.21.1", "release", DateTimeOffset.UtcNow)] }, install, new RecordingDialogService());
        await viewModel.InitializeAsync();

        viewModel.InstallCommand.Execute(null);
        await WaitForAsync(() => install.InstallCalls == 1 && !viewModel.IsBusy, "下载完成");

        Assert.Equal("1.21.1", install.LastVersionId);
        Assert.Equal(100, viewModel.ProgressPercent, 1);
        Assert.Contains("已下载并校验完成", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task Install_WithoutSelection_ShowsValidationMessage()
    {
        DownloadsPageViewModel viewModel = CreateViewModel(new FakeManifestService { Versions = [] }, new RecordingInstallService(), new RecordingDialogService());
        await viewModel.InitializeAsync();

        viewModel.InstallCommand.Execute(null);
        await WaitForAsync(() => viewModel.StatusMessage.Contains("请先在列表中选择", StringComparison.Ordinal), "校验提示");

        Assert.Contains("请先在列表中选择", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancel_RequestsCancellation()
    {
        CancelableInstallService install = new();
        DownloadsPageViewModel viewModel = CreateViewModel(new FakeManifestService { Versions = [Version("1.21.1", "release", DateTimeOffset.UtcNow)] }, install, new RecordingDialogService());
        await viewModel.InitializeAsync();

        viewModel.InstallCommand.Execute(null);
        await WaitForAsync(() => viewModel.IsBusy, "下载开始");
        viewModel.CancelCommand.Execute(null);
        await WaitForAsync(() => viewModel.StatusMessage.Contains("已取消下载", StringComparison.Ordinal), "取消完成");

        Assert.True(install.WasCanceled);
        Assert.False(viewModel.IsBusy);
    }

    private static DownloadsPageViewModel CreateViewModel(IVersionManifestService manifest, IVersionInstallService install, IDialogService dialogs) =>
        new(manifest, install, NullLogger<DownloadsPageViewModel>.Instance, dialogs);

    private static VersionSummary Version(string id, string type, DateTimeOffset releaseTime) =>
        new(id, type, $"https://example.test/{id}.json", releaseTime, releaseTime, null);

    private static async Task WaitForAsync(Func<bool> condition, string description)
    {
        for (int attempt = 0; attempt < 600; attempt++)
        {
            if (condition()) return;
            await Task.Delay(10);
        }

        throw new TimeoutException($"Timed out waiting for: {description}");
    }

    private sealed class FakeManifestService : IVersionManifestService
    {
        public IReadOnlyList<VersionSummary> Versions { get; set; } = [];

        public Task<VersionManifest> GetManifestAsync(bool forceRefresh = false, CancellationToken cancellationToken = default) =>
            Task.FromResult(new VersionManifest(new VersionManifestLatest(Versions.FirstOrDefault()?.Id ?? "1.21.1", "24w44a"), Versions));

        public Task<IReadOnlyList<VersionSummary>> GetVersionsAsync(bool includeSnapshots, CancellationToken cancellationToken = default) =>
            Task.FromResult(Versions);

        public Task<VersionSummary> GetVersionSummaryAsync(string versionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Versions.Single(version => string.Equals(version.Id, versionId, StringComparison.Ordinal)));
    }

    private sealed class RecordingInstallService : IVersionInstallService
    {
        public int InstallCalls { get; private set; }

        public string? LastVersionId { get; private set; }

        public bool ReportProgress { get; set; }

        public Task<VersionInstallPlan> CreatePlanAsync(string versionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new VersionInstallPlan(versionId, [], 0));

        public Task InstallAsync(string versionId, IProgress<VersionInstallProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            InstallCalls++;
            LastVersionId = versionId;
            if (ReportProgress)
            {
                progress?.Report(new VersionInstallProgress(3, 1, 300, 100, "下载游戏文件", "a.jar"));
                progress?.Report(new VersionInstallProgress(3, 3, 300, 300, "下载游戏文件", null));
            }

            return Task.CompletedTask;
        }

        public Task<bool> IsVersionInstalledAsync(string versionId, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class CancelableInstallService : IVersionInstallService
    {
        public bool WasCanceled { get; private set; }

        public Task<VersionInstallPlan> CreatePlanAsync(string versionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new VersionInstallPlan(versionId, [], 0));

        public async Task InstallAsync(string versionId, IProgress<VersionInstallProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                WasCanceled = true;
                throw;
            }
        }

        public Task<bool> IsVersionInstalledAsync(string versionId, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class RecordingDialogService : IDialogService
    {
        public int ErrorCount { get; private set; }

        public bool Confirm(string message, string title = "Astra Launcher") => true;

        public void ShowError(string message, string title = "Astra Launcher") => ErrorCount++;
    }
}
