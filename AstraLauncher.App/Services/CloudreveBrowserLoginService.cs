using System.Windows;
using Microsoft.Extensions.Logging;

namespace AstraLauncher.App.Services;

/// <summary>Opens the embedded Cloudreve login/registration window and provisions a WebDAV account afterwards.</summary>
public interface ICloudreveBrowserLoginService
{
    /// <summary>Returns the WebDAV account after the user logs in/registers, or <see langword="null"/> when cancelled.</summary>
    Task<CloudreveWebDavAccount?> LoginAsync(string serverUrl, CancellationToken cancellationToken = default);
}

/// <summary>Adapter between the WebView2 login window and <see cref="ICloudreveAuthService"/>.</summary>
public sealed class CloudreveBrowserLoginService : ICloudreveBrowserLoginService
{
    private readonly ICloudreveAuthService _auth;
    private readonly ILogger<CloudreveBrowserLoginService> _logger;

    public CloudreveBrowserLoginService(ICloudreveAuthService auth, ILogger<CloudreveBrowserLoginService> logger)
    {
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(logger);
        _auth = auth;
        _logger = logger;
    }

    public async Task<CloudreveWebDavAccount?> LoginAsync(string serverUrl, CancellationToken cancellationToken = default)
    {
        string? token = null;
        string email = string.Empty;
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var window = new CloudreveLoginWindow(serverUrl.Trim())
            {
                Owner = Application.Current.MainWindow,
            };
            window.ShowDialog();
            token = window.AccessToken;
            email = window.Email;
        }).Task.ConfigureAwait(true);

        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(email))
        {
            throw new InvalidOperationException("无法从 Cloudreve 登录会话中读取邮箱，请确认账号状态。");
        }

        _logger.LogInformation("Cloudreve browser login completed for {Email}.", email);
        return await _auth.ProvisionWebDavAsync(serverUrl, token, email, cancellationToken).ConfigureAwait(true);
    }
}
