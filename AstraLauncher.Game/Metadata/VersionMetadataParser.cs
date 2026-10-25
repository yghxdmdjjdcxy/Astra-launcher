using System.Text.Json;

namespace AstraLauncher.Game.Metadata;

public static class VersionMetadataParser
{
    public static async Task<MinecraftVersionMetadata> ParseAsync(Stream stream, string? sourcePath = null, CancellationToken cancellationToken = default)
    {
        try
        {
            using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return Parse(document.RootElement, sourcePath);
        }
        catch (JsonException exception)
        {
            throw new VersionMetadataException("The version metadata is not valid JSON.", sourcePath, exception);
        }
    }

    public static MinecraftVersionMetadata Parse(JsonElement root, string? sourcePath = null)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new VersionMetadataException("The version metadata root must be an object.", sourcePath);
        string id = RequiredString(root, "id", sourcePath);
        string? inheritsFrom = OptionalString(root, "inheritsFrom");
        string? mainClass = OptionalString(root, "mainClass");
        return new MinecraftVersionMetadata(
            id, inheritsFrom, mainClass, ParseAssetIndex(root, sourcePath), ParseJavaVersion(root, sourcePath),
            ParseLibraries(root, sourcePath), ParseArguments(root, sourcePath), OptionalString(root, "minecraftArguments"), ParseDownloads(root, sourcePath));
    }

    private static AssetIndexDescriptor? ParseAssetIndex(JsonElement root, string? sourcePath)
    {
        if (!root.TryGetProperty("assetIndex", out JsonElement value) || value.ValueKind == JsonValueKind.Null) return null;
        return new AssetIndexDescriptor(RequiredString(value, "id", sourcePath), OptionalString(value, "sha1"), OptionalInt64(value, "size"), OptionalInt64(value, "totalSize"), OptionalUri(value, "url", sourcePath));
    }

    private static JavaVersionRequirement? ParseJavaVersion(JsonElement root, string? sourcePath)
    {
        if (!root.TryGetProperty("javaVersion", out JsonElement value) || value.ValueKind == JsonValueKind.Null) return null;
        return new JavaVersionRequirement(RequiredString(value, "component", sourcePath), RequiredInt(value, "majorVersion", sourcePath));
    }

    private static IReadOnlyList<LibraryDescriptor> ParseLibraries(JsonElement root, string? sourcePath)
    {
        if (!root.TryGetProperty("libraries", out JsonElement libraries)) return [];
        if (libraries.ValueKind != JsonValueKind.Array) throw new VersionMetadataException("'libraries' must be an array.", sourcePath);
        return libraries.EnumerateArray().Select(item => new LibraryDescriptor(
            RequiredString(item, "name", sourcePath), ParseLibraryDownloads(item, sourcePath), ParseStringMap(item, "natives", sourcePath), ParseExtractExcludes(item, sourcePath), ParseRules(item, sourcePath))).ToArray();
    }

    private static LibraryDownloads? ParseLibraryDownloads(JsonElement library, string? sourcePath)
    {
        if (!library.TryGetProperty("downloads", out JsonElement downloads)) return null;
        DownloadArtifact? artifact = downloads.TryGetProperty("artifact", out JsonElement value) ? ParseArtifact(value, sourcePath) : null;
        var classifiers = new Dictionary<string, DownloadArtifact>(StringComparer.Ordinal);
        if (downloads.TryGetProperty("classifiers", out JsonElement classifierElement))
        {
            if (classifierElement.ValueKind != JsonValueKind.Object) throw new VersionMetadataException("Library classifiers must be an object.", sourcePath);
            foreach (JsonProperty property in classifierElement.EnumerateObject()) classifiers[property.Name] = ParseArtifact(property.Value, sourcePath);
        }
        return new LibraryDownloads(artifact, classifiers);
    }

    private static IReadOnlyList<string> ParseExtractExcludes(JsonElement library, string? sourcePath)
    {
        if (!library.TryGetProperty("extract", out JsonElement extract) || !extract.TryGetProperty("exclude", out JsonElement excludes)) return [];
        if (excludes.ValueKind != JsonValueKind.Array) throw new VersionMetadataException("Library extract excludes must be an array.", sourcePath);
        return excludes.EnumerateArray().Select(value => value.GetString() ?? throw new VersionMetadataException("Library extract exclude cannot be null.", sourcePath)).ToArray();
    }

    private static VersionArguments ParseArguments(JsonElement root, string? sourcePath) => !root.TryGetProperty("arguments", out JsonElement arguments)
        ? new VersionArguments([], [])
        : new VersionArguments(ParseArgumentList(arguments, "game", sourcePath), ParseArgumentList(arguments, "jvm", sourcePath));

    private static IReadOnlyList<LaunchArgument> ParseArgumentList(JsonElement arguments, string name, string? sourcePath)
    {
        if (!arguments.TryGetProperty(name, out JsonElement values)) return [];
        if (values.ValueKind != JsonValueKind.Array) throw new VersionMetadataException($"arguments.{name} must be an array.", sourcePath);
        return values.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.String
            ? new LaunchArgument([value.GetString()!], [])
            : new LaunchArgument(ParseValues(value, sourcePath), ParseRules(value, sourcePath))).ToArray();
    }

    private static IReadOnlyList<string> ParseValues(JsonElement element, string? sourcePath)
    {
        if (!element.TryGetProperty("value", out JsonElement value)) throw new VersionMetadataException("Argument object has no value.", sourcePath);
        return value.ValueKind switch
        {
            JsonValueKind.String => [value.GetString()!],
            JsonValueKind.Array => value.EnumerateArray().Select(item => item.GetString() ?? throw new VersionMetadataException("Argument value cannot be null.", sourcePath)).ToArray(),
            _ => throw new VersionMetadataException("Argument value must be a string or an array.", sourcePath)
        };
    }

    private static IReadOnlyList<ArgumentRule> ParseRules(JsonElement element, string? sourcePath)
    {
        if (!element.TryGetProperty("rules", out JsonElement rules)) return [];
        if (rules.ValueKind != JsonValueKind.Array) throw new VersionMetadataException("Rules must be an array.", sourcePath);
        return rules.EnumerateArray().Select(rule =>
        {
            JsonElement os = rule.TryGetProperty("os", out JsonElement candidate) ? candidate : default;
            return new ArgumentRule(RequiredString(rule, "action", sourcePath), OptionalString(os, "name"), OptionalString(os, "version"), OptionalString(os, "arch"), ParseBooleanMap(rule, "features", sourcePath));
        }).ToArray();
    }

    private static IReadOnlyDictionary<string, DownloadArtifact> ParseDownloads(JsonElement root, string? sourcePath)
    {
        var result = new Dictionary<string, DownloadArtifact>(StringComparer.Ordinal);
        if (!root.TryGetProperty("downloads", out JsonElement downloads)) return result;
        if (downloads.ValueKind != JsonValueKind.Object) throw new VersionMetadataException("'downloads' must be an object.", sourcePath);
        foreach (JsonProperty property in downloads.EnumerateObject()) result[property.Name] = ParseArtifact(property.Value, sourcePath);
        return result;
    }

    private static DownloadArtifact ParseArtifact(JsonElement value, string? sourcePath) => new(OptionalString(value, "path"), OptionalString(value, "sha1"), OptionalInt64(value, "size"), OptionalUri(value, "url", sourcePath));
    private static IReadOnlyDictionary<string, string> ParseStringMap(JsonElement root, string name, string? sourcePath) => ParseMap(root, name, sourcePath, value => value.GetString() ?? throw new VersionMetadataException($"'{name}' cannot contain null.", sourcePath));
    private static IReadOnlyDictionary<string, bool> ParseBooleanMap(JsonElement root, string name, string? sourcePath) => ParseMap(root, name, sourcePath, value => value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : throw new VersionMetadataException($"'{name}' must contain booleans.", sourcePath));
    private static IReadOnlyDictionary<string, T> ParseMap<T>(JsonElement root, string name, string? sourcePath, Func<JsonElement, T> convert)
    {
        var map = new Dictionary<string, T>(StringComparer.Ordinal);
        if (!root.TryGetProperty(name, out JsonElement values)) return map;
        if (values.ValueKind != JsonValueKind.Object) throw new VersionMetadataException($"'{name}' must be an object.", sourcePath);
        foreach (JsonProperty property in values.EnumerateObject()) map[property.Name] = convert(property.Value);
        return map;
    }
    private static string RequiredString(JsonElement root, string name, string? sourcePath) => OptionalString(root, name) ?? throw new VersionMetadataException($"Required string '{name}' is missing.", sourcePath);
    private static int RequiredInt(JsonElement root, string name, string? sourcePath) => OptionalInt(root, name) ?? throw new VersionMetadataException($"Required integer '{name}' is missing.", sourcePath);
    private static string? OptionalString(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static int? OptionalInt(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int result) ? result : null;
    private static long? OptionalInt64(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out JsonElement value) && value.TryGetInt64(out long result) ? result : null;
    private static Uri? OptionalUri(JsonElement root, string name, string? sourcePath) { string? text = OptionalString(root, name); if (text is null) return null; return Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) ? uri : throw new VersionMetadataException($"'{name}' is not an absolute URI.", sourcePath); }
}
