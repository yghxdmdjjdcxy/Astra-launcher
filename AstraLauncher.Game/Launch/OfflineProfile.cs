using System.Security.Cryptography;
using System.Text;

namespace AstraLauncher.Game.Launch;

/// <summary>
/// Creates offline ("no Microsoft account") profiles. The derived UUID matches the identity vanilla servers and other
/// launchers compute for offline players — <c>UUID.nameUUIDFromBytes(("OfflinePlayer:" + name).getBytes(UTF_8))</c> —
/// so a player keeps the same UUID across launchers, server restarts and player-data directories.
/// </summary>
public static class OfflineProfile
{
    /// <summary>Vanilla Minecraft limits offline player names to 16 characters.</summary>
    public const int MaximumPlayerNameLength = 16;

    private const string OfflineUuidPrefix = "OfflinePlayer:";

    /// <summary>
    /// Derives the deterministic offline UUID of <paramref name="playerName"/>: the MD5 digest of
    /// <c>"OfflinePlayer:" + playerName</c> stamped as an RFC 4122 version 3 (name-based) UUID with the IETF variant.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="playerName"/> is empty or whitespace.</exception>
    public static string GeneratePlayerUuid(string playerName)
    {
        if (string.IsNullOrWhiteSpace(playerName))
        {
            throw new ArgumentException("A player name is required to derive an offline UUID.", nameof(playerName));
        }

        byte[] digest = MD5.HashData(Encoding.UTF8.GetBytes(OfflineUuidPrefix + playerName));
        // Same bit operations as java.util.UUID.nameUUIDFromBytes: version 3 in the high nibble of byte 6,
        // IETF variant (10xx) in the high bits of byte 8.
        digest[6] = (byte)((digest[6] & 0x0F) | 0x30);
        digest[8] = (byte)((digest[8] & 0x3F) | 0x80);
        string hex = Convert.ToHexString(digest).ToLowerInvariant();
        // Java prints the 16 digest bytes in order, which is not the same as .NET's little-endian Guid formatting.
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }

    /// <summary>
    /// Creates an offline session for <paramref name="playerName"/>. The name is trimmed before the UUID is derived and
    /// must be non-empty and at most <see cref="MaximumPlayerNameLength"/> characters long.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="playerName"/> is empty, whitespace or longer than 16 characters.</exception>
    public static GameSession Create(string playerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playerName);
        string normalized = playerName.Trim();
        if (normalized.Length > MaximumPlayerNameLength)
        {
            throw new ArgumentException($"Minecraft player names can be at most {MaximumPlayerNameLength} characters long.", nameof(playerName));
        }

        return GameSession.Offline(normalized, GeneratePlayerUuid(normalized));
    }
}
