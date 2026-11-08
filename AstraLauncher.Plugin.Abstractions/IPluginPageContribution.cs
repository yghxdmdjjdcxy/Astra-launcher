namespace AstraLauncher.Plugin.Abstractions;

/// <summary>
/// Optional UI contribution from a plugin. The host creates the view on the UI thread and shows it in a
/// launcher-owned window; the object is typically a WPF <c>UserControl</c> from the plugin assembly.
/// </summary>
public interface IPluginPageContribution
{
    /// <summary>Stable id, unique within the plugin.</summary>
    string Id { get; }

    /// <summary>Title shown in the plugin manager.</summary>
    string Title { get; }

    /// <summary>Short description shown next to the launcher button.</summary>
    string Description { get; }

    /// <summary>Creates the view. Called only when the user opens the page.</summary>
    object CreateView();
}
