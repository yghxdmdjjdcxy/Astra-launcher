using System.Windows;
using AstraLauncher.App.Services;
using AstraLauncher.App.ViewModels;
using AstraLauncher.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AstraLauncher.App;

public partial class MainWindow : Window
{
    public MainWindow(ShellViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += OnLoaded;
    }

    private void OnFeedbackClick(object sender, RoutedEventArgs e)
    {
        try
        {
            IServiceProvider services = ((App)Application.Current).Services;
            var window = new FeedbackWindow(
                services.GetRequiredService<IFeedbackService>(),
                services.GetRequiredService<ISettingsService>())
            {
                Owner = this,
            };
            window.ShowDialog();
        }
        catch (Exception exception)
        {
            MessageBox.Show("无法打开反馈窗口：" + exception.Message, "Astra Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is ShellViewModel shell) await shell.InitializeAsync();
    }
}
