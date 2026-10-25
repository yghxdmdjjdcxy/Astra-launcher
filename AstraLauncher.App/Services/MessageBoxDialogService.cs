using System.Windows;

namespace AstraLauncher.App.Services;

public sealed class MessageBoxDialogService : IDialogService
{
    public bool Confirm(string message, string title = "Astra Launcher") =>
        MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    public void ShowError(string message, string title = "Astra Launcher") =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
}
