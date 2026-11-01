using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace AstraLauncher.App.Services;

/// <summary>
/// Embedded Cloudreve registration/login window. It shows a unified verification loading screen while the page
/// boots, lets the user register or sign in with the real Cloudreve UI, then detects the stored access token.
/// </summary>
public partial class CloudreveLoginWindow : Window
{
    private readonly string _startUrl;
    private readonly CancellationTokenSource _cancellation = new();
    private bool _initialized;
    private bool _completed;

    public CloudreveLoginWindow(string startUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startUrl);
        _startUrl = startUrl;
        InitializeComponent();
        Loaded += OnLoadedAsync;
        Closed += (_, _) => _cancellation.Cancel();
    }

    public string? AccessToken { get; private set; }

    public string Email { get; private set; } = string.Empty;

    private async void OnLoadedAsync(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        try
        {
            string userDataFolder = Path.Combine(Path.GetTempPath(), "AstraLauncher", "WebView2");
            Directory.CreateDirectory(userDataFolder);
            CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await Browser.EnsureCoreWebView2Async(environment);
            Browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            Browser.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            Browser.CoreWebView2.NavigationStarting += (_, _) => ShowLoading("正在连接 Astra Launcher & cloudrive 统一验证服务");
            Browser.Source = new Uri(_startUrl);
            _ = PollForLoginAsync();
        }
        catch (Exception exception)
        {
            LoadingText.Text = "无法启动嵌入式浏览器：" + exception.Message;
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
        }
        else
        {
            LoadingText.Text = "无法连接 Cloudreve：" + e.WebErrorStatus;
            LoadingOverlay.Visibility = Visibility.Visible;
        }
    }

    private void ShowLoading(string text)
    {
        LoadingText.Text = text;
        LoadingOverlay.Visibility = Visibility.Visible;
    }

    private async Task PollForLoginAsync()
    {
        const string script = """
            (function(){
              try {
                var raw = localStorage.getItem('cloudreve_session');
                if (!raw) return '';
                var state = JSON.parse(raw);
                var sessions = state.sessions || {};
                for (var key in sessions) {
                  var entry = sessions[key];
                  if (entry && entry.token && entry.token.access_token) {
                    return JSON.stringify({
                      token: entry.token.access_token,
                      email: (entry.user && entry.user.email) || ''
                    });
                  }
                }
                return '';
              } catch (e) { return ''; }
            })()
            """;
        try
        {
            while (!_cancellation.IsCancellationRequested && !_completed)
            {
                await Task.Delay(900, _cancellation.Token).ConfigureAwait(true);
                if (Browser.CoreWebView2 is null)
                {
                    continue;
                }

                string result = await Browser.CoreWebView2.ExecuteScriptAsync(script).ConfigureAwait(true);
                string? payload = JsonSerializer.Deserialize<string>(result);
                if (string.IsNullOrWhiteSpace(payload))
                {
                    continue;
                }

                using JsonDocument document = JsonDocument.Parse(payload);
                JsonElement root = document.RootElement;
                string? token = root.TryGetProperty("token", out JsonElement tokenElement) ? tokenElement.GetString() : null;
                if (string.IsNullOrWhiteSpace(token))
                {
                    continue;
                }

                AccessToken = token;
                Email = root.TryGetProperty("email", out JsonElement emailElement) ? emailElement.GetString() ?? string.Empty : string.Empty;
                _completed = true;
                Close();
                return;
            }
        }
        catch (OperationCanceledException)
        {
            // Window was closed by the user.
        }
        catch (Exception)
        {
            // The page may be navigating; keep polling until the window is closed or login completes.
        }
    }
}
