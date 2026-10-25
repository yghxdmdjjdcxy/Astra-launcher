namespace AstraLauncher.App.ViewModels;

public abstract class PageViewModel(string title) : ObservableObject { public string Title { get; } = title; }
public sealed class WelcomePageViewModel() : PageViewModel("首页");
public sealed class DownloadsPageViewModel() : PageViewModel("下载");
public sealed class AccountsPageViewModel() : PageViewModel("账户");
public sealed class ServersPageViewModel() : PageViewModel("服务器");
