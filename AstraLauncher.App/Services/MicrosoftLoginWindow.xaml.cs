using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AstraLauncher.App.Services;

/// <summary>
/// Embedded WebView2 window that shows the Microsoft sign-in page. The launcher polls the device-code token
/// endpoint in the background and closes the window automatically as soon as sign-in completes.
/// </summary>
public partial class MicrosoftLoginWindow : Window
{
    private readonly string _startUrl;
    private bool _disposed;

    public MicrosoftLoginWindow(string startUrl, string? userCode = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startUrl);
        _startUrl = startUrl;
        InitializeComponent();
        if (!string.IsNullOrWhiteSpace(userCode))
        {
            HintText.Text = "使用你的 Microsoft / Xbox 账户登录。若页面要求输入代码，请输入：" + userCode
                + "（登录成功后窗口会自动关闭）。";
        }

        Loaded += OnLoadedAsync;
        Closed += OnClosed;
    }

    private async void OnLoadedAsync(object sender, RoutedEventArgs e)
    {
        try
        {
            string userDataFolder = Path.Combine(Path.GetTempPath(), "AstraLauncher", "WebView2");
            Directory.CreateDirectory(userDataFolder);
            CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await Browser.EnsureCoreWebView2Async(environment);
            Browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            Browser.Source = new Uri(_startUrl);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                "无法启动嵌入式浏览器（WebView2）：" + exception.Message
                + "\n\n请安装 Microsoft Edge WebView2 Runtime 后重试。",
                "微软账户登录",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Close();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            Browser.Dispose();
        }
        catch (Exception)
        {
            // Best-effort disposal only.
        }
    }
}
