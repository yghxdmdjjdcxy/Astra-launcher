using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using AstraLauncher.Game.Metadata;

namespace AstraLauncher.Game.Launch;

/// <summary>Builds the Java command line that starts a Minecraft client from resolved version metadata.</summary>
public sealed class GameCommandBuilder
{
    /// <summary>Creates the executable, argument list and working directory for a launch request.</summary>
    /// <exception cref="GameLaunchValidationException">The metadata or the request cannot be turned into a safe command.</exception>
    public GameLaunchCommand Build(MinecraftVersionMetadata metadata, GameLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(request);
        ValidateMetadata(metadata);
        ValidateRequest(request);

        string minecraftDirectory = Path.GetFullPath(request.MinecraftDirectory);
        string gameDirectory = Path.GetFullPath(request.GameDirectory);
        string versionsDirectory = Path.Combine(minecraftDirectory, "versions");
        string librariesDirectory = Path.Combine(minecraftDirectory, "libraries");
        string assetsDirectory = Path.Combine(minecraftDirectory, "assets");
        string versionDirectory = LaunchPathGuard.EnsureUnder(versionsDirectory, Path.Combine(versionsDirectory, metadata.Id), "Version directory");
        string nativesDirectory = LaunchPathGuard.EnsureUnder(gameDirectory, Path.Combine(gameDirectory, ".astra", "natives", metadata.Id), "Natives directory");
        string clientJar = ResolveClientJar(metadata, versionsDirectory, versionDirectory);
        IReadOnlyList<string> libraries = ResolveLibraries(metadata, librariesDirectory);
        EnsureFiles([clientJar, .. libraries]);
        Directory.CreateDirectory(nativesDirectory);
        NativeLibraryExtractor.Extract(metadata, librariesDirectory, nativesDirectory);
        string classPath = string.Join(Path.PathSeparator, libraries.Append(clientJar));

        IReadOnlyDictionary<string, string> variables = CreateVariables(metadata, request, gameDirectory, assetsDirectory, librariesDirectory, nativesDirectory, classPath);
        IReadOnlyList<string> metadataJvmArguments = SelectArguments(metadata.Arguments.Jvm, variables);
        var arguments = new List<string>(metadataJvmArguments.Count + 8);
        arguments.AddRange(metadataJvmArguments);
        AddLoggingArgument(arguments, metadata, minecraftDirectory);
        AddJvmDefaults(arguments, metadataJvmArguments, classPath, nativesDirectory);
        arguments.Add("-Xmx" + request.MaximumMemoryMiB.ToString(CultureInfo.InvariantCulture) + "M");
        AddExtraArguments(arguments, request.ExtraJvmArguments, "JVM");
        arguments.Add(metadata.MainClass!);
        arguments.AddRange(metadata.Arguments.Game.Count > 0
            ? SelectArguments(metadata.Arguments.Game, variables)
            : SplitLegacyArguments(metadata.LegacyMinecraftArguments, variables));
        AddExtraArguments(arguments, request.ExtraGameArguments, "game");
        return new GameLaunchCommand(Path.GetFullPath(request.JavaExecutablePath), arguments, gameDirectory);
    }

    private static void ValidateMetadata(MinecraftVersionMetadata metadata)
    {
        LaunchPathGuard.ValidateVersionId(metadata.Id, "Version ID");
        if (string.IsNullOrWhiteSpace(metadata.MainClass)) throw new GameLaunchValidationException("Version metadata has no mainClass.");
    }

    private static void ValidateRequest(GameLaunchRequest request)
    {
        if (!File.Exists(request.JavaExecutablePath)) throw new GameLaunchValidationException($"Java executable does not exist: {request.JavaExecutablePath}");
        if (request.MaximumMemoryMiB < 256) throw new GameLaunchValidationException("At least 256 MiB of memory is required.");
        if (string.IsNullOrWhiteSpace(request.Session.PlayerName) || string.IsNullOrWhiteSpace(request.Session.PlayerUuid)) throw new GameLaunchValidationException("A player name and UUID are required.");
        if (request.Session.Kind == GameSessionKind.Authenticated && string.IsNullOrWhiteSpace(request.Session.AccessToken)) throw new GameLaunchValidationException("An authenticated session requires an access token.");
        if (request.Session.Kind == GameSessionKind.Offline && request.Session.AccessToken is not null) throw new GameLaunchValidationException("Offline profiles must not carry an access token.");
    }

    /// <summary>
    /// Resolves the client jar: the metadata download path first, then <c>versions/&lt;id&gt;/&lt;id&gt;.jar</c> and finally
    /// the jar of the inherited version (a child version usually only ships its own JSON). Every candidate is kept inside
    /// the versions directory; the first existing candidate wins and the first candidate is returned when nothing exists
    /// so that <see cref="EnsureFiles"/> reports the most relevant path.
    /// </summary>
    private static string ResolveClientJar(MinecraftVersionMetadata metadata, string versionsDirectory, string versionDirectory)
    {
        var candidates = new List<string>(3);
        if (metadata.Downloads.TryGetValue("client", out DownloadArtifact? client) && !string.IsNullOrWhiteSpace(client.Path))
        {
            candidates.Add(LaunchPathGuard.ResolveUnder(versionDirectory, client.Path, "Client jar path"));
        }

        candidates.Add(LaunchPathGuard.EnsureUnder(versionDirectory, Path.Combine(versionDirectory, $"{metadata.Id}.jar"), "Client jar"));
        if (!string.IsNullOrWhiteSpace(metadata.InheritsFrom))
        {
            LaunchPathGuard.ValidateVersionId(metadata.InheritsFrom, "Inherited version ID");
            string inheritedDirectory = LaunchPathGuard.EnsureUnder(versionsDirectory, Path.Combine(versionsDirectory, metadata.InheritsFrom), "Inherited version directory");
            candidates.Add(LaunchPathGuard.EnsureUnder(inheritedDirectory, Path.Combine(inheritedDirectory, $"{metadata.InheritsFrom}.jar"), "Inherited client jar"));
        }

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate)) return candidate;
        }

        return candidates[0];
    }

    private static IReadOnlyList<string> ResolveLibraries(MinecraftVersionMetadata metadata, string librariesDirectory)
    {
        var result = new List<string>(metadata.Libraries.Count);
        foreach (LibraryDescriptor library in metadata.Libraries)
        {
            if (!LaunchRuleEvaluator.Allows(library.Rules)) continue;
            string? artifactPath = library.Downloads?.Artifact?.Path;
            result.Add(string.IsNullOrWhiteSpace(artifactPath)
                ? LaunchPathGuard.EnsureUnder(librariesDirectory, Path.Combine(librariesDirectory, MavenPath(library.Name)), "Library path")
                : LaunchPathGuard.ResolveUnder(librariesDirectory, artifactPath, $"Artifact path of library '{library.Name}'"));
        }

        return result;
    }

    private static string MavenPath(string coordinate)
    {
        string[] segments = coordinate.Split(':');
        if (segments.Length < 3) throw new GameLaunchValidationException($"Invalid library coordinate '{coordinate}': group, artifact and version are required.");
        ValidateCoordinateSegment(segments[0], coordinate);
        ValidateCoordinateSegment(segments[1], coordinate);
        ValidateCoordinateSegment(segments[2], coordinate);
        string classifier = string.Empty;
        if (segments.Length > 3)
        {
            ValidateCoordinateSegment(segments[3], coordinate);
            classifier = "-" + segments[3];
        }

        string groupPath = segments[0].Replace('.', Path.DirectorySeparatorChar);
        return Path.Combine(groupPath, segments[1], segments[2], segments[1] + "-" + segments[2] + classifier + ".jar");
    }

    private static void ValidateCoordinateSegment(string segment, string coordinate)
    {
        if (segment.Length == 0 || segment.Contains("..", StringComparison.Ordinal) || segment.Contains('/') || segment.Contains('\\'))
        {
            throw new GameLaunchValidationException($"Invalid library coordinate '{coordinate}': segment '{segment}' is not a safe path fragment.");
        }
    }

    private static IReadOnlyDictionary<string, string> CreateVariables(
        MinecraftVersionMetadata metadata,
        GameLaunchRequest request,
        string gameDirectory,
        string assetsDirectory,
        string librariesDirectory,
        string nativesDirectory,
        string classPath) => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["${auth_player_name}"] = request.Session.PlayerName,
            ["${version_name}"] = metadata.Id,
            ["${game_directory}"] = gameDirectory,
            ["${assets_root}"] = assetsDirectory,
            ["${assets_index_name}"] = metadata.AssetIndex?.Id ?? string.Empty,
            ["${auth_uuid}"] = request.Session.PlayerUuid,
            ["${auth_access_token}"] = request.Session.AccessToken ?? "0",
            ["${auth_session}"] = request.Session.AccessToken ?? "-",
            ["${auth_xuid}"] = request.Session.Xuid ?? "0",
            ["${clientid}"] = request.Session.ClientId ?? "0",
            ["${user_type}"] = request.Session.UserType,
            ["${user_properties}"] = "{}",
            ["${version_type}"] = request.VersionType,
            ["${natives_directory}"] = nativesDirectory,
            ["${launcher_name}"] = "AstraLauncher",
            ["${launcher_version}"] = "0.1",
            ["${classpath}"] = classPath,
            ["${classpath_separator}"] = Path.PathSeparator.ToString(),
            ["${library_directory}"] = librariesDirectory,
        };

    private static void AddLoggingArgument(List<string> arguments, MinecraftVersionMetadata metadata, string minecraftDirectory)
    {
        VersionLoggingClient? logging = metadata.Logging?.Client;
        if (logging is null || string.IsNullOrWhiteSpace(logging.File.Id)) return;
        string? loggingPath = TryResolveLoggingFile(Path.Combine(minecraftDirectory, "assets", "logging"), logging.File.Id);
        if (loggingPath is null) return;
        arguments.Add(logging.Argument.Replace("${path}", loggingPath, StringComparison.Ordinal));
    }

    /// <summary>Returns the log configuration path when the file exists; a missing or unsafe id is skipped silently.</summary>
    private static string? TryResolveLoggingFile(string loggingDirectory, string fileId)
    {
        string path;
        try
        {
            path = LaunchPathGuard.ResolveUnder(loggingDirectory, fileId, "Logging configuration path");
        }
        catch (GameLaunchValidationException)
        {
            // A malformed logging id must never make the game unlaunchable; the client falls back to its default configuration.
            return null;
        }

        return File.Exists(path) ? path : null;
    }

    private static void AddJvmDefaults(List<string> arguments, IReadOnlyList<string> metadataJvmArguments, string classPath, string nativesDirectory)
    {
        bool hasClassPath = metadataJvmArguments.Any(argument => string.Equals(argument, "-cp", StringComparison.Ordinal) || string.Equals(argument, "-classpath", StringComparison.Ordinal));
        if (!hasClassPath)
        {
            arguments.Add("-cp");
            arguments.Add(classPath);
        }

        bool hasLibraryPath = metadataJvmArguments.Any(argument => argument.StartsWith("-Djava.library.path", StringComparison.Ordinal));
        if (!hasLibraryPath) arguments.Add("-Djava.library.path=" + nativesDirectory);
    }

    private static void EnsureFiles(IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            if (!File.Exists(path)) throw new GameLaunchValidationException($"Required game file is missing: {path}");
        }
    }

    private static IReadOnlyList<string> SelectArguments(IReadOnlyList<LaunchArgument> arguments, IReadOnlyDictionary<string, string> variables)
    {
        var result = new List<string>();
        foreach (LaunchArgument argument in arguments)
        {
            if (!LaunchRuleEvaluator.Allows(argument.Rules)) continue;
            foreach (string value in argument.Values) result.Add(Replace(value, variables));
        }

        return result;
    }

    private static IReadOnlyList<string> SplitLegacyArguments(string? arguments, IReadOnlyDictionary<string, string> variables)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return [];
        var result = new List<string>();
        foreach (string token in CommandLineTokenizer.Tokenize(arguments)) result.Add(Replace(token, variables));
        return result;
    }

    /// <summary>
    /// Appends user-supplied arguments verbatim. Blank entries are ignored and placeholders are rejected because the
    /// launcher cannot expand variables the user invented.
    /// </summary>
    private static void AddExtraArguments(List<string> arguments, IReadOnlyList<string>? extraArguments, string kind)
    {
        if (extraArguments is null) return;
        foreach (string argument in extraArguments)
        {
            if (string.IsNullOrWhiteSpace(argument)) continue;
            if (argument.Contains("${", StringComparison.Ordinal))
            {
                throw new GameLaunchValidationException($"Extra {kind} argument '{argument}' contains an unsupported placeholder.");
            }

            arguments.Add(argument);
        }
    }

    /// <summary>Expands every known placeholder and rejects arguments that would still contain one.</summary>
    private static string Replace(string value, IReadOnlyDictionary<string, string> variables)
    {
        string result = value;
        foreach ((string placeholder, string replacement) in variables)
        {
            result = result.Replace(placeholder, replacement, StringComparison.Ordinal);
        }

        if (result.Contains("${", StringComparison.Ordinal))
        {
            throw new GameLaunchValidationException($"Launch argument '{value}' contains an unsupported placeholder.");
        }

        return result;
    }

    private static class CommandLineTokenizer
    {
        public static IReadOnlyList<string> Tokenize(string text)
        {
            var result = new List<string>();
            var current = new System.Text.StringBuilder();
            bool quoted = false;
            foreach (char character in text)
            {
                if (character == '"')
                {
                    quoted = !quoted;
                    continue;
                }

                if (char.IsWhiteSpace(character) && !quoted)
                {
                    if (current.Length > 0)
                    {
                        result.Add(current.ToString());
                        current.Clear();
                    }
                }
                else
                {
                    current.Append(character);
                }
            }

            if (quoted) throw new GameLaunchValidationException("Legacy launch arguments contain an unmatched quote.");
            if (current.Length > 0) result.Add(current.ToString());
            return result;
        }
    }
}

/// <summary>Evaluates the Mojang <c>rules</c> lists shared by launch arguments, libraries and native extraction.</summary>
public static class LaunchRuleEvaluator
{
    private static readonly TimeSpan OsVersionRegexTimeout = TimeSpan.FromSeconds(1);

    /// <summary>Returns whether the rules allow the current platform and launcher feature set; no rules means allowed.</summary>
    public static bool Allows(IReadOnlyList<ArgumentRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (rules.Count == 0) return true;
        bool allowed = false;
        foreach (ArgumentRule rule in rules)
        {
            if (Matches(rule)) allowed = string.Equals(rule.Action, "allow", StringComparison.OrdinalIgnoreCase);
        }

        return allowed;
    }

    private static bool Matches(ArgumentRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (rule.OperatingSystemName is not null && !string.Equals(rule.OperatingSystemName, CurrentOperatingSystem(), StringComparison.OrdinalIgnoreCase)) return false;
        if (rule.OperatingSystemArchitecture is not null && !ArchitectureMatches(rule.OperatingSystemArchitecture)) return false;
        if (rule.OperatingSystemVersion is not null && !MatchesOperatingSystemVersion(rule.OperatingSystemVersion)) return false;
        // The launcher enables no optional features, so a rule only matches when every requested feature is disabled.
        if (rule.Features is { Count: > 0 } features)
        {
            foreach (bool value in features.Values)
            {
                if (value) return false;
            }
        }

        return true;
    }

    private static bool ArchitectureMatches(string expected)
    {
        string normalized = expected.Trim().ToUpperInvariant() switch
        {
            "X86_64" or "AMD64" => "X64",
            "AARCH64" => "ARM64",
            var value => value,
        };
        return string.Equals(normalized, CurrentArchitecture(), StringComparison.Ordinal);
    }

    private static bool MatchesOperatingSystemVersion(string pattern)
    {
        try
        {
            return Regex.IsMatch(Environment.OSVersion.Version.ToString(), pattern, RegexOptions.CultureInvariant, OsVersionRegexTimeout);
        }
        catch (ArgumentException)
        {
            // An invalid pattern can never match, so malformed metadata must not break the launch.
            return false;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static string CurrentOperatingSystem()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "windows";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "osx";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "linux";
        // A platform the metadata format does not know: no OS specific rule can match it.
        return "unknown";
    }

    private static string CurrentArchitecture() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X86 => "X86",
        Architecture.X64 => "X64",
        Architecture.Arm => "ARM",
        Architecture.Arm64 => "ARM64",
        _ => RuntimeInformation.ProcessArchitecture.ToString().ToUpperInvariant(),
    };
}

/// <summary>Checks that paths derived from version metadata stay inside the directory they are resolved against.</summary>
internal static class LaunchPathGuard
{
    /// <summary>Rejects version ids that are not a single, directory safe name.</summary>
    public static void ValidateVersionId(string versionId, string description)
    {
        if (string.IsNullOrWhiteSpace(versionId)
            || versionId is "." or ".."
            || versionId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || versionId.Contains(Path.DirectorySeparatorChar)
            || versionId.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new GameLaunchValidationException($"{description} '{versionId}' is not a safe single directory name.");
        }
    }

    /// <summary>Resolves a relative metadata path below <paramref name="rootDirectory"/> and rejects escapes.</summary>
    public static string ResolveUnder(string rootDirectory, string relativePath, string description)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) throw new GameLaunchValidationException($"{description} is empty.");
        if (Path.IsPathRooted(relativePath)) throw new GameLaunchValidationException($"{description} '{relativePath}' must be a relative path.");
        string normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        return EnsureUnder(rootDirectory, Path.Combine(rootDirectory, normalized), description, relativePath);
    }

    /// <summary>Resolves a path and rejects it when the result escapes <paramref name="rootDirectory"/>.</summary>
    public static string EnsureUnder(string rootDirectory, string path, string description, string? declaredPath = null)
    {
        string root = Path.GetFullPath(rootDirectory);
        string candidate = Path.GetFullPath(path);
        string relative = Path.GetRelativePath(root, candidate);
        bool inside = string.Equals(relative, ".", StringComparison.Ordinal)
            || (!Path.IsPathRooted(relative)
                && !string.Equals(relative, "..", StringComparison.Ordinal)
                && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal));
        if (!inside)
        {
            throw new GameLaunchValidationException($"{description} '{declaredPath ?? path}' escapes the root directory '{root}' (resolved to '{candidate}').");
        }

        return candidate;
    }
}

/// <summary>Extracts the Windows native libraries of a version into a directory below the instance.</summary>
public static class NativeLibraryExtractor
{
    /// <summary>Extracts every platform matching native archive; unsafe archive paths and zip entries are rejected.</summary>
    public static void Extract(MinecraftVersionMetadata metadata, string librariesDirectory, string destinationDirectory)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        string librariesRoot = Path.GetFullPath(librariesDirectory);
        string destinationRoot = Path.GetFullPath(destinationDirectory);
        foreach (LibraryDescriptor library in metadata.Libraries)
        {
            if (!LaunchRuleEvaluator.Allows(library.Rules)) continue;
            if (!library.Natives.TryGetValue("windows", out string? classifier) || string.IsNullOrWhiteSpace(classifier)) continue;
            if (library.Downloads is null || !library.Downloads.Classifiers.TryGetValue(classifier, out DownloadArtifact? artifact)) continue;
            string? artifactPath = artifact.Path;
            if (string.IsNullOrWhiteSpace(artifactPath)) continue;
            string archive = LaunchPathGuard.ResolveUnder(librariesRoot, artifactPath, $"Native archive path of library '{library.Name}'");
            if (!File.Exists(archive)) throw new GameLaunchValidationException($"Required native library is missing: {archive}");
            using ZipArchive zip = ZipFile.OpenRead(archive);
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;
                if (entry.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase)) continue;
                if (library.ExtractExcludes.Any(exclude => entry.FullName.StartsWith(exclude, StringComparison.Ordinal))) continue;
                string target = Path.GetFullPath(Path.Combine(destinationRoot, entry.FullName));
                if (!target.StartsWith(destinationRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    throw new GameLaunchValidationException($"Native archive '{archive}' contains an unsafe entry: {entry.FullName}");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
            }
        }
    }
}
