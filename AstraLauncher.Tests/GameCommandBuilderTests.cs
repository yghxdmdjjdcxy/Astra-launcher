using System.IO;
using System.Runtime.InteropServices;
using AstraLauncher.Game.Launch;
using AstraLauncher.Game.Metadata;

namespace AstraLauncher.Tests;

public sealed class GameCommandBuilderTests : IDisposable
{
    private const string VersionType = "release";
    private static readonly IReadOnlyDictionary<string, string> NoNatives = new Dictionary<string, string>(StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<string, DownloadArtifact> NoClassifiers = new Dictionary<string, DownloadArtifact>(StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<string, DownloadArtifact> NoDownloads = new Dictionary<string, DownloadArtifact>(StringComparer.Ordinal);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "AstraLauncher.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _gameDirectory;
    private readonly string _librariesDirectory;
    private readonly string _javaPath;
    private readonly GameCommandBuilder _builder = new();

    public GameCommandBuilderTests()
    {
        _gameDirectory = Path.Combine(_root, "instances", "demo");
        _librariesDirectory = Path.Combine(_root, "libraries");
        _javaPath = Path.Combine(_root, "java", "bin", "java.exe");
        Directory.CreateDirectory(_gameDirectory);
        Directory.CreateDirectory(_librariesDirectory);
        WriteFile(_javaPath, "fake-java");
    }

    [Fact]
    public void Build_ModernMetadata_ProducesCompleteCommand()
    {
        const string versionId = "1.21.1";
        string clientJar = WriteVersionJar(versionId);
        string libraryJar = WriteFile(Path.Combine(_librariesDirectory, "com", "example", "demo", "1.0", "demo-1.0.jar"), "library");
        string nativesDirectory = Path.Combine(_gameDirectory, ".astra", "natives", versionId);
        MinecraftVersionMetadata metadata = CreateMetadata(
            versionId,
            libraries: [Library("com.example:demo:1.0", "com/example/demo/1.0/demo-1.0.jar")],
            jvm: [new LaunchArgument(["-Dastra.test=true"], [])],
            game:
            [
                new LaunchArgument(
                [
                    "--username", "${auth_player_name}", "--uuid", "${auth_uuid}", "--accessToken", "${auth_access_token}",
                    "--xuid", "${auth_xuid}", "--clientId", "${clientid}", "--version", "${version_name}",
                    "--gameDir", "${game_directory}", "--assetsDir", "${assets_root}", "--assetIndex", "${assets_index_name}",
                    "--userType", "${user_type}", "--versionType", "${version_type}",
                ], []),
            ]);

        GameLaunchCommand command = _builder.Build(metadata, CreateRequest(Authenticated()));

        Assert.Equal(Path.GetFullPath(_javaPath), command.ExecutablePath);
        Assert.Equal(Path.GetFullPath(_gameDirectory), command.WorkingDirectory);
        int classPathIndex = IndexOf(command.Arguments, "-cp");
        Assert.True(classPathIndex >= 0, "The command must contain a -cp argument.");
        string[] entries = command.Arguments[classPathIndex + 1].Split(Path.PathSeparator);
        Assert.Equal(Path.GetFullPath(clientJar), entries[^1]);
        Assert.Contains(Path.GetFullPath(libraryJar), entries);
        Assert.Contains(command.Arguments, argument => string.Equals(argument, "-Djava.library.path=" + Path.GetFullPath(nativesDirectory), StringComparison.Ordinal));
        Assert.Contains("net.minecraft.client.main.Main", command.Arguments);
        Assert.Contains("--username", command.Arguments);
        Assert.Contains("Steve", command.Arguments);
        int jvmIndex = IndexOf(command.Arguments, "-Dastra.test=true");
        int memoryIndex = IndexOf(command.Arguments, "-Xmx4096M");
        Assert.True(jvmIndex >= 0, "The metadata JVM argument must be present.");
        Assert.True(memoryIndex > jvmIndex, "-Xmx must come after the metadata JVM arguments so it can override them.");
        Assert.True(memoryIndex > classPathIndex, "-Xmx must come after -cp.");
        Assert.DoesNotContain(command.Arguments, argument => argument.Contains("${", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_LegacyMetadata_AddsClassPathAndLibraryPath()
    {
        const string versionId = "1.8.9";
        string clientJar = WriteVersionJar(versionId);
        string nativesDirectory = Path.Combine(_gameDirectory, ".astra", "natives", versionId);
        MinecraftVersionMetadata metadata = CreateMetadata(
            versionId,
            legacyMinecraftArguments: "--username ${auth_player_name} --session ${auth_session} --gameDir ${game_directory} --assetsDir ${assets_root} --uuid ${auth_uuid} --accessToken ${auth_access_token} --version ${version_name} --versionType ${version_type}");

        GameLaunchCommand command = _builder.Build(metadata, CreateRequest(Authenticated()));

        int classPathIndex = IndexOf(command.Arguments, "-cp");
        Assert.True(classPathIndex >= 0, "A legacy version must still receive -cp.");
        Assert.Equal(Path.GetFullPath(clientJar), command.Arguments[classPathIndex + 1]);
        Assert.Contains(command.Arguments, argument => string.Equals(argument, "-Djava.library.path=" + Path.GetFullPath(nativesDirectory), StringComparison.Ordinal));
        AssertArgumentPair(command.Arguments, "--username", "Steve");
        AssertArgumentPair(command.Arguments, "--session", "access-token");
        int mainClassIndex = IndexOf(command.Arguments, "net.minecraft.client.main.Main");
        int firstLegacyArgument = IndexOf(command.Arguments, "--username");
        Assert.True(mainClassIndex >= 0, "The main class must be present.");
        Assert.True(firstLegacyArgument > mainClassIndex, "Legacy game arguments must follow the main class.");
        Assert.DoesNotContain(command.Arguments, argument => argument.Contains("${", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_EvaluatesOperatingSystemAndArchitectureRules()
    {
        const string versionId = "1.20.6";
        WriteVersionJar(versionId);
        string windowsJar = WriteFile(Path.Combine(_librariesDirectory, "com", "example", "windows", "1.0", "windows-1.0.jar"), "windows");
        string foreignArchitecture = NonMatchingArchitecture();
        MinecraftVersionMetadata metadata = CreateMetadata(
            versionId,
            libraries:
            [
                Library("com.example:windows:1.0", "com/example/windows/1.0/windows-1.0.jar", [Rule("allow", os: "windows")]),
                Library("com.example:linux:1.0", "com/example/linux/1.0/linux-1.0.jar", [Rule("allow", os: "linux")]),
                Library("com.example:arch:1.0", "com/example/arch/1.0/arch-1.0.jar", [Rule("allow", arch: foreignArchitecture)]),
            ],
            game:
            [
                new LaunchArgument(["--arch-specific"], [Rule("allow", os: "windows", arch: foreignArchitecture)]),
                new LaunchArgument(["--both-present"], [Rule("allow", os: "windows")]),
                new LaunchArgument(["--denied"], [Rule("allow", os: "windows"), Rule("disallow", os: "windows")]),
            ]);

        GameLaunchCommand command = _builder.Build(metadata, CreateRequest(Authenticated()));

        int classPathIndex = IndexOf(command.Arguments, "-cp");
        string[] entries = command.Arguments[classPathIndex + 1].Split(Path.PathSeparator);
        Assert.Contains(Path.GetFullPath(windowsJar), entries);
        Assert.DoesNotContain(entries, entry => entry.Contains("linux-1.0.jar", StringComparison.Ordinal));
        Assert.DoesNotContain(entries, entry => entry.Contains("arch-1.0.jar", StringComparison.Ordinal));
        Assert.DoesNotContain("--arch-specific", command.Arguments);
        Assert.DoesNotContain("--denied", command.Arguments);
        Assert.Contains("--both-present", command.Arguments);
    }

    [Fact]
    public void Build_RejectsMetadataIdAndLibraryPathsOutsideTheirRoots()
    {
        GameLaunchValidationException idException = Assert.Throws<GameLaunchValidationException>(() =>
        {
            _ = _builder.Build(CreateMetadata("..\\evil"), CreateRequest(Authenticated()));
        });
        Assert.Contains("evil", idException.Message, StringComparison.Ordinal);

        WriteVersionJar("1.20.6");
        MinecraftVersionMetadata metadata = CreateMetadata("1.20.6", libraries: [Library("com.example:evil:1.0", "../../evil.jar")]);
        GameLaunchValidationException libraryException = Assert.Throws<GameLaunchValidationException>(() =>
        {
            _ = _builder.Build(metadata, CreateRequest(Authenticated()));
        });
        Assert.Contains("evil.jar", libraryException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_RejectsUnknownPlaceholders()
    {
        const string versionId = "1.20.6";
        WriteVersionJar(versionId);
        MinecraftVersionMetadata metadata = CreateMetadata(versionId, game: [new LaunchArgument(["--token", "${unknown_token}"], [])]);

        GameLaunchValidationException exception = Assert.Throws<GameLaunchValidationException>(() =>
        {
            _ = _builder.Build(metadata, CreateRequest(Authenticated()));
        });

        Assert.Contains("${unknown_token}", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_AddsLoggingArgumentOnlyWhenConfigurationExists()
    {
        const string versionId = "1.12.2";
        WriteVersionJar(versionId);
        string loggingPath = WriteFile(Path.Combine(_root, "assets", "logging", "client-1.12.xml"), "<configuration/>");
        MinecraftVersionMetadata metadata = CreateMetadata(versionId, logging: CreateLogging("client-1.12.xml"));

        GameLaunchCommand command = _builder.Build(metadata, CreateRequest(Authenticated()));

        Assert.Contains(command.Arguments, argument => string.Equals(argument, "-Dlog4j.configurationFile=" + Path.GetFullPath(loggingPath), StringComparison.Ordinal));

        MinecraftVersionMetadata missingLogging = CreateMetadata(versionId, logging: CreateLogging("client-missing.xml"));
        GameLaunchCommand fallback = _builder.Build(missingLogging, CreateRequest(Authenticated()));
        Assert.DoesNotContain(fallback.Arguments, argument => argument.Contains("log4j.configurationFile", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_ExpandsSessionPlaceholdersAndOfflineDefaults()
    {
        const string versionId = "1.20.6";
        WriteVersionJar(versionId);
        MinecraftVersionMetadata metadata = CreateMetadata(
            versionId,
            game:
            [
                new LaunchArgument(
                [
                    "--username", "${auth_player_name}", "--uuid", "${auth_uuid}", "--accessToken", "${auth_access_token}",
                    "--xuid", "${auth_xuid}", "--clientId", "${clientid}", "--userProperties", "${user_properties}", "--session", "${auth_session}",
                ], []),
            ]);

        GameLaunchCommand online = _builder.Build(metadata, CreateRequest(Authenticated()));
        AssertArgumentPair(online.Arguments, "--username", "Steve");
        AssertArgumentPair(online.Arguments, "--uuid", "uuid-1234");
        AssertArgumentPair(online.Arguments, "--accessToken", "access-token");
        AssertArgumentPair(online.Arguments, "--xuid", "xuid-123");
        AssertArgumentPair(online.Arguments, "--clientId", "client-456");
        AssertArgumentPair(online.Arguments, "--userProperties", "{}");
        AssertArgumentPair(online.Arguments, "--session", "access-token");

        GameLaunchCommand offline = _builder.Build(metadata, CreateRequest(GameSession.Offline("Alex", "uuid-offline")));
        AssertArgumentPair(offline.Arguments, "--username", "Alex");
        AssertArgumentPair(offline.Arguments, "--uuid", "uuid-offline");
        AssertArgumentPair(offline.Arguments, "--accessToken", "0");
        AssertArgumentPair(offline.Arguments, "--xuid", "0");
        AssertArgumentPair(offline.Arguments, "--clientId", "0");
        AssertArgumentPair(offline.Arguments, "--userProperties", "{}");
        AssertArgumentPair(offline.Arguments, "--session", "-");
    }

    [Fact]
    public void Build_FallsBackToInheritedVersionJar()
    {
        const string parentId = "1.20.6";
        const string childId = "1.20.6-astra";
        string parentJar = WriteFile(Path.Combine(_root, "versions", parentId, parentId + ".jar"), "parent-jar");
        var downloads = new Dictionary<string, DownloadArtifact>(StringComparer.Ordinal)
        {
            ["client"] = new DownloadArtifact("client.jar", null, null, null),
        };
        MinecraftVersionMetadata metadata = CreateMetadata(childId, inheritsFrom: parentId, downloads: downloads);

        GameLaunchCommand command = _builder.Build(metadata, CreateRequest(Authenticated()));

        int classPathIndex = IndexOf(command.Arguments, "-cp");
        Assert.True(classPathIndex >= 0, "The command must contain a -cp argument.");
        Assert.Equal(Path.GetFullPath(parentJar), command.Arguments[classPathIndex + 1]);
    }

    [Fact]
    public void Build_ResolvesMavenCoordinateWhenArtifactIsMissing()
    {
        const string versionId = "1.20.6";
        WriteVersionJar(versionId);
        string mavenJar = WriteFile(Path.Combine(_librariesDirectory, "com", "example", "demo", "1.0", "demo-1.0.jar"), "maven");
        MinecraftVersionMetadata metadata = CreateMetadata(versionId, libraries: [new LibraryDescriptor("com.example:demo:1.0", null, NoNatives, [], [])]);

        GameLaunchCommand command = _builder.Build(metadata, CreateRequest(Authenticated()));

        int classPathIndex = IndexOf(command.Arguments, "-cp");
        string[] entries = command.Arguments[classPathIndex + 1].Split(Path.PathSeparator);
        Assert.Contains(Path.GetFullPath(mavenJar), entries);
    }

    [Fact]
    public void Build_AppendsExtraJvmAndGameArgumentsInOrder()
    {
        const string versionId = "1.20.6";
        WriteVersionJar(versionId);
        MinecraftVersionMetadata metadata = CreateMetadata(
            versionId,
            jvm: [new LaunchArgument(["-Dastra.metadata=true"], [])],
            game: [new LaunchArgument(["--username", "${auth_player_name}"], [])]);

        GameLaunchCommand command = _builder.Build(
            metadata,
            CreateRequest(
                Authenticated(),
                extraJvmArguments: ["-Dastra.extra=quoted value", "-Dastra.flag"],
                extraGameArguments: ["--astra-game", "two words"]));

        int memoryIndex = IndexOf(command.Arguments, "-Xmx4096M");
        int mainClassIndex = IndexOf(command.Arguments, "net.minecraft.client.main.Main");
        int extraJvmIndex = IndexOf(command.Arguments, "-Dastra.extra=quoted value");
        Assert.True(extraJvmIndex > memoryIndex, "Extra JVM arguments must follow -Xmx.");
        Assert.True(extraJvmIndex < mainClassIndex, "Extra JVM arguments must precede the main class.");
        Assert.Contains("-Dastra.flag", command.Arguments);

        int metadataGameIndex = IndexOf(command.Arguments, "--username");
        int extraGameIndex = IndexOf(command.Arguments, "--astra-game");
        Assert.True(extraGameIndex > metadataGameIndex, "Extra game arguments must follow the metadata game arguments.");
        Assert.Equal("two words", command.Arguments[extraGameIndex + 1]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Build_RejectsUnknownPlaceholdersInExtraArguments(bool placeInJvmArguments)
    {
        const string versionId = "1.20.6";
        WriteVersionJar(versionId);
        MinecraftVersionMetadata metadata = CreateMetadata(versionId);
        IReadOnlyList<string>? extraJvm = placeInJvmArguments ? ["-Dastra.extra=${unknown}"] : null;
        IReadOnlyList<string>? extraGame = placeInJvmArguments ? null : ["--astra-extra", "${unknown}"];

        GameLaunchValidationException exception = Assert.Throws<GameLaunchValidationException>(() =>
        {
            _ = _builder.Build(
                metadata,
                CreateRequest(Authenticated(), extraJvmArguments: extraJvm, extraGameArguments: extraGame));
        });

        Assert.Contains("${unknown}", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_IgnoresBlankExtraArguments()
    {
        const string versionId = "1.20.6";
        WriteVersionJar(versionId);
        MinecraftVersionMetadata metadata = CreateMetadata(versionId);

        GameLaunchCommand command = _builder.Build(
            metadata,
            CreateRequest(
                Authenticated(),
                extraJvmArguments: ["", "   ", "-Dastra.extra=true"],
                extraGameArguments: ["\t", "--astra-extra", "  "]));

        Assert.Contains("-Dastra.extra=true", command.Arguments);
        Assert.Contains("--astra-extra", command.Arguments);
        Assert.DoesNotContain(command.Arguments, argument => string.IsNullOrWhiteSpace(argument));
    }

    [Fact]
    public void Build_AppendsServerJoinArgumentsAfterGameArguments()
    {
        const string versionId = "1.20.6";
        WriteVersionJar(versionId);
        MinecraftVersionMetadata metadata = CreateMetadata(versionId);

        GameLaunchCommand command = _builder.Build(
            metadata,
            CreateRequest(Authenticated(), serverAddress: "mc.example.com", serverPort: 25566));

        int serverIndex = IndexOf(command.Arguments, "--server");
        Assert.True(serverIndex > IndexOf(command.Arguments, metadata.MainClass!));
        Assert.Equal("mc.example.com", command.Arguments[serverIndex + 1]);
        Assert.Equal("--port", command.Arguments[serverIndex + 2]);
        Assert.Equal("25566", command.Arguments[serverIndex + 3]);
    }

    [Fact]
    public void Build_ServerAddressWithoutPort_OmitsPortArgument()
    {
        const string versionId = "1.20.6";
        WriteVersionJar(versionId);
        MinecraftVersionMetadata metadata = CreateMetadata(versionId);

        GameLaunchCommand command = _builder.Build(metadata, CreateRequest(Authenticated(), serverAddress: "mc.example.com"));

        Assert.Contains("--server", command.Arguments);
        Assert.DoesNotContain("--port", command.Arguments);
    }

    [Fact]
    public void Build_WithoutServerJoin_OmitsServerArguments()
    {
        const string versionId = "1.20.6";
        WriteVersionJar(versionId);
        MinecraftVersionMetadata metadata = CreateMetadata(versionId);

        GameLaunchCommand command = _builder.Build(metadata, CreateRequest(Authenticated()));

        Assert.DoesNotContain("--server", command.Arguments);
        Assert.DoesNotContain("--port", command.Arguments);
    }

    [Theory]
    [InlineData("bad address", null)]
    [InlineData("ok.example.com", 0)]
    [InlineData("ok.example.com", 70000)]
    public void Build_InvalidServerJoinArguments_Throw(string? serverAddress, int? serverPort)
    {
        const string versionId = "1.20.6";
        WriteVersionJar(versionId);
        MinecraftVersionMetadata metadata = CreateMetadata(versionId);

        Assert.Throws<GameLaunchValidationException>(() =>
            _builder.Build(metadata, CreateRequest(Authenticated(), serverAddress: serverAddress, serverPort: serverPort)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static MinecraftVersionMetadata CreateMetadata(
        string id,
        string? inheritsFrom = null,
        string mainClass = "net.minecraft.client.main.Main",
        IReadOnlyList<LibraryDescriptor>? libraries = null,
        IReadOnlyList<LaunchArgument>? jvm = null,
        IReadOnlyList<LaunchArgument>? game = null,
        string? legacyMinecraftArguments = null,
        IReadOnlyDictionary<string, DownloadArtifact>? downloads = null,
        VersionLogging? logging = null) =>
        new(
            id,
            inheritsFrom,
            mainClass,
            new AssetIndexDescriptor("17", null, null, null, null),
            null,
            libraries ?? [],
            new VersionArguments(game ?? [], jvm ?? []),
            legacyMinecraftArguments,
            downloads ?? NoDownloads,
            logging);

    private static LibraryDescriptor Library(string name, string artifactPath, IReadOnlyList<ArgumentRule>? rules = null) =>
        new(name, new LibraryDownloads(new DownloadArtifact(artifactPath, null, null, null), NoClassifiers), NoNatives, [], rules ?? []);

    private static ArgumentRule Rule(string action, string? os = null, string? arch = null) =>
        new(action, os, null, arch, new Dictionary<string, bool>(StringComparer.Ordinal));

    private static VersionLogging CreateLogging(string fileId) =>
        new(new VersionLoggingClient("-Dlog4j.configurationFile=${path}", new VersionLoggingFile(fileId, null, null, null), "log4j2-xml"));

    private static GameSession Authenticated() =>
        new(GameSessionKind.Authenticated, "Steve", "uuid-1234", "access-token", "msa", "xuid-123", "client-456");

    private GameLaunchRequest CreateRequest(
        GameSession session,
        IReadOnlyList<string>? extraJvmArguments = null,
        IReadOnlyList<string>? extraGameArguments = null,
        string? serverAddress = null,
        int? serverPort = null) =>
        new(
            _root,
            _gameDirectory,
            _javaPath,
            4096,
            session,
            VersionType,
            extraJvmArguments,
            extraGameArguments,
            serverAddress,
            serverPort);

    private string WriteVersionJar(string versionId) =>
        WriteFile(Path.Combine(_root, "versions", versionId, versionId + ".jar"), "client-jar");

    private static string WriteFile(string path, string content)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(path, content);
        return Path.GetFullPath(path);
    }

    private static int IndexOf(IReadOnlyList<string> arguments, string value)
    {
        for (int index = 0; index < arguments.Count; index++)
        {
            if (string.Equals(arguments[index], value, StringComparison.Ordinal)) return index;
        }

        return -1;
    }

    private static void AssertArgumentPair(IReadOnlyList<string> arguments, string name, string expectedValue)
    {
        int index = IndexOf(arguments, name);
        Assert.True(index >= 0, $"Argument '{name}' is missing from the command line.");
        Assert.True(index + 1 < arguments.Count, $"Argument '{name}' has no value.");
        Assert.Equal(expectedValue, arguments[index + 1]);
    }

    private static string NonMatchingArchitecture() =>
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "x64" : "arm64";
}
