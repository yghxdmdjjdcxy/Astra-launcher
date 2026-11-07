using System.Globalization;
using AstraLauncher.Plugin.Abstractions;

namespace AstraLauncher.Multiplayer;

/// <summary>
/// Bridges the plugin launch pipeline and multiplayer: while a room is connected, starting the game
/// without an explicit server automatically joins the room's entry address. This is the shared
/// extension point that lets the multiplayer feature behave like a built-in plugin.
/// </summary>
public sealed class EasyTierLaunchHook(IEasyTierService service) : ILaunchHook
{
    public Task<PluginLaunchRequest> BeforeBuildAsync(PluginLaunchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!service.AutoJoinEnabled)
        {
            return Task.FromResult(request);
        }

        EasyTierStatus status = service.Status;
        if (!status.IsRunning
            || !string.IsNullOrWhiteSpace(request.ServerAddress)
            || !TrySplitAddress(status.EntryAddress, out string? host, out int port))
        {
            return Task.FromResult(request);
        }

        return Task.FromResult(request with { ServerAddress = host, ServerPort = port });
    }

    public static bool TrySplitAddress(string? address, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        int separator = address.LastIndexOf(':');
        if (separator <= 0 || separator == address.Length - 1)
        {
            return false;
        }

        host = address[..separator].Trim().Trim('[', ']');
        return int.TryParse(address[(separator + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out port)
            && port is >= 1 and <= 65535
            && host.Length > 0;
    }
}
