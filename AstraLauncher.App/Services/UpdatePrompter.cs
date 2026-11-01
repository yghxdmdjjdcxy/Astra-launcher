using System.IO;
using System.Net.Http;
using System.Windows;
using AstraLauncher.Core.Models;
using AstraLauncher.Core.Services;
using Microsoft.Extensions.Logging;

namespace AstraLauncher.App.Services;

/// <summary>Coordinates update checks, user prompts, progress display and restart.</summary>
public interface IUpdatePrompter
{
    /// <summary>Gets the running launcher version.</summary>
    string CurrentVersion { get; }

    /// <summary>Checks for updates; when <paramref name="silent"/> is true, respects the auto-check setting and stays quiet.</summary>
    Task CheckAsync(bool silent, CancellationToken cancellationToken = default);
}

/// <summary>Default update UI flow built on <see cref="IUpdateService"/> and <see cref="IDialogService"/>.</summary>
public sealed class UpdatePrompter : IUpdatePrompter
{
    private readonly IUpdateService _updates;
    private readonly ISettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly ILogger<UpdatePrompter> _logger;

    public UpdatePrompter(
        IUpdateService updates,
        ISettingsService settings,
        IDialogService dialogs,
        ILogger<UpdatePrompter> logger)
    {
        ArgumentNullException.ThrowIfNull(updates);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(logger);
        _updates = updates;
        _settings = settings;
        _dialogs = dialogs;
        _logger = logger;
    }

    /// <inheritdoc />
    public string CurrentVersion => _updates.CurrentVersion;

    /// <inheritdoc />
    public async Task CheckAsync(bool silent, CancellationToken cancellationToken = default)
    {
        try
        {
            if (silent)
            {
                LauncherSettings settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(true);
                if (!settings.AutoCheckUpdates)
                {
                    return;
                }
            }

            LauncherUpdate? update = await _updates.CheckAsync(cancellationToken).ConfigureAwait(true);
            if (update is null)
            {
                if (!silent)
                {
                    _dialogs.ShowInfo($"当前已是最新版本（{CurrentVersion}）。", "检查更新");
                }

                return;
            }

            bool accepted = _dialogs.Confirm(
                $"发现新版本 {update.Tag}（当前 {CurrentVersion}）。\n\n"
                + "是否立即下载并更新？\n"
                + "更新完成后启动器会自动重启，正在运行的游戏不会受影响。",
                "发现新版本");
            if (!accepted)
            {
                return;
            }

            UpdateProgressWindow? progressWindow = null;
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                progressWindow = new UpdateProgressWindow(update.Tag)
                {
                    Owner = Application.Current.MainWindow,
                };
                progressWindow.Show();
            }).Task.ConfigureAwait(true);

            try
            {
                var progress = new Progress<double>(percent => progressWindow?.Report(percent, $"正在下载更新… {percent:0}%"));
                string downloaded = await _updates.DownloadAsync(update, progress, cancellationToken).ConfigureAwait(true);
                progressWindow?.Report(100, "下载完成，正在应用更新并重启…");
                await Task.Delay(600).ConfigureAwait(true);
                _updates.ApplyAndRestart(downloaded);
            }
            finally
            {
                progressWindow?.Close();
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
        {
            _logger.LogWarning(exception, "检查或安装更新失败。");
            if (!silent)
            {
                _dialogs.ShowError("更新失败：" + exception.Message, "检查更新");
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "更新流程出现意外错误。");
            if (!silent)
            {
                _dialogs.ShowError("更新失败：" + exception.Message, "检查更新");
            }
        }
    }
}
