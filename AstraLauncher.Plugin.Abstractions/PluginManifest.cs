using System.Text.Json;

namespace AstraLauncher.Plugin.Abstractions;

/// <summary>
/// Version of the plugin contract. A plugin declaring a different major value is refused by the loader
/// instead of failing later with a missing-method exception.
/// </summary>
public static class PluginApi
{
    public const int Version = 1;

    /// <summary>Options used to read <c>plugin.json</c>; shared so host and tooling parse identically.</summary>
    public static JsonSerializerOptions ManifestJsonOptions { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}

/// <summary>
/// On-disk <c>plugin.json</c> manifest. This type lives in the frozen contract assembly so the host and
/// third-party plugins always agree on the manifest schema.
/// </summary>
public sealed class PluginManifest
{
    /// <summary>Stable, unique identifier. Only letters, digits, '.', '_' and '-' are accepted by the loader.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Human-readable name shown in the plugin list.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Informational plugin version.</summary>
    public string Version { get; set; } = "1.0.0";

    /// <summary>Path of the entry DLL, relative to the plugin directory (for example <c>MyPlugin.dll</c>).</summary>
    public string EntryAssembly { get; set; } = string.Empty;

    /// <summary>Must equal <see cref="PluginApi.Version"/>.</summary>
    public int ApiVersion { get; set; } = PluginApi.Version;

    /// <summary>A disabled plugin stays on disk but is never loaded.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Optional lowercase SHA-256 of the entry DLL. When present the loader verifies it before loading.</summary>
    public string? Sha256 { get; set; }

    public string? Description { get; set; }

    public string? Author { get; set; }

    /// <summary>Optional GitHub repository in <c>owner/name</c> form; used by the market and updater.</summary>
    public string? Repository { get; set; }

    /// <summary>Optional project homepage / release page.</summary>
    public string? Homepage { get; set; }
}
