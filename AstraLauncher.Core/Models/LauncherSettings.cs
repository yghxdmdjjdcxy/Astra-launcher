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
    public bool ExternalImportPrompted { get; set; }

    /// <summary>Gets or sets a value indicating whether the beta notice is shown on startup.</summary>
    public bool ShowBetaNotice { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the launcher checks GitHub Releases for updates on startup.</summary>
    public bool AutoCheckUpdates { get; set; } = true;

    /// <summary>Gets or sets the player name used for offline profiles.</summary>
    public string? OfflinePlayerName { get; set; } = "Player";

    /// <summary>Gets or sets the instance selected when the launcher last closed.</summary>
    public Guid? LastSelectedInstanceId { get; set; }

    /// <summary>Gets or sets the account selected when the launcher last closed.</summary>
    public Guid? SelectedAccountId { get; set; }

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
}
