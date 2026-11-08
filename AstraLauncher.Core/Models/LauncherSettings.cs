namespace AstraLauncher.Core.Models;

/// <summary>Selects where version metadata and game files are downloaded from.</summary>
public enum DownloadSourceMode
{
    Official,
    Mirror,
    OfficialWithFallback,
}

/// <summary>User-configurable launcher options. Instance construction always produces usable defaults.</summary>
public sealed class LauncherSettings
{
    public const int DefaultMemoryMiB = 4096;
    public const int MinimumMemoryMiB = 512;
    public const int MaximumMemoryMiB = 65536;
    public const int DefaultMaxConcurrentDownloads = 4;
    public const int MinimumMaxConcurrentDownloads = 1;
    public const int MaximumMaxConcurrentDownloads = 32;
    public const string DefaultMirrorBaseUrl = "https://bmclapi2.bangbang93.com";
    public const string DefaultProgressBarStyle = "classic";
    public const int DefaultProgressBarHeight = 16;
    public const int MinimumProgressBarHeight = 8;
    public const int MaximumProgressBarHeight = 32;

    /// <summary>Gets or sets the maximum Java heap size in mebibytes.</summary>
    public int MemoryMiB { get; set; } = DefaultMemoryMiB;

    /// <summary>Gets or sets the number of downloads the queue may run in parallel.</summary>
    public int MaxConcurrentDownloads { get; set; } = DefaultMaxConcurrentDownloads;

    /// <summary>Gets or sets the preferred download source.</summary>
    public DownloadSourceMode DownloadSourceMode { get; set; } = DownloadSourceMode.OfficialWithFallback;

    /// <summary>Gets or sets the base URL of the mirror used by <see cref="DownloadSourceMode.Mirror"/> and fallback downloads.</summary>
    public string? MirrorBaseUrl { get; set; } = DefaultMirrorBaseUrl;

    /// <summary>Gets or sets an optional explicit Java executable.</summary>
    public string? JavaExecutablePath { get; set; }

    /// <summary>
    /// Gets or sets the Minecraft root directory used for versions/libraries/assets (normally a <c>.minecraft</c> folder).
    /// When null the launcher resolves a default: a <c>.minecraft</c> folder beside the executable, an existing
    /// <c>%APPDATA%\.minecraft</c>, or a newly created portable folder.
    /// </summary>
    public string? MinecraftDirectory { get; set; }

    /// <summary>Gets or sets the Azure application (public client) id used for Microsoft device-code sign-in.</summary>
    public string? MicrosoftClientId { get; set; }

    /// <summary>Gets or sets extra game arguments appended to the generated command line.</summary>
    public string? GameArguments { get; set; }

    /// <summary>Gets or sets extra JVM arguments prepended to the generated command line.</summary>
    public string? JvmArguments { get; set; }

    /// <summary>Gets or sets a value indicating whether snapshot versions are listed.</summary>
    public bool ShowSnapshots { get; set; }

    /// <summary>Gets or sets the progress-bar visual preset key (see the launcher's appearance presets).</summary>
    public string ProgressBarStyle { get; set; } = DefaultProgressBarStyle;

    /// <summary>Gets or sets the progress-bar thickness in device-independent pixels.</summary>
    public int ProgressBarHeight { get; set; } = DefaultProgressBarHeight;

    /// <summary>Gets or sets the saved Cloudreve WebDAV endpoint.</summary>
    public string? CloudDriveUrl { get; set; }

    /// <summary>Gets or sets the saved Cloudreve WebDAV account email.</summary>
    public string? CloudDriveEmail { get; set; }

    /// <summary>Gets or sets a value indicating whether the launcher has already offered to import foreign instances.</summary>
    public const string DefaultFeedbackEndpoint = "https://feedback.astra-launcher-clouddrive.us.ci/api/feedback";
    public const string DefaultEasyTierSharedNode = "tcp://public.easytier.cn:11010";
    public const string DefaultEasyTierHostVirtualIp = "10.77.0.1";
    public const string DefaultEasyTierRoomName = "astra-room";
    public const int DefaultEasyTierGamePort = 25565;
    public const int DefaultEasyTierLocalPort = 25566;
    public const int EasyTierHostRpcPort = 15889;
    public const int EasyTierGuestRpcPort = 15890;
    public const string DefaultPluginIndexUrl = "https://raw.githubusercontent.com/yghxdmdjjdcxy/Astra-launcher/main/plugins/index.json";

    /// <summary>Gets or sets a value indicating whether the launcher has already offered to import foreign instances.</summary>
    public bool ExternalImportPrompted { get; set; }

    /// <summary>Gets or sets a value indicating whether the beta notice is shown on startup.</summary>
    public bool ShowBetaNotice { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the launcher checks GitHub Releases for updates on startup.</summary>
    public bool AutoCheckUpdates { get; set; } = true;

    /// <summary>Gets or sets the feedback upload endpoint (multipart POST).</summary>
    public string FeedbackEndpoint { get; set; } = DefaultFeedbackEndpoint;

    /// <summary>Gets or sets the most recently used instance ids, newest first (max 2).</summary>
    public List<Guid> RecentInstanceIds { get; set; } = [];

    /// <summary>Gets or sets the player name used for offline profiles.</summary>
    public string? OfflinePlayerName { get; set; } = "Player";

    /// <summary>Gets or sets the instance selected when the launcher last closed.</summary>
    public Guid? LastSelectedInstanceId { get; set; }

    /// <summary>Gets or sets the account selected when the launcher last closed.</summary>
    public Guid? SelectedAccountId { get; set; }

    /// <summary>Optional directory containing a manually extracted official EasyTier ZIP. Null uses the managed install.</summary>
    public string? EasyTierDirectory { get; set; }

    /// <summary>Public shared node used for discovery / NAT traversal / relay fallback.</summary>
    public string EasyTierSharedNode { get; set; } = DefaultEasyTierSharedNode;

    /// <summary>Fixed virtual IPv4 of the host inside the EasyTier network.</summary>
    public string EasyTierHostVirtualIp { get; set; } = DefaultEasyTierHostVirtualIp;

    public int EasyTierGamePort { get; set; } = DefaultEasyTierGamePort;

    /// <summary>Local port on a guest that forwards to the host's game port in no-TUN mode.</summary>
    public int EasyTierLocalPort { get; set; } = DefaultEasyTierLocalPort;

    /// <summary>When true, EasyTier creates a TUN device and is started elevated (UAC prompt).</summary>
    public bool EasyTierUseFullTun { get; set; }

    /// <summary>When true, launching a game while connected automatically joins the room.</summary>
    public bool EasyTierAutoJoin { get; set; } = true;

    public string EasyTierLastRoomName { get; set; } = DefaultEasyTierRoomName;

    /// <summary>Curated plugin index hosted on GitHub (raw JSON).</summary>
    public string PluginIndexUrl { get; set; } = DefaultPluginIndexUrl;

    /// <summary>When true, the launcher checks the plugin index and stages updates on startup.</summary>
    public bool PluginAutoUpdate { get; set; } = true;

    /// <summary>Clamps numeric values, restores invalid choices and trims text so persisted settings are always usable.</summary>
    public void Normalize()
    {
        MemoryMiB = Math.Clamp(MemoryMiB, MinimumMemoryMiB, MaximumMemoryMiB);
        MaxConcurrentDownloads = Math.Clamp(MaxConcurrentDownloads, MinimumMaxConcurrentDownloads, MaximumMaxConcurrentDownloads);
        if (!Enum.IsDefined(DownloadSourceMode))
        {
            DownloadSourceMode = DownloadSourceMode.OfficialWithFallback;
        }

        MirrorBaseUrl = NormalizeMirrorBaseUrl(MirrorBaseUrl);
        ProgressBarStyle = NormalizeProgressBarStyle(ProgressBarStyle);
        ProgressBarHeight = Math.Clamp(ProgressBarHeight, MinimumProgressBarHeight, MaximumProgressBarHeight);
        JavaExecutablePath = NormalizeOptionalText(JavaExecutablePath);
        MinecraftDirectory = NormalizeOptionalText(MinecraftDirectory);
        GameArguments = NormalizeOptionalText(GameArguments);
        JvmArguments = NormalizeOptionalText(JvmArguments);
        OfflinePlayerName = NormalizePlayerName(OfflinePlayerName);
        EasyTierDirectory = NormalizeOptionalText(EasyTierDirectory);
        EasyTierSharedNode = NormalizeSharedNode(EasyTierSharedNode);
        EasyTierHostVirtualIp = NormalizeIpv4(EasyTierHostVirtualIp);
        EasyTierGamePort = NormalizePort(EasyTierGamePort, DefaultEasyTierGamePort);
        EasyTierLocalPort = NormalizePort(EasyTierLocalPort, DefaultEasyTierLocalPort);
        EasyTierLastRoomName = NormalizeRoomName(EasyTierLastRoomName);
        PluginIndexUrl = NormalizeHttpUrl(PluginIndexUrl, DefaultPluginIndexUrl);
    }

    private static string NormalizeProgressBarStyle(string? style)
    {
        string value = string.IsNullOrWhiteSpace(style) ? DefaultProgressBarStyle : style.Trim().ToLowerInvariant();
        return value.Length <= 32 ? value : DefaultProgressBarStyle;
    }

    private static string NormalizePlayerName(string? playerName)
    {
        string value = string.IsNullOrWhiteSpace(playerName) ? "Player" : playerName.Trim();
        return value.Length <= 16 ? value : value[..16];
    }

    private static string NormalizeMirrorBaseUrl(string? mirrorBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(mirrorBaseUrl))
        {
            return DefaultMirrorBaseUrl;
        }

        string candidate = mirrorBaseUrl.Trim().TrimEnd('/');
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? parsed))
        {
            return DefaultMirrorBaseUrl;
        }

        return IsHttp(parsed.Scheme) ? candidate : DefaultMirrorBaseUrl;
    }

    private static bool IsHttp(string scheme) =>
        string.Equals(scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
        || string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeOptionalText(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NormalizeSharedNode(string? node)
    {
        string value = string.IsNullOrWhiteSpace(node) ? DefaultEasyTierSharedNode : node.Trim();
        bool supported = value.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("udp://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("ws://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("wss://", StringComparison.OrdinalIgnoreCase);
        return supported && value.Length <= 256 ? value : DefaultEasyTierSharedNode;
    }

    private static string NormalizeIpv4(string? address)
    {
        if (!System.Net.IPAddress.TryParse(address, out System.Net.IPAddress? parsed)
            || parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return DefaultEasyTierHostVirtualIp;
        }

        return parsed.ToString();
    }

    private static int NormalizePort(int port, int fallback) => port is >= 1 and <= 65535 ? port : fallback;

    private static string NormalizeHttpUrl(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        return Uri.TryCreate(value.Trim(), UriKind.Absolute, out Uri? parsed) && IsHttp(parsed.Scheme)
            ? value.Trim()
            : fallback;
    }

    private static string NormalizeRoomName(string? name)
    {
        string value = string.IsNullOrWhiteSpace(name) ? DefaultEasyTierRoomName : name.Trim();
        return value.Length <= 64 ? value : DefaultEasyTierRoomName;
    }
}
