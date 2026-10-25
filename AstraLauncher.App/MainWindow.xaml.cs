using System.Windows;
using AstraLauncher.App.ViewModels;

namespace AstraLauncher.App;

public partial class MainWindow : Window
{
    public MainWindow(ShellViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is ShellViewModel shell) await shell.InitializeAsync();
    }
}
