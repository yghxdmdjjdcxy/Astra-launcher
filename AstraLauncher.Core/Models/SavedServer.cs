namespace AstraLauncher.Core.Models;

/// <summary>A saved multiplayer server shown on the Servers page.</summary>
/// <param name="Id">Stable identifier used to update or delete the entry.</param>
/// <param name="Name">Display name.</param>
/// <param name="Address">Host name or IP address without the port.</param>
/// <param name="Port">Optional port; when omitted the game uses its default (25565).</param>
public sealed record SavedServer(Guid Id, string Name, string Address, int? Port = null)
{
    /// <summary>Gets the host and port in the <c>host:port</c> form used for display.</summary>
    public string DisplayAddress => Port is int port ? $"{Address}:{port}" : Address;
}
