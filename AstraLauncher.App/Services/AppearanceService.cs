using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using AstraLauncher.App.Appearance;
using AstraLauncher.Core.Models;
using AstraLauncher.Core.Services;
using Microsoft.Extensions.Logging;

namespace AstraLauncher.App.Services;

/// <summary>Applies user-selected interface appearance settings to the running application.</summary>
public interface IAppearanceService
{
    /// <summary>Gets all progress-bar presets that can be shown in the UI.</summary>
    IReadOnlyList<ProgressBarPreset> ProgressBarPresets { get; }

    /// <summary>Applies a progress-bar preset (unknown keys fall back to the default) and thickness.</summary>
    void ApplyProgressBar(string? styleKey, int height);

    /// <summary>Loads persisted appearance settings and applies them (called at startup).</summary>
    Task ApplySavedAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Mutates the application resource dictionary with dynamic brushes/effects so every open ProgressBar updates
/// immediately without rebuilding the UI.
/// </summary>
public sealed class AppearanceService : IAppearanceService
{
    private readonly ISettingsService _settings;
    private readonly ILogger<AppearanceService> _logger;

    public AppearanceService(ISettingsService settings, ILogger<AppearanceService> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        _settings = settings;
        _logger = logger;
    }

    /// <inheritdoc />
    public IReadOnlyList<ProgressBarPreset> ProgressBarPresets => global::AstraLauncher.App.Appearance.ProgressBarPresets.All;

    /// <inheritdoc />
    public void ApplyProgressBar(string? styleKey, int height)
    {
        ProgressBarPreset preset = global::AstraLauncher.App.Appearance.ProgressBarPresets.Resolve(styleKey);
        if (Application.Current is null)
        {
            return;
        }

        ResourceDictionary resources = Application.Current.Resources;
        resources["ProgressTrackBrush"] = new SolidColorBrush(Parse(preset.Track));
        resources["ProgressFillBrush"] = CreateFillBrush(preset);
        resources["ProgressCornerRadius"] = new CornerRadius(preset.CornerRadius);
        resources["ProgressGlowEffect"] = preset.Glow
            ? new DropShadowEffect
            {
                Color = Parse(preset.FillEnd),
                BlurRadius = 10,
                ShadowDepth = 0,
                Opacity = 0.55,
            }
            : null;
        resources["ProgressBarHeight"] = (double)Math.Clamp(
            height,
            LauncherSettings.MinimumProgressBarHeight,
            LauncherSettings.MaximumProgressBarHeight);
        _logger.LogInformation("Applied progress-bar preset {Preset} (height {Height}).", preset.Key, height);
    }

    /// <inheritdoc />
    public async Task ApplySavedAsync(CancellationToken cancellationToken = default)
    {
        LauncherSettings settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        ApplyProgressBar(settings.ProgressBarStyle, settings.ProgressBarHeight);
    }

    private static Brush CreateFillBrush(ProgressBarPreset preset)
    {
        if (string.Equals(preset.FillStart, preset.FillEnd, StringComparison.OrdinalIgnoreCase))
        {
            return new SolidColorBrush(Parse(preset.FillStart));
        }

        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5),
        };
        brush.GradientStops.Add(new GradientStop(Parse(preset.FillStart), 0));
        brush.GradientStops.Add(new GradientStop(Parse(preset.FillEnd), 1));
        return brush;
    }

    private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);
}
