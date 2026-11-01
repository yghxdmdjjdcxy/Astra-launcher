using System.Windows;

namespace AstraLauncher.App.Services;

/// <summary>Small modal window that shows update download progress.</summary>
public partial class UpdateProgressWindow : Window
{
    public UpdateProgressWindow(string tag)
    {
        InitializeComponent();
        TitleText.Text = $"正在更新 Astra Launcher {tag}…";
    }

    /// <summary>Updates the progress UI from any thread.</summary>
    public void Report(double percent, string status)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => Report(percent, status)));
            return;
        }

        Progress.IsIndeterminate = false;
        Progress.Value = Math.Clamp(percent, 0, 100);
        StatusText.Text = status;
    }
}
