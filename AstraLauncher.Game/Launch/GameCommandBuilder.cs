using System.IO.Compression;
using System.Runtime.InteropServices;
using AstraLauncher.Game.Metadata;

namespace AstraLauncher.Game.Launch;

public sealed class GameCommandBuilder
{
    public GameLaunchCommand Build(MinecraftVersionMetadata metadata, GameLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(metadata); ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(metadata, request);
        string root = Path.GetFullPath(request.MinecraftDirectory);
        string gameDirectory = Path.GetFullPath(request.GameDirectory);
        string versionDirectory = Path.Combine(root, "versions", metadata.Id);
        string librariesDirectory = Path.Combine(root, "libraries");
        string assetsDirectory = Path.Combine(root, "assets");
        string nativesDirectory = Path.Combine(gameDirectory, ".astra", "natives", metadata.Id);
        string clientJar = Path.Combine(versionDirectory, $"{metadata.Id}.jar");
        IReadOnlyList<string> libraries = ResolveLibraries(metadata, librariesDirectory);
        EnsureFiles([clientJar, .. libraries]);
        Directory.CreateDirectory(nativesDirectory);
        NativeLibraryExtractor.Extract(metadata, librariesDirectory, nativesDirectory);
        string classPath = string.Join(Path.PathSeparator, libraries.Append(clientJar));
        var variables = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["${auth_player_name}"] = request.Session.PlayerName, ["${version_name}"] = metadata.Id, ["${game_directory}"] = gameDirectory,
            ["${assets_root}"] = assetsDirectory, ["${assets_index_name}"] = metadata.AssetIndex?.Id ?? string.Empty, ["${auth_uuid}"] = request.Session.PlayerUuid,
            ["${auth_access_token}"] = request.Session.AccessToken ?? "0", ["${user_type}"] = request.Session.UserType, ["${version_type}"] = request.VersionType,
            ["${natives_directory}"] = nativesDirectory, ["${launcher_name}"] = "AstraLauncher", ["${launcher_version}"] = "0.1",
            ["${classpath}"] = classPath, ["${classpath_separator}"] = Path.PathSeparator.ToString(), ["${library_directory}"] = librariesDirectory
        };
        var arguments = new List<string> { $"-Xmx{request.MaximumMemoryMiB}M" };
        arguments.AddRange(SelectArguments(metadata.Arguments.Jvm, variables));
        arguments.Add(metadata.MainClass!);
        IEnumerable<string> gameArguments = metadata.Arguments.Game.Count > 0 ? SelectArguments(metadata.Arguments.Game, variables) : SplitLegacyArguments(metadata.LegacyMinecraftArguments, variables);
        arguments.AddRange(gameArguments);
        return new GameLaunchCommand(Path.GetFullPath(request.JavaExecutablePath), arguments, gameDirectory);
    }

    private static void ValidateRequest(MinecraftVersionMetadata metadata, GameLaunchRequest request)
    {
        if (string.IsNullOrWhiteSpace(metadata.MainClass)) throw new GameLaunchValidationException("Version metadata has no mainClass.");
        if (!File.Exists(request.JavaExecutablePath)) throw new GameLaunchValidationException($"Java executable does not exist: {request.JavaExecutablePath}");
        if (request.MaximumMemoryMiB < 256) throw new GameLaunchValidationException("At least 256 MiB of memory is required.");
        if (string.IsNullOrWhiteSpace(request.Session.PlayerName) || string.IsNullOrWhiteSpace(request.Session.PlayerUuid)) throw new GameLaunchValidationException("A player name and UUID are required.");
        if (request.Session.Kind == GameSessionKind.Authenticated && string.IsNullOrWhiteSpace(request.Session.AccessToken)) throw new GameLaunchValidationException("An authenticated session requires an access token.");
        if (request.Session.Kind == GameSessionKind.Offline && request.Session.AccessToken is not null) throw new GameLaunchValidationException("Offline profiles must not carry an access token.");
    }
    private static IReadOnlyList<string> ResolveLibraries(MinecraftVersionMetadata metadata, string librariesDirectory) => metadata.Libraries.Where(library => RuleEvaluator.Allows(library.Rules)).Select(library => library.Downloads?.Artifact?.Path is string path ? Path.Combine(librariesDirectory, path.Replace('/', Path.DirectorySeparatorChar)) : Path.Combine(librariesDirectory, MavenPath(library.Name))).ToArray();
    private static string MavenPath(string coordinate) { string[] segments = coordinate.Split(':'); if (segments.Length < 3) throw new GameLaunchValidationException($"Invalid library coordinate '{coordinate}'."); string group = segments[0].Replace('.', Path.DirectorySeparatorChar); string classifier = segments.Length > 3 ? $"-{segments[3]}" : string.Empty; return Path.Combine(group, segments[1], segments[2], $"{segments[1]}-{segments[2]}{classifier}.jar"); }
    private static void EnsureFiles(IEnumerable<string> paths) { foreach (string path in paths) if (!File.Exists(path)) throw new GameLaunchValidationException($"Required game file is missing: {path}"); }
    private static IEnumerable<string> SelectArguments(IEnumerable<LaunchArgument> arguments, IReadOnlyDictionary<string, string> variables) => arguments.Where(argument => RuleEvaluator.Allows(argument.Rules)).SelectMany(argument => argument.Values).Select(value => Replace(value, variables));
    private static IEnumerable<string> SplitLegacyArguments(string? arguments, IReadOnlyDictionary<string, string> variables) => string.IsNullOrWhiteSpace(arguments) ? [] : CommandLineTokenizer.Tokenize(arguments).Select(value => Replace(value, variables));
    private static string Replace(string value, IReadOnlyDictionary<string, string> variables) { foreach ((string key, string replacement) in variables) value = value.Replace(key, replacement, StringComparison.Ordinal); return value; }

    private static class RuleEvaluator
    {
        public static bool Allows(IReadOnlyList<ArgumentRule> rules)
        {
            if (rules.Count == 0) return true;
            bool allowed = false;
            foreach (ArgumentRule rule in rules) if (Matches(rule)) allowed = rule.Action.Equals("allow", StringComparison.OrdinalIgnoreCase);
            return allowed;
        }
        private static bool Matches(ArgumentRule rule)
        {
            string os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows" : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx" : "linux";
            if (rule.OperatingSystemName is not null && !rule.OperatingSystemName.Equals(os, StringComparison.OrdinalIgnoreCase)) return false;
            return rule.Features.All(feature => !feature.Value);
        }
    }
    private static class CommandLineTokenizer
    {
        public static IReadOnlyList<string> Tokenize(string text)
        {
            var result = new List<string>(); var current = new System.Text.StringBuilder(); bool quoted = false;
            foreach (char character in text) { if (character == '"') { quoted = !quoted; continue; } if (char.IsWhiteSpace(character) && !quoted) { if (current.Length > 0) { result.Add(current.ToString()); current.Clear(); } } else current.Append(character); }
            if (quoted) throw new GameLaunchValidationException("Legacy launch arguments contain an unmatched quote.");
            if (current.Length > 0) result.Add(current.ToString()); return result;
        }
    }
}

public static class NativeLibraryExtractor
{
    public static void Extract(MinecraftVersionMetadata metadata, string librariesDirectory, string destinationDirectory)
    {
        foreach (LibraryDescriptor library in metadata.Libraries.Where(library => GameCommandBuilderRuleCheck(library.Rules)))
        {
            if (!library.Natives.TryGetValue("windows", out string? classifier) || library.Downloads is null || !library.Downloads.Classifiers.TryGetValue(classifier, out DownloadArtifact? artifact) || artifact.Path is null) continue;
            string archive = Path.Combine(librariesDirectory, artifact.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(archive)) throw new GameLaunchValidationException($"Required native library is missing: {archive}");
            using ZipArchive zip = ZipFile.OpenRead(archive);
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name) || entry.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase) || library.ExtractExcludes.Any(exclude => entry.FullName.StartsWith(exclude, StringComparison.Ordinal))) continue;
                string target = Path.GetFullPath(Path.Combine(destinationDirectory, entry.FullName));
                if (!target.StartsWith(Path.GetFullPath(destinationDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new GameLaunchValidationException("Native archive contains an unsafe path.");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!); entry.ExtractToFile(target, overwrite: true);
            }
        }
    }
    private static bool GameCommandBuilderRuleCheck(IReadOnlyList<ArgumentRule> rules) => rules.Count == 0 || string.Equals(rules.LastOrDefault(rule => rule.OperatingSystemName is null || string.Equals(rule.OperatingSystemName, "windows", StringComparison.OrdinalIgnoreCase))?.Action, "allow", StringComparison.OrdinalIgnoreCase);
}
