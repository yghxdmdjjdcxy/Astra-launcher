using System.Diagnostics;
using System.Windows;

namespace AstraLauncher.App.Services;

/// <summary>In-launcher changelog for the public test builds.</summary>
public partial class ChangelogWindow : Window
{
    public ChangelogWindow(string? version = null)
    {
        InitializeComponent();
        if (!string.IsNullOrWhiteSpace(version))
        {
            VersionText.Text = "当前版本：" + version;
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnOpenReleases(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://github.com/yghxdmdjjdcxy/Astra-launcher/releases")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Best effort.
        }
    }
}
