using System.Windows.Input;

namespace AstraLauncher.App.Commands;

public sealed class AsyncRelayCommand(Func<Task> execute) : ICommand
{
    private bool _isRunning;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !_isRunning;
    public async void Execute(object? parameter)
    {
        if (_isRunning) return;
        _isRunning = true; CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try { await execute(); }
        finally { _isRunning = false; CanExecuteChanged?.Invoke(this, EventArgs.Empty); }
    }
}
