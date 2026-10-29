using System.Globalization;
using System.Text;
using AstraLauncher.Core.Models;
using AstraLauncher.Core.Services;
using AstraLauncher.Game.Java;
using AstraLauncher.Game.Metadata;
using Microsoft.Extensions.Logging;

namespace AstraLauncher.Game.Launch;

/// <summary>
/// Default <see cref="IGameLaunchService"/>: installs missing version files, resolves the version metadata, selects a
/// Java runtime, builds the command line through <see cref="GameCommandBuilder"/> and starts the process.
/// </summary>
public sealed class GameLaunchService : IGameLaunchService
{
    /// <summary>Memory used when the caller does not request a concrete amount.</summary>
    public const int DefaultMemoryMiB = 4096;

    /// <summary>Java major version assumed for old versions whose metadata has no <c>javaVersion</c> block.</summary>
    public const int DefaultJavaMajorVersion = 8;

    private readonly ILauncherPaths _paths;
    private readonly IVersionInstallService _installService;
    private readonly VersionMetadataStore _metadataStore;
    private readonly IJavaRuntimeDetector _javaDetector;
    private readonly GameCommandBuilder _commandBuilder;
    private readonly IGameProcessLauncher _processLauncher;
    private readonly ILogger<GameLaunchService> _logger;

    public GameLaunchService(
        ILauncherPaths paths,
        IVersionInstallService installService,
        VersionMetadataStore metadataStore,
        IJavaRuntimeDetector javaDetector,
        GameCommandBuilder commandBuilder,
        IGameProcessLauncher processLauncher,
        ILogger<GameLaunchService> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(installService);
        ArgumentNullException.ThrowIfNull(metadataStore);
        ArgumentNullException.ThrowIfNull(javaDetector);
        ArgumentNullException.ThrowIfNull(commandBuilder);
        ArgumentNullException.ThrowIfNull(processLauncher);
        ArgumentNullException.ThrowIfNull(logger);
        _paths = paths;
        _installService = installService;
        _metadataStore = metadataStore;
        _javaDetector = javaDetector;
        _commandBuilder = commandBuilder;
        _processLauncher = processLauncher;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IGameProcess> LaunchAsync(
        GameInstance instance,
        GameSession session,
        GameLaunchOptions options,
        IProgress<LaunchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(instance.VersionId))
        {
            throw new GameLaunchValidationException("The instance does not have a version id to launch.");
        }

        if (string.IsNullOrWhiteSpace(instance.DirectoryPath))
        {
            throw new GameLaunchValidationException($"Instance '{instance.Name}' does not have a game directory.");
        }

        string gameDirectory = Path.GetFullPath(instance.DirectoryPath);
        if (!Directory.Exists(gameDirectory))
        {
            throw new GameLaunchValidationException($"The game directory of instance '{instance.Name}' does not exist: {gameDirectory}");
        }

        // Parse the user-supplied arguments before touching the installation so malformed input fails fast.
        IReadOnlyList<string> extraJvmArguments = ParseExtraArguments(options.ExtraJvmArguments, "JVM");
        IReadOnlyList<string> extraGameArguments = ParseExtraArguments(options.ExtraGameArguments, "game");

        Report(progress, "准备启动", $"正在准备启动「{instance.Name}」（版本 {instance.VersionId}）…", 0);

        // 1. Make sure every file the version needs is present and verified before touching Java.
        Report(progress, "检查游戏文件", $"正在检查版本 {instance.VersionId} 的游戏文件…");
        await _installService.InstallAsync(instance.VersionId, new InstallProgressAdapter(progress), cancellationToken).ConfigureAwait(false);

        // 2. Resolve (and if necessary download) the merged metadata of this version.
        Report(progress, "读取版本信息", $"正在读取版本 {instance.VersionId} 的元数据…");
        MinecraftVersionMetadata metadata = await _metadataStore.EnsureResolvedAsync(instance.VersionId, cancellationToken).ConfigureAwait(false);

        // 3. Pick the Java runtime: an explicitly configured one wins, otherwise the best detected candidate is chosen.
        Report(progress, "选择 Java", "正在选择 Java 运行时…");
        (string javaExecutablePath, JavaRuntimeInfo? selectedRuntime) = await SelectJavaAsync(options, metadata, cancellationToken).ConfigureAwait(false);
        if (selectedRuntime is null)
        {
            _logger.LogInformation("Launching version {VersionId} with the user-configured Java executable {JavaPath}.", instance.VersionId, javaExecutablePath);
        }
        else
        {
            _logger.LogInformation(
                "Selected Java {JavaMajorVersion} ({JavaVersion}) at {JavaPath} for version {VersionId}.",
                selectedRuntime.MajorVersion,
                selectedRuntime.Version,
                selectedRuntime.ExecutablePath,
                instance.VersionId);
        }

        // 4. Build the Java command line, forwarding the extra JVM/game arguments parsed from the launch options.
        Report(progress, "生成启动命令", "正在生成启动参数…");
        int memoryMiB = options.MemoryMiB > 0 ? options.MemoryMiB : DefaultMemoryMiB;
        var request = new GameLaunchRequest(
            _paths.MinecraftDirectory,
            gameDirectory,
            javaExecutablePath,
            memoryMiB,
            session,
            options.VersionType,
            extraJvmArguments,
            extraGameArguments,
            options.ServerAddress,
            options.ServerPort);
        if (!string.IsNullOrWhiteSpace(options.ServerAddress))
        {
            _logger.LogInformation(
                "Joining server {ServerAddress}:{ServerPort} directly when launching version {VersionId}.",
                options.ServerAddress,
                options.ServerPort?.ToString(CultureInfo.InvariantCulture) ?? "(default)",
                instance.VersionId);
        }
        if (extraJvmArguments.Count > 0 || extraGameArguments.Count > 0)
        {
            // Only the counts are logged: extra arguments may contain access tokens or other secrets.
            _logger.LogInformation(
                "Forwarding {ExtraJvmArgumentCount} extra JVM and {ExtraGameArgumentCount} extra game arguments for version {VersionId}.",
                extraJvmArguments.Count,
                extraGameArguments.Count,
                instance.VersionId);
        }

        GameLaunchCommand command = _commandBuilder.Build(metadata, request);
        LogClassPathDiagnostics(instance.VersionId, command);

        // 5. Start the process unless the caller cancelled in the meantime.
        cancellationToken.ThrowIfCancellationRequested();
        Report(progress, "启动游戏", "正在启动游戏进程…");
        IGameProcess process = _processLauncher.Start(command);
        _logger.LogInformation(
            "Started version {VersionId} (PID {ProcessId}) with Java {JavaPath}, {MemoryMiB} MiB, working directory {WorkingDirectory}.",
            instance.VersionId,
            process.Id,
            command.ExecutablePath,
            memoryMiB,
            command.WorkingDirectory);
        Report(progress, "启动游戏", $"游戏进程已启动（PID {process.Id}）", 100);
        return process;
    }

    /// <summary>
    /// Returns the Java executable to use. An explicit path is validated verbatim; otherwise the detected runtimes are
    /// filtered down to an exact major-version match first and to the smallest sufficient runtime second.
    /// </summary>
    /// <summary>Logs the classpath size and whether the LWJGL core jar is on it, which helps diagnose loader launch issues.</summary>
    private void LogClassPathDiagnostics(string versionId, GameLaunchCommand command)
    {
        int index = -1;
        for (int i = 0; i < command.Arguments.Count - 1; i++)
        {
            if (string.Equals(command.Arguments[i], "-cp", StringComparison.Ordinal) || string.Equals(command.Arguments[i], "-classpath", StringComparison.Ordinal))
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            _logger.LogWarning("Launch command for {VersionId} has no classpath argument.", versionId);
            return;
        }

        string classPath = command.Arguments[index + 1];
        string[] entries = classPath.Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        bool hasLwjgl = entries.Any(entry => entry.Contains("lwjgl-3.3.3.jar", StringComparison.OrdinalIgnoreCase));
        _logger.LogInformation("Launch classpath for {VersionId}: {EntryCount} entries, lwjglCore={HasLwjgl}.", versionId, entries.Length, hasLwjgl);
    }

    private async Task<(string ExecutablePath, JavaRuntimeInfo? Runtime)> SelectJavaAsync(
        GameLaunchOptions options,
        MinecraftVersionMetadata metadata,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(options.JavaExecutablePath))
        {
            if (!File.Exists(options.JavaExecutablePath))
            {
                throw new GameLaunchValidationException($"The configured Java executable does not exist: {options.JavaExecutablePath}");
            }

            return (Path.GetFullPath(options.JavaExecutablePath), null);
        }

        IReadOnlyList<JavaRuntimeInfo> detected = await _javaDetector.DetectAsync(cancellationToken).ConfigureAwait(false);
        int required = metadata.JavaVersion?.MajorVersion ?? DefaultJavaMajorVersion;
        if (detected.Count == 0)
        {
            throw new GameLaunchValidationException($"No Java runtime was detected; version {metadata.Id} requires Java {required} or newer. Install Java or configure a Java executable.");
        }

        JavaRuntimeInfo? selected = detected.FirstOrDefault(runtime => runtime.MajorVersion == required)
            ?? detected
                .Where(runtime => runtime.MajorVersion >= required)
                .OrderBy(runtime => runtime.MajorVersion)
                .ThenBy(runtime => runtime.ExecutablePath, StringComparer.Ordinal)
                .FirstOrDefault();

        if (selected is null)
        {
            string detectedVersions = string.Join(
                ", ",
                detected.Select(runtime => runtime.MajorVersion.ToString(CultureInfo.InvariantCulture)));
            throw new GameLaunchValidationException(
                $"No Java runtime can run version {metadata.Id}: Java {required} or newer is required, detected Java {detectedVersions}.");
        }

        return (Path.GetFullPath(selected.ExecutablePath), selected);
    }

    /// <summary>
    /// Splits a user-entered argument string into individual arguments. Whitespace separates arguments, double quotes
    /// group text (for example a path containing spaces) and are removed, and an unmatched quote is rejected before
    /// anything is installed or launched.
    /// </summary>
    private static IReadOnlyList<string> ParseExtraArguments(string? text, string kind)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var result = new List<string>();
        var current = new StringBuilder();
        bool quoted = false;
        bool hasToken = false;
        foreach (char character in text)
        {
            if (character == '"')
            {
                quoted = !quoted;
                hasToken = true;
                continue;
            }

            if (char.IsWhiteSpace(character) && !quoted)
            {
                if (hasToken)
                {
                    result.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }

                continue;
            }

            current.Append(character);
            hasToken = true;
        }

        if (quoted) throw new GameLaunchValidationException($"Extra {kind} arguments contain an unmatched double quote.");
        if (hasToken) result.Add(current.ToString());
        return result;
    }

    private static void Report(IProgress<LaunchProgress>? progress, string stage, string message, double? percent = null) =>
        progress?.Report(new LaunchProgress(stage, message, percent));

    /// <summary>Maps installation progress onto the launcher's launch progress model.</summary>
    private sealed class InstallProgressAdapter(IProgress<LaunchProgress>? progress) : IProgress<VersionInstallProgress>
    {
        public void Report(VersionInstallProgress value)
        {
            if (progress is null)
            {
                return;
            }

            double? percent;
            if (value.TotalBytes > 0)
            {
                percent = Percent(value.DownloadedBytes, value.TotalBytes);
            }
            else if (value.TotalFiles > 0)
            {
                percent = Percent(value.CompletedFiles, value.TotalFiles);
            }
            else
            {
                percent = null;
            }

            progress.Report(new LaunchProgress(
                "下载游戏文件",
                $"{value.CompletedFiles}/{value.TotalFiles} 文件 · {value.Stage}",
                percent));
        }

        private static double Percent(long completed, long total) =>
            Math.Clamp((double)completed / total * 100d, 0d, 100d);
    }
}
