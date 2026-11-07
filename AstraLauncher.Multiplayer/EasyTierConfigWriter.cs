using System.Globalization;
using System.Net;
using System.Text;

namespace AstraLauncher.Multiplayer;

/// <summary>
/// Builds the EasyTier TOML config. Secrets always go into this file and never onto the command line,
/// because command lines are visible in Task Manager and in process-creation logs.
/// </summary>
public static class EasyTierConfigWriter
{
    public const string ConfigFileNameHost = "astra-host.toml";
    public const string ConfigFileNameGuest = "astra-guest.toml";

    public static string Build(EasyTierOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Validate(options);

        var text = new StringBuilder();
        text.Append("instance_name = ").Append(Quote(options.InstanceName)).Append('\n');
        text.Append("hostname = ").Append(Quote(options.InstanceName)).Append('\n');

        if (options.Role == RoomRole.Host)
        {
            text.Append("ipv4 = ").Append(Quote(options.HostVirtualIp + "/24")).Append('\n');
            text.Append("dhcp = false\n");
            text.Append("listeners = [ \"tcp://0.0.0.0:11010\", \"udp://0.0.0.0:11010\" ]\n");
            text.Append("tcp_whitelist = [ ").Append(Quote(options.GamePort.ToString(CultureInfo.InvariantCulture))).Append(" ]\n");
            text.Append("udp_whitelist = []\n");
        }
        else
        {
            text.Append("dhcp = true\n");
            text.Append("listeners = []\n");
        }

        text.Append("rpc_portal = ").Append(Quote("127.0.0.1:" + options.RpcPort.ToString(CultureInfo.InvariantCulture))).Append('\n');
        text.Append('\n');

        text.Append("[network_identity]\n");
        text.Append("network_name = ").Append(Quote(options.NetworkName)).Append('\n');
        text.Append("network_secret = ").Append(Quote(options.NetworkSecret)).Append('\n');
        text.Append('\n');

        text.Append("[[peer]]\n");
        text.Append("uri = ").Append(Quote(options.SharedNode)).Append('\n');
        text.Append('\n');

        // Port forwarding is deliberately NOT written into TOML: the array key differs between EasyTier
        // versions ([[port_forward]] vs [[port_forwards]]), while the --port-forward CLI flag is stable.
        // EasyTierService adds it to the command line for no-TUN guests.

        text.Append("[flags]\n");
        text.Append("no_tun = ").Append(options.Mode == EasyTierMode.NoTun ? "true" : "false").Append('\n');
        text.Append("enable_encryption = ").Append(options.EnableEncryption ? "true" : "false").Append('\n');
        text.Append("mtu = 1380\n");
        return text.ToString();
    }

    public static async Task<string> WriteAsync(
        EasyTierOptions options,
        string directory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, options.Role == RoomRole.Host ? ConfigFileNameHost : ConfigFileNameGuest);
        await File.WriteAllTextAsync(path, Build(options), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        return path;
    }

    /// <summary>TOML basic-string escaping: backslash, quote, control characters.</summary>
    internal static string Quote(string value) =>
        "\"" + value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal) + "\"";

    private static void Validate(EasyTierOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.NetworkName) || options.NetworkName.Length > 64)
        {
            throw new EasyTierException("房间名称不能为空且不能超过 64 字符。");
        }

        if (string.IsNullOrWhiteSpace(options.NetworkSecret))
        {
            throw new EasyTierException("房间密钥不能为空。");
        }

        if (string.IsNullOrWhiteSpace(options.InstanceName))
        {
            throw new EasyTierException("节点名称不能为空。");
        }

        if (!RoomInviteCodec.IsSupportedNode(options.SharedNode))
        {
            throw new EasyTierException("共享节点地址必须以 tcp://、udp://、ws:// 或 wss:// 开头。");
        }

        if (!IPAddress.TryParse(options.HostVirtualIp, out IPAddress? address)
            || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            throw new EasyTierException("房主虚拟地址必须是合法的 IPv4 地址。");
        }

        if (!IsPort(options.GamePort) || !IsPort(options.LocalPort) || !IsPort(options.RpcPort))
        {
            throw new EasyTierException("端口必须在 1-65535 之间。");
        }

        if (options.Role == RoomRole.Guest
            && options.Mode == EasyTierMode.NoTun
            && options.RpcPort == options.LocalPort)
        {
            throw new EasyTierException("本地转发端口与管理端口不能相同。");
        }
    }

    private static bool IsPort(int port) => port is >= 1 and <= 65535;
}
