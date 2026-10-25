using AstraLauncher.Game.Metadata;

namespace AstraLauncher.Game.Launch;

public enum GameSessionKind { Authenticated, Offline }
public sealed record GameSession(GameSessionKind Kind, string PlayerName, string PlayerUuid, string? AccessToken, string UserType, string? Xuid = null, string? ClientId = null)
{
    public static GameSession Offline(string playerName, string playerUuid) => new(GameSessionKind.Offline, playerName, playerUuid, null, "legacy");
}
public sealed record GameLaunchRequest(string MinecraftDirectory, string GameDirectory, string JavaExecutablePath, int MaximumMemoryMiB, GameSession Session, string VersionType = "release");
public sealed record GameLaunchCommand(string ExecutablePath, IReadOnlyList<string> Arguments, string WorkingDirectory);
public sealed record GameExitInfo(int ExitCode, DateTimeOffset StartedAt, DateTimeOffset ExitedAt);

/// <summary>User-selectable options used when launching an instance.</summary>
public sealed record GameLaunchOptions(int MemoryMiB = 4096, string? JavaExecutablePath = null, string? ExtraGameArguments = null, string? ExtraJvmArguments = null, string VersionType = "release");

/// <summary>Progress information reported while preparing and starting a game process.</summary>
public sealed record LaunchProgress(string Stage, string Message, double? Percent = null);

public sealed class GameLaunchValidationException(string message) : InvalidOperationException(message);
