using System.Text.Json;
using System.Text.RegularExpressions;
using AstraLauncher.Core.Services;
using AstraLauncher.Game.Mods;

namespace AstraLauncher.Game.Diagnostics;

/// <summary>Resolves the vanilla Minecraft version and loader of a version profile, following inheritsFrom chains.</summary>
public static partial class InstanceModContextResolver
{
    public static InstanceModContext Resolve(ILauncherPaths paths, string versionId)
    {
        ArgumentNullException.ThrowIfNull(paths);
        string current = versionId;
        for (int depth = 0; depth < 8; depth++)
        {
            string path = Path.Combine(paths.VersionsDirectory, current, current + ".json");
            if (!File.Exists(path))
            {
                break;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
                if (!document.RootElement.TryGetProperty("inheritsFrom", out JsonElement inherited) || inherited.ValueKind != JsonValueKind.String)
                {
                    break;
                }

                string parent = inherited.GetString() ?? string.Empty;
                Match parentMatch = VersionNumberRegex().Match(parent);
                if (parentMatch.Success && parentMatch.Value == parent)
                {
                    return new InstanceModContext(parent, DetectPlatform(versionId));
                }

                current = parent;
            }
            catch (Exception exception) when (exception is IOException or JsonException)
            {
                break;
            }
        }

        string minecraftVersion = VersionNumberRegex().Match(versionId) is { Success: true } match
            ? match.Value
            : versionId;
        return new InstanceModContext(minecraftVersion, DetectPlatform(versionId));
    }

    public static ModPlatform DetectPlatform(string versionId)
    {
        if (versionId.Contains("neoforge", StringComparison.OrdinalIgnoreCase))
        {
            return ModPlatform.NeoForge;
        }

        if (versionId.Contains("quilt-loader", StringComparison.OrdinalIgnoreCase))
        {
            return ModPlatform.Quilt;
        }

        if (versionId.Contains("fabric-loader", StringComparison.OrdinalIgnoreCase)
            || versionId.Contains("fabric", StringComparison.OrdinalIgnoreCase))
        {
            return ModPlatform.Fabric;
        }

        if (versionId.Contains("forge", StringComparison.OrdinalIgnoreCase))
        {
            return ModPlatform.Forge;
        }

        return ModPlatform.Fabric;
    }

    [GeneratedRegex(@"\d+\.\d+(?:\.\d+)?")]
    private static partial Regex VersionNumberRegex();
}
