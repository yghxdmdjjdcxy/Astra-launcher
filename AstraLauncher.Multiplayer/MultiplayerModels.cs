namespace AstraLauncher.Multiplayer;

/// <summary>NoTun is user-mode (no virtual NIC, no administrator rights); FullTun creates a TUN device and needs elevation.</summary>
public enum EasyTierMode
{
    NoTun,
    FullTun,
}

public enum RoomRole
{
    Host,
    Guest,
}

public sealed record EasyTierBinaries(string CoreExecutable, string CliExecutable, string RootDirectory);

public sealed record PeerInfo(string Ipv4, string Hostname, string Cost, double? LatencyMs, string TunnelProtocol)
{
    public bool IsDirect => string.Equals(Cost, "p2p", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Everything needed to generate one EasyTier config file and start its process.</summary>
public sealed record EasyTierOptions(
    string NetworkName,
    string NetworkSecret,
    RoomRole Role,
    EasyTierMode Mode,
    string SharedNode,
    string HostVirtualIp,
    int GamePort,
    int LocalPort,
    int RpcPort,
    string InstanceName,
    bool EnableEncryption = true);

/// <summary>Payload of a shareable room invite. The secret is intentionally included so friends do not type it.</summary>
public sealed record RoomInvite(
    string NetworkName,
    string NetworkSecret,
    string HostVirtualIp,
    int GamePort,
    string SharedNode,
    string? Label = null);

public sealed record EasyTierStatus(
    bool IsRunning,
    int? ProcessId,
    string Message,
    string? EntryAddress,
    IReadOnlyList<PeerInfo> Peers)
{
    public static EasyTierStatus Idle(string message = "未连接") => new(false, null, message, null, []);
}

public sealed class EasyTierException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);
