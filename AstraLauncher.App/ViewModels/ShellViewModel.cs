using System.Windows.Input;
using AstraLauncher.App.Commands;
using AstraLauncher.App.Services;

namespace AstraLauncher.App.ViewModels;

public sealed class ShellViewModel
{
    private readonly InstancesPageViewModel _instances;
    public ShellViewModel(INavigationService navigation, InstancesPageViewModel instances)
    {
        Navigation = navigation;
        _instances = instances;
        NavigateCommand = new RelayCommand(Navigate);
    }

    public INavigationService Navigation { get; }
    public ICommand NavigateCommand { get; }
    public Task InitializeAsync() => _instances.InitializeAsync();

    private void Navigate(object? parameter)
    {
        if (parameter is not string pageName || !Enum.TryParse(pageName, out PageKey page))
            throw new ArgumentException("The navigation command requires a known page name.", nameof(parameter));
        Navigation.Navigate(page);
    }
}
