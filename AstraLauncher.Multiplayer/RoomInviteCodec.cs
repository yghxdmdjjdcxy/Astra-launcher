using System.Net;
using System.Text;
using System.Text.Json;

namespace AstraLauncher.Multiplayer;

/// <summary>Encodes a room as a single copy-pasteable code so friends never hand-type network secrets.</summary>
public static class RoomInviteCodec
{
    private const string Prefix = "ASTRA1.";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static string Encode(RoomInvite invite)
    {
        ArgumentNullException.ThrowIfNull(invite);
        Validate(invite);
        string json = JsonSerializer.Serialize(invite, JsonOptions);
        string payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return Prefix + payload;
    }

    public static bool TryDecode(string? code, out RoomInvite? invite)
    {
        invite = null;
        if (string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        string trimmed = code.Trim();
        if (!trimmed.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            string payload = trimmed[Prefix.Length..].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
            string json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            RoomInvite? decoded = JsonSerializer.Deserialize<RoomInvite>(json, JsonOptions);
            if (decoded is null)
            {
                return false;
            }

            Validate(decoded);
            invite = decoded;
            return true;
        }
        catch (Exception exception) when (exception is FormatException or JsonException or EasyTierException)
        {
            return false;
        }
    }

    private static void Validate(RoomInvite invite)
    {
        if (string.IsNullOrWhiteSpace(invite.NetworkName) || invite.NetworkName.Length > 64)
        {
            throw new EasyTierException("房间名称不能为空且不能超过 64 字符。");
        }

        if (string.IsNullOrWhiteSpace(invite.NetworkSecret))
        {
            throw new EasyTierException("房间密钥不能为空。");
        }

        if (!IPAddress.TryParse(invite.HostVirtualIp, out IPAddress? address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            throw new EasyTierException("房主虚拟地址必须是合法的 IPv4 地址。");
        }

        if (invite.GamePort is < 1 or > 65535)
        {
            throw new EasyTierException("游戏端口必须在 1-65535 之间。");
        }

        if (!IsSupportedNode(invite.SharedNode))
        {
            throw new EasyTierException("共享节点地址必须以 tcp://、udp://、ws:// 或 wss:// 开头。");
        }
    }

    internal static bool IsSupportedNode(string? node)
    {
        if (string.IsNullOrWhiteSpace(node))
        {
            return false;
        }

        return node.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase)
            || node.StartsWith("udp://", StringComparison.OrdinalIgnoreCase)
            || node.StartsWith("ws://", StringComparison.OrdinalIgnoreCase)
            || node.StartsWith("wss://", StringComparison.OrdinalIgnoreCase);
    }
}
