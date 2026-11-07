using System.Collections.ObjectModel;
using System.Windows;
using AstraLauncher.Core.Models;
using AstraLauncher.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace AstraLauncher.App.Services;

/// <summary>Feedback form: writes feedback.md, optional lossless screenshots, and optional logs.</summary>
public partial class FeedbackWindow : Window
{
    private readonly IFeedbackService _feedback;
    private readonly ISettingsService _settings;
    private readonly ILogger<FeedbackWindow> _logger;
    private readonly ObservableCollection<string> _screenshots = [];

    public FeedbackWindow(IFeedbackService feedback, ISettingsService settings, ILogger<FeedbackWindow>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(feedback);
        ArgumentNullException.ThrowIfNull(settings);
        _feedback = feedback;
        _settings = settings;
        _logger = logger ?? NullLogger<FeedbackWindow>.Instance;
        InitializeComponent();
        ScreenshotList.ItemsSource = _screenshots;
        _ = LoadEndpointAsync();
        RefreshQueueText();
    }

    private async Task LoadEndpointAsync()
    {
        try
        {
            LauncherSettings settings = await _settings.GetAsync();
            EndpointBox.Text = string.IsNullOrWhiteSpace(settings.FeedbackEndpoint)
                ? LauncherSettings.DefaultFeedbackEndpoint
                : settings.FeedbackEndpoint;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "读取反馈服务器地址失败。");
            EndpointBox.Text = LauncherSettings.DefaultFeedbackEndpoint;
        }
    }

    private void OnAddScreenshot(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择反馈截图",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.webp|所有文件|*.*",
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        foreach (string file in dialog.FileNames)
        {
            if (!_screenshots.Contains(file))
            {
                _screenshots.Add(file);
            }
        }
    }

    private void OnRemoveScreenshot(object sender, RoutedEventArgs e)
    {
        if (ScreenshotList.SelectedItem is string selected)
        {
            _screenshots.Remove(selected);
        }
    }

    private async void OnSubmit(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(DescriptionBox.Text))
        {
            StatusText.Text = "请先填写详细描述。";
            return;
        }

        SubmitButton.IsEnabled = false;
        StatusText.Text = "正在构建并提交反馈…";
        try
        {
            LauncherSettings settings = await _settings.GetAsync();
            settings.FeedbackEndpoint = string.IsNullOrWhiteSpace(EndpointBox.Text)
                ? LauncherSettings.DefaultFeedbackEndpoint
                : EndpointBox.Text.Trim();
            await _settings.SaveAsync(settings);

            var draft = new FeedbackDraft(
                SummaryBox.Text.Trim(),
                DescriptionBox.Text.Trim(),
                ContactBox.Text.Trim(),
                IncludeLogsBox.IsChecked == true,
                _screenshots.ToList());
            FeedbackSubmitResult result = await _feedback.SubmitAsync(draft);
            StatusText.Text = result.Message;
            RefreshQueueText();
            if (result.State == FeedbackSubmitState.Submitted)
            {
                DescriptionBox.Clear();
                SummaryBox.Clear();
                _screenshots.Clear();
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "提交反馈失败。");
            StatusText.Text = "提交反馈失败：" + exception.Message;
        }
        finally
        {
            SubmitButton.IsEnabled = true;
        }
    }

    private void RefreshQueueText()
    {
        int pending = _feedback.PendingCount;
        QueueText.Text = pending > 0 ? $"待提交队列：{pending} 条" : "待提交队列：0 条";
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
