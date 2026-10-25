using AstraLauncher.Core.Models;

namespace AstraLauncher.Game.Launch;

/// <summary>
/// Orchestrates a game launch: ensures the version files are installed, selects a compatible Java runtime,
/// builds the command line and starts the game process.
/// </summary>
public interface IGameLaunchService
{
    /// <summary>
    /// Launches <paramref name="instance"/> with <paramref name="session"/> and returns the running process.
    /// Every preparation step reports a <see cref="LaunchProgress"/> through <paramref name="progress"/>.
    /// </summary>
    /// <exception cref="GameLaunchValidationException">The instance, Java runtime or generated command is not usable.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<IGameProcess> LaunchAsync(
        GameInstance instance,
        GameSession session,
        GameLaunchOptions options,
        IProgress<LaunchProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
