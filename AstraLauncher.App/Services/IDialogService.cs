namespace AstraLauncher.App.Services;

public interface IDialogService
{
    bool Confirm(string message, string title = "Astra Launcher");
    void ShowError(string message, string title = "Astra Launcher");
}
