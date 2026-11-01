using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;

namespace AstraLauncher.App.Services;

/// <summary>Modal beta notice shown on startup; the user may suppress it for later launches.</summary>
public partial class BetaNoticeWindow : Window
{
    public BetaNoticeWindow(string version)
    {
        InitializeComponent();
        VersionText.Text = $"当前版本：{version}";
    }

    /// <summary>Gets a value indicating whether the user asked to hide the notice in future launches.</summary>
    public bool DontShowAgain => DontShowAgainBox.IsChecked == true;

    private void OnConfirm(object sender, RoutedEventArgs e) => Close();

    private void OnOpenReleases(object sender, RoutedEventArgs e) => new ChangelogWindow().ShowDialog();

    private void OnNavigate(object sender, RequestNavigateEventArgs e)
    {
        Open(e.Uri.AbsoluteUri);
        e.Handled = true;
    }

    private static void Open(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Opening the browser is best-effort.
        }
    }
}
