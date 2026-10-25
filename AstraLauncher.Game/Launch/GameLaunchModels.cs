using AstraLauncher.Game.Metadata;

namespace AstraLauncher.Game.Launch;

public enum GameSessionKind { Authenticated, Offline }
public sealed record GameSession(GameSessionKind Kind, string PlayerName, string PlayerUuid, string? AccessToken, string UserType)
{
    public static GameSession Offline(string playerName, string playerUuid) => new(GameSessionKind.Offline, playerName, playerUuid, null, "legacy");
}
public sealed record GameLaunchRequest(string MinecraftDirectory, string GameDirectory, string JavaExecutablePath, int MaximumMemoryMiB, GameSession Session, string VersionType = "release");
public sealed record GameLaunchCommand(string ExecutablePath, IReadOnlyList<string> Arguments, string WorkingDirectory);
public sealed record GameExitInfo(int ExitCode, DateTimeOffset StartedAt, DateTimeOffset ExitedAt);
public sealed class GameLaunchValidationException(string message) : InvalidOperationException(message);
