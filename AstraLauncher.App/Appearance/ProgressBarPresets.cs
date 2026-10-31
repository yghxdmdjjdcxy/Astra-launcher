namespace AstraLauncher.App.Appearance;

/// <summary>A selectable progress-bar preset. Colors are hex strings so the presets stay serialization-free.</summary>
public sealed record ProgressBarPreset(
    string Key,
    string Name,
    string Description,
    string FillStart,
    string FillEnd,
    string Track,
    double CornerRadius,
    bool Glow);

/// <summary>Built-in progress-bar presets offered by the 百宝箱 → 自定义 page.</summary>
public static class ProgressBarPresets
{
    public static IReadOnlyList<ProgressBarPreset> All { get; } =
    [
        new("classic", "经典蓝", "默认样式：纯蓝填充、柔和圆角", "#2563EB", "#2563EB", "#E2E8F0", 6, false),
        new("neon", "霓虹绿", "青绿渐变 + 发光边缘", "#22D3A6", "#00C9FF", "#E6FFFA", 999, true),
        new("sunset", "日落橙", "橙粉渐变、圆润饱满", "#F97316", "#EC4899", "#FFF1E6", 10, false),
        new("aurora", "极光紫", "紫青渐变、极客风", "#8B5CF6", "#22D3A6", "#F3E8FF", 8, true),
        new("minimal", "极简灰", "细窄、直角、低干扰", "#0F172A", "#334155", "#F1F5F9", 2, false),
    ];

    public static ProgressBarPreset Resolve(string? key) =>
        All.FirstOrDefault(preset => string.Equals(preset.Key, key, StringComparison.OrdinalIgnoreCase))
        ?? All[0];
}
