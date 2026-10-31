using System.Windows.Input;
using AstraLauncher.App.Appearance;
using AstraLauncher.App.Commands;
using AstraLauncher.App.Services;
using AstraLauncher.Core.Models;
using AstraLauncher.Core.Services;
using Microsoft.Extensions.Logging;

namespace AstraLauncher.App.ViewModels;

/// <summary>A selectable progress-bar thickness.</summary>
public sealed record ProgressBarSizeOption(int Value, string Label);

/// <summary>
/// 百宝箱：launcher extras. The first section is 自定义, where the user can change the progress-bar style and
/// thickness; changes are applied immediately and persisted.
/// </summary>
public sealed class ToolboxPageViewModel : PageViewModel
{
    private readonly ISettingsService _settings;
    private readonly IAppearanceService _appearance;
    private readonly ILogger<ToolboxPageViewModel> _logger;
    private ProgressBarPreset? _selectedPreset;
    private ProgressBarSizeOption? _selectedSize;
    private string _statusMessage = "选择样式后会立即生效并保存。";
    private bool _suppressApply;

    public ToolboxPageViewModel(
        ISettingsService settings,
        IAppearanceService appearance,
        ILogger<ToolboxPageViewModel> logger) : base("百宝箱")
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(appearance);
        ArgumentNullException.ThrowIfNull(logger);
        _settings = settings;
        _appearance = appearance;
        _logger = logger;
        ResetCommand = new AsyncRelayCommand(ResetAsync);
        _ = LoadAsync();
    }

    public IReadOnlyList<ProgressBarPreset> ProgressBarPresets => _appearance.ProgressBarPresets;

    public IReadOnlyList<ProgressBarSizeOption> ProgressBarSizes { get; } =
    [
        new(10, "细"),
        new(16, "标准"),
        new(22, "粗"),
    ];

    public ProgressBarPreset? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (SetProperty(ref _selectedPreset, value) && value is not null && !_suppressApply)
            {
                _ = ApplyAsync(value.Key, SelectedSize?.Value ?? LauncherSettings.DefaultProgressBarHeight);
            }
        }
    }

    public ProgressBarSizeOption? SelectedSize
    {
        get => _selectedSize;
        set
        {
            if (SetProperty(ref _selectedSize, value) && value is not null && !_suppressApply)
            {
                _ = ApplyAsync(SelectedPreset?.Key ?? LauncherSettings.DefaultProgressBarStyle, value.Value);
            }
        }
    }

    public int PreviewPercent => 68;

    public ICommand ResetCommand { get; }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    private async Task LoadAsync()
    {
        try
        {
            LauncherSettings settings = await _settings.GetAsync().ConfigureAwait(true);
            _suppressApply = true;
            SelectedPreset = _appearance.ProgressBarPresets.FirstOrDefault(
                preset => string.Equals(preset.Key, settings.ProgressBarStyle, StringComparison.OrdinalIgnoreCase));
            SelectedSize = ProgressBarSizes.FirstOrDefault(option => option.Value == settings.ProgressBarHeight)
                ?? ProgressBarSizes[1];
            _suppressApply = false;
            _appearance.ApplyProgressBar(
                SelectedPreset?.Key ?? LauncherSettings.DefaultProgressBarStyle,
                SelectedSize?.Value ?? LauncherSettings.DefaultProgressBarHeight);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "加载外观设置失败。");
            StatusMessage = "加载外观设置失败：" + exception.Message;
        }
    }

    private async Task ApplyAsync(string styleKey, int height)
    {
        try
        {
            _appearance.ApplyProgressBar(styleKey, height);
            LauncherSettings settings = await _settings.GetAsync().ConfigureAwait(true);
            settings.ProgressBarStyle = styleKey;
            settings.ProgressBarHeight = height;
            await _settings.SaveAsync(settings).ConfigureAwait(true);
            StatusMessage = $"已应用：{ProgressBarPresets.FirstOrDefault(item => item.Key == styleKey)?.Name ?? styleKey} / {height}px";
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "保存外观设置失败。");
            StatusMessage = "保存外观设置失败：" + exception.Message;
        }
    }

    private async Task ResetAsync()
    {
        _suppressApply = true;
        SelectedPreset = _appearance.ProgressBarPresets[0];
        SelectedSize = ProgressBarSizes[1];
        _suppressApply = false;
        await ApplyAsync(LauncherSettings.DefaultProgressBarStyle, LauncherSettings.DefaultProgressBarHeight).ConfigureAwait(true);
    }
}
