using System.Text.Json;
using AstraLauncher.Core.Models;
using AstraLauncher.Core.Services;
using Microsoft.Extensions.Logging;

namespace AstraLauncher.Infrastructure.Servers;

/// <summary>
/// Stores the saved server list as <c>&lt;launcher data&gt;\servers.json</c>. A malformed file never prevents the
/// launcher from starting: it is logged and treated as an empty list until the next successful save replaces it.
/// </summary>
public sealed class JsonServerRepository : IServerRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _filePath;
    private readonly ILogger<JsonServerRepository> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonServerRepository(ILauncherPaths paths, ILogger<JsonServerRepository> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(logger);
        _filePath = Path.Combine(paths.ApplicationDataDirectory, "servers.json");
        _logger = logger;
    }

    public async Task<IReadOnlyList<SavedServer>> ListAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlyList<SavedServer> servers = await ReadAsync(cancellationToken).ConfigureAwait(false);
            return servers
                .OrderBy(server => server.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(server => server.Address, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(SavedServer server, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var servers = (await ReadAsync(cancellationToken).ConfigureAwait(false)).ToList();
            int index = servers.FindIndex(existing => existing.Id == server.Id);
            if (index >= 0)
            {
                servers[index] = server;
            }
            else
            {
                servers.Add(server);
            }

            await WriteAsync(servers, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var servers = (await ReadAsync(cancellationToken).ConfigureAwait(false)).ToList();
            if (servers.RemoveAll(server => server.Id == id) == 0)
            {
                return;
            }

            await WriteAsync(servers, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<SavedServer>> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            return [];
        }

        try
        {
            await using FileStream stream = new(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            List<SavedServer>? servers = await JsonSerializer.DeserializeAsync<List<SavedServer>>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            if (servers is null)
            {
                return [];
            }

            return servers
                .Where(server => !string.IsNullOrWhiteSpace(server.Name) && !string.IsNullOrWhiteSpace(server.Address))
                .GroupBy(server => server.Id)
                .Select(group => group.First())
                .ToArray();
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "服务器列表文件格式无效，将暂时按空列表处理：{Path}", _filePath);
            return [];
        }
        catch (IOException exception)
        {
            _logger.LogWarning(exception, "读取服务器列表文件失败，将暂时按空列表处理：{Path}", _filePath);
            return [];
        }
    }

    private async Task WriteAsync(IReadOnlyList<SavedServer> servers, CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(_filePath) ?? throw new InvalidOperationException("服务器列表路径没有父目录。");
        Directory.CreateDirectory(directory);
        string temporaryPath = _filePath + ".tmp";
        try
        {
            await using (FileStream stream = new(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(stream, servers, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup only.
            }
        }
    }
}
