using System.Net.Http;
using System.Windows;
using AstraLauncher.Authentication;
using Microsoft.Extensions.Logging;

namespace AstraLauncher.App.Services;

/// <summary>
/// Xbox/Microsoft login popup. It uses Microsoft's device-code flow under the hood (a well-known public client used by
/// open-source launchers), but instead of showing a code it opens the verification page inside an embedded WebView2
/// window. The launcher polls the token endpoint and closes the window automatically once the user finishes signing in.
/// This keeps the refresh-token flow working without requiring an Azure app registration or redirect URI.
/// </summary>
public sealed class WebViewMicrosoftLoginClient : IMicrosoftInteractiveLoginClient
{
    /// <summary>Public client id used by PrismLauncher and other open-source Minecraft launchers.</summary>
    public const string PublicClientId = "c36a9fb6-4f2a-41ff-90bd-ae7cc92031eb";

    private readonly IDeviceCodeOAuthClient _deviceCode;
    private readonly ILogger<WebViewMicrosoftLoginClient> _logger;

    public WebViewMicrosoftLoginClient(
        IDeviceCodeOAuthClient deviceCode,
        HttpClient httpClient,
        ILogger<WebViewMicrosoftLoginClient> logger)
    {
        ArgumentNullException.ThrowIfNull(deviceCode);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);
        _deviceCode = deviceCode;
        _logger = logger;
    }

    /// <inheritdoc />
    public string ClientId => PublicClientId;

    /// <inheritdoc />
    public async Task<MicrosoftTokenSet> SignInAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        progress?.Report("正在连接 Microsoft 登录服务…");
        DeviceCodeInfo prompt = await _deviceCode.RequestDeviceCodeAsync(PublicClientId, cancellationToken).ConfigureAwait(false);
        string verificationUrl = string.IsNullOrWhiteSpace(prompt.VerificationUriComplete)
            ? prompt.VerificationUri
              + (prompt.VerificationUri.Contains('?') ? "&" : "?")
              + "otc=" + Uri.EscapeDataString(prompt.UserCode)
            : prompt.VerificationUriComplete;

        using var windowCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        MicrosoftLoginWindow? window = null;
        bool completed = false;
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            window = new MicrosoftLoginWindow(verificationUrl, prompt.UserCode)
            {
                Owner = Application.Current.MainWindow,
            };
            window.Closed += (_, _) =>
            {
                if (!completed)
                {
                    windowCancellation.Cancel();
                }
            };
            window.Show();
        });

        try
        {
            progress?.Report("请在登录窗口中完成 Microsoft / Xbox 账户登录…");
            MicrosoftTokenSet tokens = await _deviceCode
                .WaitForTokenAsync(PublicClientId, prompt, progress, windowCancellation.Token)
                .ConfigureAwait(false);
            completed = true;
            _logger.LogInformation("Microsoft popup login completed.");
            return tokens;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AccountAuthenticationException(
                AccountErrorCode.AuthorizationDeclined,
                "已关闭 Microsoft 登录窗口，登录已取消。");
        }
        finally
        {
            MicrosoftLoginWindow? toClose = window;
            if (toClose is not null)
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        toClose.Close();
                    }
                    catch (InvalidOperationException)
                    {
                        // The window was already closed by the user.
                    }
                });
            }
        }
    }

    /// <inheritdoc />
    public Task<MicrosoftTokenSet> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default) =>
        _deviceCode.RefreshAsync(PublicClientId, refreshToken, cancellationToken);
}
