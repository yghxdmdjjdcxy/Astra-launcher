namespace AstraLauncher.App.ViewModels;

public abstract class PageViewModel(string title) : ObservableObject { public string Title { get; } = title; }
public sealed class WelcomePageViewModel() : PageViewModel("首页");
