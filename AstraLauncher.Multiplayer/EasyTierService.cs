using System.ComponentModel;
using AstraLauncher.Core.Services;
using Microsoft.Extensions.Logging;

namespace AstraLauncher.Multiplayer;

public interface IEasyTierService : IDisposable
{
    bool IsRunning { get; }

    /// <summary>When true, launching the game while connected automatically joins the room's entry address.</summary>
    bool AutoJoinEnabled { get; set; }

    EasyTierStatus Status { get; }

    event Action<EasyTierStatus>? StatusChanged;

    EasyTierBinaries? TryResolveBinaries(string? configuredDirectory);

    Task<EasyTierBinaries> EnsureInstalledAsync(
        string? configuredDirectory,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);

    Task StartAsync(
        EasyTierOptions options,
        string? configuredDirectory,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);

    Task StopAsync();

    Task RefreshAsync(CancellationToken cancellationToken = default);
}

/// <summary>Orchestrates binaries, config, the child process and the peer-status poller.</summary>
public sealed class EasyTierService : IEasyTierService
{
    private readonly EasyTierBinaryResolver _resolver;
    private readonly ILauncherPaths _paths;
    private readonly ILogger<EasyTierService> _logger;
    private readonly object _gate = new();
    private EasyTierStatus _status = EasyTierStatus.Idle();
    private EasyTierProcess? _process;
    private EasyTierOptions? _options;
    private string? _cliExecutable;
    private CancellationTokenSource? _pollCancellation;
    private Task? _pollTask;
    private bool _autoJoinEnabled = true;

    public EasyTierService(
        EasyTierBinaryResolver resolver,
        ILauncherPaths paths,
        ILogger<EasyTierService> logger)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(logger);
        _resolver = resolver;
        _paths = paths;
        _logger = logger;
    }

    public string ConfigDirectory => Path.Combine(_paths.ApplicationDataDirectory, "multiplayer");

    public bool IsRunning => Status.IsRunning;

    public bool AutoJoinEnabled
    {
        get => Volatile.Read(ref _autoJoinEnabled);
        set => Volatile.Write(ref _autoJoinEnabled, value);
    }

    public EasyTierStatus Status => Volatile.Read(ref _status);

    public event Action<EasyTierStatus>? StatusChanged;

    public EasyTierBinaries? TryResolveBinaries(string? configuredDirectory) => _resolver.TryResolve(configuredDirectory);

    public Task<EasyTierBinaries> EnsureInstalledAsync(
        string? configuredDirectory,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default) =>
        _resolver.EnsureInstalledAsync(configuredDirectory, progress, cancellationToken);

    public async Task StartAsync(
        EasyTierOptions options,
        string? configuredDirectory,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        if (options.Mode == EasyTierMode.FullTun)
        {
            throw new EasyTierException("完整 TUN 模式需要管理员权限启动子进程，当前公开测试版暂未开放。请使用默认的无 TUN 模式。");
        }

        await StopAsync().ConfigureAwait(false);

        EasyTierBinaries binaries = _resolver.TryResolve(configuredDirectory)
            ?? throw new EasyTierException("尚未找到 EasyTier，请先点击「安装 / 检测 EasyTier」。");

        string configPath = await EasyTierConfigWriter
            .WriteAsync(options, ConfigDirectory, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Starting EasyTier for network {NetworkName} as {Role} in {Mode} mode (config {ConfigPath}).",
            options.NetworkName, options.Role, options.Mode, configPath);

        var process = new EasyTierProcess(line => _logger.LogInformation("[easytier] {Line}", line));
        process.Exited += OnCoreExited;
        try
        {
            progress?.Report("正在启动 easytier-core …");
            process.Start(binaries, configPath, options.Mode == EasyTierMode.FullTun, BuildExtraArguments(options));
        }
        catch (Exception exception) when (exception is Win32Exception or EasyTierException or InvalidOperationException)
        {
            process.Exited -= OnCoreExited;
            await process.DisposeAsync().ConfigureAwait(false);
            throw new EasyTierException(
                "启动 easytier-core 失败。完整 TUN 模式需要同意 UAC 提权；也可能是防火墙或杀毒软件拦截了程序。", exception);
        }

        lock (_gate)
        {
            _process = process;
            _options = options;
            _cliExecutable = binaries.CliExecutable;
            _pollCancellation = new CancellationTokenSource();
            _pollTask = PollLoopAsync(_pollCancellation.Token);
        }

        Publish(new EasyTierStatus(
            true,
            process.ProcessId,
            "已启动，正在验证管理接口…",
            ResolveEntryAddress(options),
            []));
        progress?.Report("EasyTier 已启动，正在验证管理接口…");
        bool rpcReady = await WaitForRpcReadyAsync(binaries.CliExecutable, options.RpcPort, cancellationToken).ConfigureAwait(false);
        Publish(new EasyTierStatus(
            true,
            process.ProcessId,
            rpcReady ? "已启动，正在等待节点加入…" : "已启动，但管理接口未就绪；节点状态可能无法显示",
            ResolveEntryAddress(options),
            []));
        progress?.Report(rpcReady ? "EasyTier 已启动，正在等待节点加入…" : "EasyTier 已启动，但管理接口未就绪。");
    }

    private async Task<bool> WaitForRpcReadyAsync(string cliExecutable, int rpcPort, CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(12);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await EasyTierStatusPoller.QueryAsync(cliExecutable, rpcPort, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or OperationCanceledException)
            {
                await Task.Delay(700, cancellationToken).ConfigureAwait(false);
            }
        }

        return false;
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? pollCancellation;
        Task? pollTask;
        EasyTierProcess? process;
        lock (_gate)
        {
            pollCancellation = _pollCancellation;
            pollTask = _pollTask;
            process = _process;
            _pollCancellation = null;
            _pollTask = null;
            _process = null;
            _options = null;
            _cliExecutable = null;
        }

        pollCancellation?.Cancel();
        if (pollTask is not null)
        {
            try
            {
                await pollTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        pollCancellation?.Dispose();
        if (process is not null)
        {
            process.Exited -= OnCoreExited;
            await process.StopAsync().ConfigureAwait(false);
            await process.DisposeAsync().ConfigureAwait(false);
        }

        Publish(EasyTierStatus.Idle("已停止"));
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        EasyTierOptions? options;
        string? cli;
        lock (_gate)
        {
            options = _options;
            cli = _cliExecutable;
        }

        if (options is null || cli is null || !File.Exists(cli))
        {
            return;
        }

        IReadOnlyList<PeerInfo> peers;
        try
        {
            peers = await EasyTierStatusPoller.QueryAsync(cli, options.RpcPort, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or InvalidOperationException or Win32Exception)
        {
            _logger.LogDebug(exception, "EasyTier peer query failed.");
            return;
        }

        int directCount = peers.Count(peer => peer.IsDirect);
        string message = peers.Count == 0
            ? "已启动，尚未发现其他节点（检查共享节点与防火墙）"
            : $"已发现 {peers.Count} 个节点 · 直连 {directCount} 个";
        EasyTierStatus current = Status;
        Publish(new EasyTierStatus(
            current.IsRunning,
            current.ProcessId,
            message,
            current.EntryAddress,
            peers));
    }

    /// <summary>
    /// Synchronous disposal is deliberate: the DI container disposes singletons with Dispose() on shutdown,
    /// and easytier-core must be stopped or it would survive as an orphan process.
    /// </summary>
    public void Dispose()
    {
        try
        {
            StopAsync().GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to stop EasyTier during shutdown.");
        }
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
                await RefreshAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnCoreExited(object? sender, EventArgs args) => _ = HandleCoreExitAsync();

    private async Task HandleCoreExitAsync()
    {
        await StopAsync().ConfigureAwait(false);
        Publish(EasyTierStatus.Idle("easytier-core 已退出，请查看启动器日志或重新连接。"));
    }

    private void Publish(EasyTierStatus status)
    {
        Volatile.Write(ref _status, status);
        StatusChanged?.Invoke(status);
    }

    /// <summary>
    /// The no-TUN guest cannot initiate connections into the overlay, so a local TCP port is forwarded to
    /// the host's virtual IP. Passing this as a CLI flag avoids version-dependent TOML array names.
    /// </summary>
    public static IReadOnlyList<string> BuildExtraArguments(EasyTierOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Role != RoomRole.Guest || options.Mode != EasyTierMode.NoTun)
        {
            return [];
        }

        string bind = "127.0.0.1:" + options.LocalPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string destination = options.HostVirtualIp + ":"
            + options.GamePort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return ["--port-forward", $"tcp://{bind}/{destination}"];
    }

    public static string ResolveEntryAddress(EasyTierOptions options) => options.Role switch
    {
        RoomRole.Host => "127.0.0.1:" + options.GamePort.ToString(System.Globalization.CultureInfo.InvariantCulture),
        RoomRole.Guest when options.Mode == EasyTierMode.FullTun =>
            options.HostVirtualIp + ":" + options.GamePort.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => "127.0.0.1:" + options.LocalPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
}
