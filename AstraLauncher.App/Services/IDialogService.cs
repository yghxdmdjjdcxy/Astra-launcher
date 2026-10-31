namespace AstraLauncher.App.Services;

public interface IDialogService
{
    bool Confirm(string message, string title = "Astra Launcher");
    void ShowError(string message, string title = "Astra Launcher");

    /// <summary>Shows an informational message. A default implementation keeps existing test fakes working.</summary>
    void ShowInfo(string message, string title = "Astra Launcher") =>
        System.Windows.MessageBox.Show(message, title, System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
}
