using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using AstraLauncher.Core.Models;
using AstraLauncher.Core.Services;
using AstraLauncher.Game.Java;
using AstraLauncher.Game.Launch;
using AstraLauncher.Game.Metadata;
using AstraLauncher.Infrastructure.FileSystem;
using AstraLauncher.Infrastructure.Net;
using Microsoft.Extensions.Logging.Abstractions;

namespace AstraLauncher.Tests;

public sealed class GameLaunchServiceTests : IAsyncLifetime
{
    private const string VersionId = "1.21.1";
    private const string MainClass = "net.minecraft.client.main.Main";
    private const string MirrorBaseUrl = "https://mirror.example.com";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "AstraLauncher.Tests", Guid.NewGuid().ToString("N"));
    private readonly LauncherPaths _paths;
    private readonly FakeInstallService _install = new();
    private readonly FakeJavaDetector _java = new();
    private readonly RecordingProcessLauncher _launcher = new();
    private readonly FakeManifestService _manifest = new();
    private readonly FakeHttpMessageHandler _handler = new();
    private readonly FakeSettingsService _settings = new();
    private readonly HttpClient _httpClient;
    private readonly string _gameDirectory;
    private readonly string _javaDirectory;

    public GameLaunchServiceTests()
    {
        _paths = new LauncherPaths(_root);
        _paths.EnsureCreated();
        _gameDirectory = Path.Combine(_root, "instances", "Test");
        Directory.CreateDirectory(_gameDirectory);
        _javaDirectory = Path.Combine(_root, "java-runtimes");
        Directory.CreateDirectory(_javaDirectory);
        _httpClient = new HttpClient(_handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        _httpClient.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task LaunchAsync_InstallsBuildsCommandAndStartsProcess()
    {
        await WriteVersionMetadataAsync(majorVersion: 21);
        string javaPath = CreateJavaRuntime("jdk-21");
        _java.Runtimes = [new JavaRuntimeInfo(javaPath, "21.0.1", 21, "Test")];
        GameInstance instance = CreateInstance();
        var reports = new List<LaunchProgress>();

        IGameProcess process = await CreateService().LaunchAsync(
            instance,
            CreateOfflineSession(),
            new GameLaunchOptions(),
            new InlineProgress<LaunchProgress>(reports.Add));

        Assert.Same(_launcher.Process, process);
        Assert.Equal(1, _install.InstallCalls);
        Assert.Equal(VersionId, _install.LastVersionId);
        Assert.Equal(1, _launcher.StartCalls);
        Assert.Equal(0, _handler.RequestCount);

        GameLaunchCommand command = Assert.IsType<GameLaunchCommand>(_launcher.Command);
        Assert.Equal(Path.GetFullPath(javaPath), command.ExecutablePath);
        Assert.Equal(Path.GetFullPath(_gameDirectory), command.WorkingDirectory);
        Assert.Contains(MainClass, command.Arguments);
        Assert.Contains("-Xmx4096M", command.Arguments);
        Assert.Contains("TestPlayer", command.Arguments);
        Assert.Contains(
            Path.Combine(_paths.ApplicationDataDirectory, "versions", VersionId, $"{VersionId}.jar"),
            command.Arguments);

        List<LaunchProgress> installReports = reports.Where(report => string.Equals(report.Stage, "下载游戏文件", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, installReports.Count);
        Assert.Contains("1/3 文件", installReports[0].Message, StringComparison.Ordinal);
        Assert.Equal(100d / 3d, installReports[0].Percent!.Value, 6);
        Assert.Contains("2/3 文件", installReports[1].Message, StringComparison.Ordinal);
        Assert.Equal(200d / 3d, installReports[1].Percent!.Value, 6);
        Assert.True(reports.Count >= 2, $"Expected at least 2 progress reports but received {reports.Count}.");
        Assert.Equal("启动游戏", reports[^1].Stage);
        Assert.Equal(100d, reports[^1].Percent!.Value);
    }

    [Fact]
    public async Task LaunchAsync_WhenMemoryIsNotPositive_UsesDefaultMemory()
    {
        await WriteVersionMetadataAsync(majorVersion: 21);
        _java.Runtimes = [new JavaRuntimeInfo(CreateJavaRuntime("jdk-21"), "21.0.1", 21, null)];

        await CreateService().LaunchAsync(CreateInstance(), CreateOfflineSession(), new GameLaunchOptions(MemoryMiB: -1));

        GameLaunchCommand command = Assert.IsType<GameLaunchCommand>(_launcher.Command);
        Assert.Contains($"-Xmx{GameLaunchService.DefaultMemoryMiB}M", command.Arguments);
    }

    [Fact]
    public async Task LaunchAsync_SelectsJavaMatchingRequiredMajorVersion()
    {
        await WriteVersionMetadataAsync(majorVersion: 21);
        string java17 = CreateJavaRuntime("jdk-17");
        string java21 = CreateJavaRuntime("jdk-21");
        string java25 = CreateJavaRuntime("jdk-25");
        _java.Runtimes =
        [
            new JavaRuntimeInfo(java17, "17.0.9", 17, null),
            new JavaRuntimeInfo(java21, "21.0.1", 21, null),
            new JavaRuntimeInfo(java25, "25.0.1", 25, null),
        ];

        await CreateService().LaunchAsync(CreateInstance(), CreateOfflineSession(), new GameLaunchOptions());

        GameLaunchCommand command = Assert.IsType<GameLaunchCommand>(_launcher.Command);
        Assert.Equal(Path.GetFullPath(java21), command.ExecutablePath);
    }

    [Fact]
    public async Task LaunchAsync_WhenNoExactMatch_FallsBackToLowestSufficientJava()
    {
        await WriteVersionMetadataAsync(majorVersion: 21);
        string java17 = CreateJavaRuntime("jdk-17");
        string java25 = CreateJavaRuntime("jdk-25");
        _java.Runtimes =
        [
            new JavaRuntimeInfo(java17, "17.0.9", 17, null),
            new JavaRuntimeInfo(java25, "25.0.1", 25, null),
        ];

        await CreateService().LaunchAsync(CreateInstance(), CreateOfflineSession(), new GameLaunchOptions());

        GameLaunchCommand command = Assert.IsType<GameLaunchCommand>(_launcher.Command);
        Assert.Equal(Path.GetFullPath(java25), command.ExecutablePath);
    }

    [Fact]
    public async Task LaunchAsync_WhenExplicitJavaPathExist_UsesItWithoutDetecting()
    {
        await WriteVersionMetadataAsync(majorVersion: 21);
        string javaPath = CreateJavaRuntime("jdk-21-explicit");

        await CreateService().LaunchAsync(
            CreateInstance(),
            CreateOfflineSession(),
            new GameLaunchOptions(JavaExecutablePath: javaPath));

        GameLaunchCommand command = Assert.IsType<GameLaunchCommand>(_launcher.Command);
        Assert.Equal(Path.GetFullPath(javaPath), command.ExecutablePath);
        Assert.Equal(0, _java.DetectCalls);
    }

    [Fact]
    public async Task LaunchAsync_WhenExplicitJavaPathIsMissing_Throws()
    {
        await WriteVersionMetadataAsync(majorVersion: 21);
        string missingPath = Path.Combine(_javaDirectory, "missing", "java.exe");

        GameLaunchValidationException exception = await Assert.ThrowsAsync<GameLaunchValidationException>(() => CreateService().LaunchAsync(
            CreateInstance(),
            CreateOfflineSession(),
            new GameLaunchOptions(JavaExecutablePath: missingPath)));

        Assert.Contains("Java", exception.Message, StringComparison.Ordinal);
        Assert.Contains(missingPath, exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, _launcher.StartCalls);
    }

    [Fact]
    public async Task LaunchAsync_WhenNoJavaDetected_ThrowsWithJavaMessage()
    {
        await WriteVersionMetadataAsync(majorVersion: 21);

        GameLaunchValidationException exception = await Assert.ThrowsAsync<GameLaunchValidationException>(() => CreateService().LaunchAsync(
            CreateInstance(),
            CreateOfflineSession(),
            new GameLaunchOptions()));

        Assert.Contains("Java", exception.Message, StringComparison.Ordinal);
        Assert.Contains("21", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, _launcher.StartCalls);
    }

    [Fact]
    public async Task LaunchAsync_WhenDetectedJavaIsTooOld_ThrowsWithRequiredAndDetectedVersions()
    {
        await WriteVersionMetadataAsync(majorVersion: 21);
        _java.Runtimes = [new JavaRuntimeInfo(CreateJavaRuntime("jdk-17"), "17.0.9", 17, null)];

        GameLaunchValidationException exception = await Assert.ThrowsAsync<GameLaunchValidationException>(() => CreateService().LaunchAsync(
            CreateInstance(),
            CreateOfflineSession(),
            new GameLaunchOptions()));

        Assert.Contains("Java", exception.Message, StringComparison.Ordinal);
        Assert.Contains("21", exception.Message, StringComparison.Ordinal);
        Assert.Contains("17", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, _launcher.StartCalls);
    }

    [Fact]
    public async Task LaunchAsync_WhenInstanceDirectoryIsMissing_Throws()
    {
        GameInstance instance = new(Guid.NewGuid(), "Missing", Path.Combine(_root, "does-not-exist"), VersionId, IsManagedDirectory: false);

        GameLaunchValidationException exception = await Assert.ThrowsAsync<GameLaunchValidationException>(() => CreateService().LaunchAsync(
            instance,
            CreateOfflineSession(),
            new GameLaunchOptions()));

        Assert.Contains("does not exist", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, _install.InstallCalls);
        Assert.Equal(0, _launcher.StartCalls);
    }

    [Fact]
    public async Task LaunchAsync_WhenVersionIdIsBlank_Throws()
    {
        GameInstance instance = new(Guid.NewGuid(), "Blank", _gameDirectory, "  ", IsManagedDirectory: false);

        GameLaunchValidationException exception = await Assert.ThrowsAsync<GameLaunchValidationException>(() => CreateService().LaunchAsync(
            instance,
            CreateOfflineSession(),
            new GameLaunchOptions()));

        Assert.Contains("version", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, _install.InstallCalls);
    }

    [Fact]
    public async Task LaunchAsync_ForwardsTokenizedExtraArgumentsToBuilder()
    {
        await WriteVersionMetadataAsync(majorVersion: 21);
        _java.Runtimes = [new JavaRuntimeInfo(CreateJavaRuntime("jdk-21"), "21.0.1", 21, null)];

        await CreateService().LaunchAsync(
            CreateInstance(),
            CreateOfflineSession(),
            new GameLaunchOptions(
                ExtraJvmArguments: "-Dastra.quoted=\"hello world\" -Dastra.flag",
                ExtraGameArguments: "--astra-game \"two words\""));

        GameLaunchCommand command = Assert.IsType<GameLaunchCommand>(_launcher.Command);
        int quotedJvmIndex = IndexOf(command.Arguments, "-Dastra.quoted=hello world");
        int memoryIndex = IndexOf(command.Arguments, "-Xmx4096M");
        int mainClassIndex = IndexOf(command.Arguments, MainClass);
        Assert.True(quotedJvmIndex > memoryIndex, "Extra JVM arguments must follow -Xmx.");
        Assert.True(quotedJvmIndex < mainClassIndex, "Extra JVM arguments must precede the main class.");
        Assert.Contains("-Dastra.flag", command.Arguments);

        int extraGameIndex = IndexOf(command.Arguments, "--astra-game");
        Assert.True(extraGameIndex > IndexOf(command.Arguments, "--gameDir"), "Extra game arguments must follow the metadata game arguments.");
        Assert.Equal("two words", command.Arguments[extraGameIndex + 1]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LaunchAsync_WhenExtraArgumentsHaveUnmatchedQuote_ThrowsWithoutLaunching(bool quoteJvmArguments)
    {
        await WriteVersionMetadataAsync(majorVersion: 21);
        _java.Runtimes = [new JavaRuntimeInfo(CreateJavaRuntime("jdk-21"), "21.0.1", 21, null)];
        GameLaunchOptions options = quoteJvmArguments
            ? new GameLaunchOptions(ExtraJvmArguments: "-Dastra.quoted=\"unterminated")
            : new GameLaunchOptions(ExtraGameArguments: "--astra-game \"unterminated");

        GameLaunchValidationException exception = await Assert.ThrowsAsync<GameLaunchValidationException>(() =>
            CreateService().LaunchAsync(CreateInstance(), CreateOfflineSession(), options));

        Assert.Contains("unmatched", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, _install.InstallCalls);
        Assert.Equal(0, _launcher.StartCalls);
    }

    private static int IndexOf(IReadOnlyList<string> arguments, string value)
    {
        for (int index = 0; index < arguments.Count; index++)
        {
            if (string.Equals(arguments[index], value, StringComparison.Ordinal)) return index;
        }

        return -1;
    }

    private GameLaunchService CreateService() => new(
        _paths,
        _install,
        new VersionMetadataStore(
            _paths,
            _manifest,
            new DownloadSourceResolver(MirrorBaseUrl),
            _settings,
            _httpClient,
            NullLogger<VersionMetadataStore>.Instance),
        _java,
        new GameCommandBuilder(),
        _launcher,
        NullLogger<GameLaunchService>.Instance);

    private GameInstance CreateInstance() => new(Guid.NewGuid(), "Test", _gameDirectory, VersionId, IsManagedDirectory: false);

    private static GameSession CreateOfflineSession() => GameSession.Offline("TestPlayer", "bb77495a-a740-3169-a238-69654c8bd2c1");

    private async Task WriteVersionMetadataAsync(int majorVersion)
    {
        string versionDirectory = Path.Combine(_paths.VersionsDirectory, VersionId);
        Directory.CreateDirectory(versionDirectory);
        string metadataJson = $$"""
            {
              "id": "{{VersionId}}",
              "mainClass": "{{MainClass}}",
              "javaVersion": { "component": "java-runtime-gamma", "majorVersion": {{majorVersion}} },
              "arguments": {
                "jvm": ["-cp", "${classpath}", "-Djava.library.path=${natives_directory}", "-Dminecraft.launcher.brand=${launcher_name}"],
                "game": ["--username", "${auth_player_name}", "--version", "${version_name}", "--gameDir", "${game_directory}"]
              },
              "downloads": {
                "client": { "sha1": "0000000000000000000000000000000000000000", "size": 12, "url": "https://piston-data.mojang.com/v1/objects/missing/client.jar" }
              }
            }
            """;
        await File.WriteAllTextAsync(Path.Combine(versionDirectory, $"{VersionId}.json"), metadataJson);
        await File.WriteAllBytesAsync(Path.Combine(versionDirectory, $"{VersionId}.jar"), []);
    }

    private string CreateJavaRuntime(string directoryName)
    {
        string directory = Path.Combine(_javaDirectory, directoryName, "bin");
        Directory.CreateDirectory(directory);
        string executablePath = Path.Combine(directory, "java.exe");
        File.WriteAllText(executablePath, "not a real java executable");
        return executablePath;
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class FakeInstallService : IVersionInstallService
    {
        public int InstallCalls { get; private set; }

        public string? LastVersionId { get; private set; }

        public Task<VersionInstallPlan> CreatePlanAsync(string versionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task InstallAsync(string versionId, IProgress<VersionInstallProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            InstallCalls++;
            LastVersionId = versionId;
            progress?.Report(new VersionInstallProgress(3, 1, 300, 100, "下载文件", "client.jar"));
            progress?.Report(new VersionInstallProgress(3, 2, 0, 0, "校验文件", null));
            return Task.CompletedTask;
        }

        public Task<bool> IsVersionInstalledAsync(string versionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class FakeJavaDetector : IJavaRuntimeDetector
    {
        public IReadOnlyList<JavaRuntimeInfo> Runtimes { get; set; } = [];

        public int DetectCalls { get; private set; }

        public Task<IReadOnlyList<JavaRuntimeInfo>> DetectAsync(CancellationToken cancellationToken = default)
        {
            DetectCalls++;
            return Task.FromResult(Runtimes);
        }

        public Task<JavaRuntimeInfo?> InspectAsync(string executablePath, CancellationToken cancellationToken = default) =>
            Task.FromResult<JavaRuntimeInfo?>(null);
    }

    private sealed class RecordingProcessLauncher : IGameProcessLauncher
    {
        public int StartCalls { get; private set; }

        public GameLaunchCommand? Command { get; private set; }

        public IGameProcess Process { get; } = new FakeProcess();

        public IGameProcess Start(GameLaunchCommand command)
        {
            StartCalls++;
            Command = command;
            return Process;
        }
    }

    private sealed class FakeProcess : IGameProcess
    {
        public int Id => 4242;

        public bool HasExited => false;

        public IReadOnlyList<string> RecentOutput => [];

        public event EventHandler<string>? OutputReceived
        {
            add { }
            remove { }
        }

        public Task<GameExitInfo> Exited => Task.FromResult(new GameExitInfo(0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch));

        public void Kill()
        {
        }
    }

    private sealed class FakeManifestService : IVersionManifestService
    {
        public Task<VersionManifest> GetManifestAsync(bool forceRefresh = false, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<VersionSummary>> GetVersionsAsync(bool includeSnapshots, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<VersionSummary> GetVersionSummaryAsync(string versionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class FakeSettingsService : ISettingsService
    {
        private readonly LauncherSettings _settings = new() { DownloadSourceMode = DownloadSourceMode.Official };

        public event EventHandler<LauncherSettings>? Changed
        {
            add { }
            remove { }
        }

        public Task<LauncherSettings> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(_settings);

        public Task SaveAsync(LauncherSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

public sealed class OfflineProfileTests
{
    [Fact]
    public void GeneratePlayerUuid_MatchesJavaNameUuidFromBytes()
    {
        // Known value of java.util.UUID.nameUUIDFromBytes(("OfflinePlayer:Notch").getBytes(UTF_8)).
        Assert.Equal("b50ad385-829d-3141-a216-7e7d7539ba7f", OfflineProfile.GeneratePlayerUuid("Notch"));

        // Independently reproduce the version-3 UUID construction and compare the formatted result.
        byte[] digest = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:TestPlayer"));
        digest[6] = (byte)((digest[6] & 0x0F) | 0x30);
        digest[8] = (byte)((digest[8] & 0x3F) | 0x80);
        Assert.Equal(FormatUuid(digest), OfflineProfile.GeneratePlayerUuid("TestPlayer"));
    }

    [Fact]
    public void GeneratePlayerUuid_ProducesVersion3IetfVariantUuid()
    {
        string uuid = OfflineProfile.GeneratePlayerUuid("TestPlayer");

        Assert.Equal(36, uuid.Length);
        Assert.Equal(uuid.ToLowerInvariant(), uuid);
        Assert.Equal('3', uuid[14]);
        Assert.Contains(uuid[19], "89ab");
    }

    [Fact]
    public void GeneratePlayerUuid_IsDeterministic() =>
        Assert.Equal(OfflineProfile.GeneratePlayerUuid("TestPlayer"), OfflineProfile.GeneratePlayerUuid("TestPlayer"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void GeneratePlayerUuid_RejectsBlankNames(string playerName) =>
        Assert.Throws<ArgumentException>(() => OfflineProfile.GeneratePlayerUuid(playerName));

    [Fact]
    public void GeneratePlayerUuid_RejectsNullName() =>
        Assert.ThrowsAny<ArgumentException>(() => OfflineProfile.GeneratePlayerUuid(null!));

    [Fact]
    public void Create_TrimsNameAndBuildsOfflineSession()
    {
        GameSession session = OfflineProfile.Create("  Steve  ");

        Assert.Equal(GameSessionKind.Offline, session.Kind);
        Assert.Equal("Steve", session.PlayerName);
        Assert.Equal("5627dd98-e6be-3c21-b8a8-e92344183641", session.PlayerUuid);
        Assert.Null(session.AccessToken);
        Assert.Equal("legacy", session.UserType);
    }

    [Fact]
    public void Create_AcceptsMaximumLengthName()
    {
        const string playerName = "ABCDEFGHIJKLMNOP";

        GameSession session = OfflineProfile.Create(playerName);

        Assert.Equal(playerName, session.PlayerName);
        Assert.Equal(OfflineProfile.GeneratePlayerUuid(playerName), session.PlayerUuid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ABCDEFGHIJKLMNOPQ")]
    public void Create_RejectsMissingOrTooLongName(string playerName) =>
        Assert.Throws<ArgumentException>(() => OfflineProfile.Create(playerName));

    [Fact]
    public void Create_RejectsNullName() =>
        Assert.ThrowsAny<ArgumentException>(() => OfflineProfile.Create(null!));

    private static string FormatUuid(byte[] digest)
    {
        string hex = Convert.ToHexString(digest).ToLowerInvariant();
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }
}
