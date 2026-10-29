using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using AstraLauncher.App.Commands;
using AstraLauncher.Core.Models;
using AstraLauncher.Core.Services;
using Microsoft.Extensions.Logging;

namespace AstraLauncher.App.ViewModels;

/// <summary>
/// Stores a small multiplayer server list and offers a one-click "launch the selected instance and join this server"
/// action. The actual launch is performed by <see cref="ShellViewModel"/> because it owns the instance selection.
/// </summary>
public sealed class ServersPageViewModel : PageViewModel
{
    private readonly IServerRepository _servers;
    private readonly ILogger<ServersPageViewModel> _logger;
    private SavedServer? _selectedServer;
    private Guid? _lastSelectedServerId;
    private string _name = string.Empty;
    private string _address = string.Empty;
    private string _port = string.Empty;
    private string _statusMessage = "正在加载服务器列表…";
    private bool _isBusy;

    public ServersPageViewModel(IServerRepository servers, ILogger<ServersPageViewModel> logger) : base("服务器")
    {
        ArgumentNullException.ThrowIfNull(servers);
        ArgumentNullException.ThrowIfNull(logger);
        _servers = servers;
        _logger = logger;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        NewCommand = new RelayCommand(_ => StartNew());
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        DeleteCommand = new AsyncRelayCommand(DeleteAsync);
        JoinCommand = new AsyncRelayCommand(JoinAsync);
    }

    /// <summary>Raised when the user asks to launch the selected instance and join the selected server.</summary>
    public event Action<SavedServer>? JoinRequested;

    public ObservableCollection<SavedServer> Servers { get; } = [];

    public ICommand RefreshCommand { get; }
    public ICommand NewCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand JoinCommand { get; }

    public SavedServer? SelectedServer
    {
        get => _selectedServer;
        set
        {
            if (!SetProperty(ref _selectedServer, value))
            {
                return;
            }

            if (value is null)
            {
                return;
            }

            _lastSelectedServerId = value.Id;
            Name = value.Name;
            Address = value.Address;
            Port = value.Port?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        }
    }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string Address
    {
        get => _address;
        set => SetProperty(ref _address, value);
    }

    public string Port
    {
        get => _port;
        set => SetProperty(ref _port, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public async Task InitializeAsync()
    {
        await RefreshAsync().ConfigureAwait(false);
    }

    /// <summary>Lets the shell report why a join request could not be started.</summary>
    public void ReportStatus(string message) => StatusMessage = message;

    private async Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            IReadOnlyList<SavedServer> values = await _servers.ListAsync().ConfigureAwait(false);
            Guid? selectedId = SelectedServer?.Id;

            // Merge instead of Clear(): clearing the bound list would push a null selection back through the
            // TwoWay binding and clear the user's current server right before a join request.
            for (int index = Servers.Count - 1; index >= 0; index--)
            {
                if (!values.Any(value => value.Id == Servers[index].Id))
                {
                    Servers.RemoveAt(index);
                }
            }

            foreach (SavedServer server in values)
            {
                int index = -1;
                for (int candidate = 0; candidate < Servers.Count; candidate++)
                {
                    if (Servers[candidate].Id == server.Id)
                    {
                        index = candidate;
                        break;
                    }
                }

                if (index >= 0)
                {
                    if (Servers[index] != server)
                    {
                        Servers[index] = server;
                    }
                }
                else
                {
                    Servers.Add(server);
                }
            }

            SelectedServer = selectedId is Guid id
                ? Servers.FirstOrDefault(server => server.Id == id)
                : Servers.FirstOrDefault();
            StatusMessage = Servers.Count == 0
                ? "还没有保存的服务器。填写名称和地址后点击「保存服务器」。"
                : $"已保存 {Servers.Count} 个服务器。选中一个后可在「游戏实例」页启动时直接加入。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(exception, "加载服务器列表失败。");
            StatusMessage = "加载服务器列表失败：" + exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void StartNew()
    {
        SelectedServer = null;
        Name = string.Empty;
        Address = string.Empty;
        Port = string.Empty;
        StatusMessage = "填写服务器名称和地址（可把端口写在地址里），然后点击「保存服务器」。";
    }

    private async Task SaveAsync()
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            (string name, string address, int? port) = ValidateEditor();
            IsBusy = true;
            var server = new SavedServer(SelectedServer?.Id ?? Guid.NewGuid(), name, address, port);
            await _servers.SaveAsync(server).ConfigureAwait(false);
            await RefreshAsync().ConfigureAwait(false);
            SelectedServer = Servers.FirstOrDefault(value => value.Id == server.Id);
            StatusMessage = $"已保存服务器：{server.Name}（{server.DisplayAddress}）。";
        }
        catch (InvalidOperationException exception)
        {
            StatusMessage = exception.Message;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(exception, "保存服务器失败。");
            StatusMessage = "保存服务器失败：" + exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DeleteAsync()
    {
        SavedServer? selected = SelectedServer;
        if (selected is null)
        {
            StatusMessage = "请先在列表中选择要删除的服务器。";
            return;
        }

        IsBusy = true;
        try
        {
            await _servers.DeleteAsync(selected.Id).ConfigureAwait(false);
            await RefreshAsync().ConfigureAwait(false);
            StartNew();
            StatusMessage = $"已删除服务器：{selected.Name}。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(exception, "删除服务器失败。");
            StatusMessage = "删除服务器失败：" + exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task JoinAsync()
    {
        if (IsBusy)
        {
            return;
        }

        // Navigating away from the servers page can make the ListBox push a null SelectedItem through the TwoWay
        // binding; fall back to the last selected id, then to a fresh read of the saved list.
        SavedServer? selected = SelectedServer
            ?? (_lastSelectedServerId is Guid id ? Servers.FirstOrDefault(server => server.Id == id) : null)
            ?? Servers.FirstOrDefault();
        if (selected is null)
        {
            try
            {
                IsBusy = true;
                IReadOnlyList<SavedServer> values = await _servers.ListAsync().ConfigureAwait(false);
                selected = values.FirstOrDefault();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(exception, "读取服务器列表失败，无法加入服务器。");
            }
            finally
            {
                IsBusy = false;
            }
        }

        if (selected is null)
        {
            StatusMessage = "请先选择一个服务器（或先保存一个服务器地址）。";
            return;
        }

        _logger.LogInformation("Join requested for server {ServerAddress}.", selected.DisplayAddress);
        SelectedServer = selected;
        JoinRequested?.Invoke(selected);
    }

    private (string Name, string Address, int? Port) ValidateEditor()
    {
        string name = Name.Trim();
        if (name.Length == 0)
        {
            throw new InvalidOperationException("请填写服务器名称。");
        }

        string rawAddress = Address.Trim();
        if (rawAddress.Length == 0)
        {
            throw new InvalidOperationException("请填写服务器地址。");
        }

        // Accept a pasted URL or "host:port" in the address field; the game only needs the bare host plus --port.
        int schemeIndex = rawAddress.IndexOf("://", StringComparison.Ordinal);
        if (schemeIndex >= 0)
        {
            rawAddress = rawAddress[(schemeIndex + 3)..];
        }

        rawAddress = rawAddress.Trim().TrimEnd('/');
        string host = rawAddress;
        int? parsedPort = null;
        if (rawAddress.StartsWith('['))
        {
            int closing = rawAddress.IndexOf(']');
            if (closing <= 1)
            {
                throw new InvalidOperationException("IPv6 地址格式无效，例如：[::1]:25565。");
            }

            host = rawAddress[1..closing];
            if (rawAddress.Length > closing + 2 && rawAddress[closing + 1] == ':')
            {
                parsedPort = ParsePort(rawAddress[(closing + 2)..]);
            }
        }
        else
        {
            int colon = rawAddress.LastIndexOf(':');
            if (colon > 0 && rawAddress.IndexOf(':') == colon)
            {
                string candidate = rawAddress[(colon + 1)..];
                if (candidate.Length > 0 && candidate.All(char.IsAsciiDigit))
                {
                    host = rawAddress[..colon];
                    parsedPort = ParsePort(candidate);
                }
            }
        }

        if (Port.Trim().Length > 0)
        {
            parsedPort = ParsePort(Port.Trim());
        }

        host = host.Trim();
        if (host.Length == 0)
        {
            throw new InvalidOperationException("服务器地址不能为空。");
        }

        if (host.Length > 253 || host.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            throw new InvalidOperationException("服务器地址不能包含空白字符，且长度不能超过 253 个字符。");
        }

        return (name, host, parsedPort);
    }

    private static int ParsePort(string text)
    {
        if (!int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int port) || port is < 1 or > 65535)
        {
            throw new InvalidOperationException("端口必须是 1–65535 之间的整数（留空使用默认端口）。");
        }

        return port;
    }
}
