using AstraLauncher.Core.Models;

namespace AstraLauncher.Core.Services;

/// <summary>Persists the saved multiplayer server list.</summary>
public interface IServerRepository
{
    Task<IReadOnlyList<SavedServer>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Adds a new server or replaces the entry with the same id.</summary>
    Task SaveAsync(SavedServer server, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
