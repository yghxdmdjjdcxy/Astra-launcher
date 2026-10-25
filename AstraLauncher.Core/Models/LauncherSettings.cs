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

    /// <summary>Gets or sets extra game arguments appended to the generated command line.</summary>
    public string? GameArguments { get; set; }

    /// <summary>Gets or sets extra JVM arguments prepended to the generated command line.</summary>
    public string? JvmArguments { get; set; }

    /// <summary>Gets or sets a value indicating whether snapshot versions are listed.</summary>
    public bool ShowSnapshots { get; set; }

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
        JavaExecutablePath = NormalizeOptionalText(JavaExecutablePath);
        GameArguments = NormalizeOptionalText(GameArguments);
        JvmArguments = NormalizeOptionalText(JvmArguments);
        OfflinePlayerName = NormalizePlayerName(OfflinePlayerName);
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
