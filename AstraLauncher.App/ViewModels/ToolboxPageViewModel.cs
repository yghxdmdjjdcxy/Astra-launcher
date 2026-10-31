using System.Diagnostics;
using System.IO;
using System.Net.Http;
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
/// 百宝箱：PCL 风格的实用工具集合。包含今日人品、内存优化、清理垃圾、千万别点彩蛋、回声洞、
/// 自定义文件下载，以及自定义进度条外观。
/// </summary>
public sealed class ToolboxPageViewModel : PageViewModel
{
    private static readonly string[] EchoQuotes =
    [
        "「世界上最遥远的距离，是你在挖钻石，而我在你背后看着你挖。」",
        "「不要害怕苦力怕，它只是想给你一个拥抱——带爆炸的那种。」",
        "「Minecraft 没有 bug，只有特性。」",
        "「当你觉得生活很难时，想想在岩浆里游泳的僵尸。」",
        "「今天也要记得备份存档。」",
        "「红石的本质，是用逻辑换掉你的睡眠。」",
        "「为什么玩家不怕鬼？因为他们有附魔金苹果。」",
    ];

    private static readonly string[] DoNotClickMessages =
    [
        "都说了千万别点！",
        "你怎么又点了？",
        "再点真的会出事……",
        "好吧，其实不会出事。但你已经浪费了 4 次点击。",
        "成就解锁：手贱の魂。",
        "再点我就……给你讲个冷笑话：为什么 Minecraft 玩家不怕鬼？因为他们有附魔金苹果。",
        "你看，你还在点。",
        "PCL 的作者说千万别点，然后所有玩家都点了。",
        "恭喜，你离「百折不挠」只差一次点击。",
        "你获得了称号：『百折不挠』。但这里真的没有隐藏功能了。",
    ];

    private readonly ISettingsService _settings;
    private readonly IAppearanceService _appearance;
    private readonly ILogger<ToolboxPageViewModel> _logger;
    private readonly IDialogService? _dialogs;
    private readonly ILauncherPaths? _paths;
    private readonly AstraLauncher.Authentication.IAccountService? _accounts;
    private readonly HttpClient _http = new();
    private ProgressBarPreset? _selectedPreset;
    private ProgressBarSizeOption? _selectedSize;
    private string _statusMessage = "选择样式后会立即生效并保存。";
    private bool _suppressApply;
    private int _doNotClickCount;
    private string _downloadUrl = string.Empty;
    private string _downloadDirectory;
    private string _downloadFileName = string.Empty;
    private string _downloadStatus = string.Empty;
    private double _downloadProgress;
    private bool _isDownloading;

    public ToolboxPageViewModel(
        ISettingsService settings,
        IAppearanceService appearance,
        ILogger<ToolboxPageViewModel> logger,
        IDialogService? dialogs = null,
        ILauncherPaths? paths = null,
        AstraLauncher.Authentication.IAccountService? accounts = null) : base("百宝箱")
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(appearance);
        ArgumentNullException.ThrowIfNull(logger);
        _settings = settings;
        _appearance = appearance;
        _logger = logger;
        _dialogs = dialogs;
        _paths = paths;
        _accounts = accounts;
        _downloadDirectory = Path.Combine(
            paths?.MinecraftDirectory ?? Path.GetTempPath(),
            "AstraLauncher",
            "Downloads");
        ResetCommand = new AsyncRelayCommand(ResetAsync);
        DailyLuckCommand = new AsyncRelayCommand(DailyLuckAsync);
        OptimizeMemoryCommand = new AsyncRelayCommand(OptimizeMemoryAsync);
        CleanJunkCommand = new AsyncRelayCommand(CleanJunkAsync);
        DoNotClickCommand = new AsyncRelayCommand(DoNotClickAsync);
        EchoCaveCommand = new RelayCommand(_ => ShowRandomEcho());
        BrowseDownloadFolderCommand = new RelayCommand(_ => BrowseDownloadFolder());
        OpenDownloadFolderCommand = new RelayCommand(_ => OpenDownloadFolder());
        StartDownloadCommand = new AsyncRelayCommand(StartDownloadAsync);
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

    public ICommand DailyLuckCommand { get; }

    public ICommand OptimizeMemoryCommand { get; }

    public ICommand CleanJunkCommand { get; }

    public ICommand DoNotClickCommand { get; }

    public ICommand EchoCaveCommand { get; }

    public ICommand BrowseDownloadFolderCommand { get; }

    public ICommand OpenDownloadFolderCommand { get; }

    public ICommand StartDownloadCommand { get; }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string DownloadUrl
    {
        get => _downloadUrl;
        set => SetProperty(ref _downloadUrl, value);
    }

    public string DownloadDirectory
    {
        get => _downloadDirectory;
        set => SetProperty(ref _downloadDirectory, value);
    }

    public string DownloadFileName
    {
        get => _downloadFileName;
        set => SetProperty(ref _downloadFileName, value);
    }

    public string DownloadStatus
    {
        get => _downloadStatus;
        private set => SetProperty(ref _downloadStatus, value);
    }

    public double DownloadProgress
    {
        get => _downloadProgress;
        private set => SetProperty(ref _downloadProgress, value);
    }

    public bool IsDownloading
    {
        get => _isDownloading;
        private set => SetProperty(ref _isDownloading, value);
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

    private async Task DailyLuckAsync()
    {
        string player = "Player";
        try
        {
            LauncherSettings settings = await _settings.GetAsync().ConfigureAwait(true);
            player = string.IsNullOrWhiteSpace(settings.OfflinePlayerName) ? "Player" : settings.OfflinePlayerName!;
            if (_accounts is not null)
            {
                var selected = await _accounts.GetSelectedAsync().ConfigureAwait(true);
                if (selected is not null)
                {
                    player = selected.PlayerName;
                }
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "读取玩家名失败，今日人品使用默认名。");
        }

        DateTime today = DateTime.Today;
        int seed = HashCode.Combine(today.Year, today.Month, today.Day, player.ToLowerInvariant());
        int value = new Random(seed).Next(0, 101);
        string comment = value switch
        {
            <= 20 => "大凶：建议今天只挖煤，不挖钻石。",
            <= 40 => "小凶：出门记得带水桶。",
            <= 60 => "平：普通的一天，适合挖矿。",
            <= 80 => "小吉：适合下矿，注意别掉岩浆。",
            <= 95 => "大吉：今天宜合成分解，忌苦力怕。",
            _ => "欧皇：今天你甚至可能一发入魂。",
        };
        ShowInfo($"今日人品：{value} / 100\n{comment}\n\n玩家：{player}\n（每天 0 点刷新）", "今日人品");
    }

    private async Task OptimizeMemoryAsync()
    {
        long totalBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        int totalMiB = totalBytes > 0 ? (int)(totalBytes / (1024 * 1024)) : 8192;
        int recommended = Math.Clamp(totalMiB / 2, 2048, 8192);
        recommended = (recommended / 512) * 512;
        try
        {
            LauncherSettings settings = await _settings.GetAsync().ConfigureAwait(true);
            int previous = settings.MemoryMiB;
            settings.MemoryMiB = recommended;
            settings.Normalize();
            await _settings.SaveAsync(settings).ConfigureAwait(true);
            ShowInfo(
                $"内存优化完成。\n\n物理内存约 {totalMiB / 1024.0:0.0} GB\n建议分配：{settings.MemoryMiB} MB（原 {previous} MB）\n\n已保存到设置，下次启动游戏生效。",
                "内存优化");
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "内存优化失败。");
            ShowError("内存优化失败：" + exception.Message);
        }
    }

    private async Task CleanJunkAsync()
    {
        var files = new List<FileInfo>();
        if (_paths is not null)
        {
            AddFiles(Path.Combine(_paths.MinecraftDirectory, "crash-reports"), files);
            AddFiles(Path.Combine(_paths.MinecraftDirectory, "logs"), files);
            AddFiles(_paths.CacheDirectory, files);
            AddFiles(_paths.TempDirectory, files);
        }

        if (files.Count == 0)
        {
            ShowInfo("没有找到可清理的日志、崩溃报告或缓存文件。", "清理游戏垃圾");
            return;
        }

        long totalBytes = files.Sum(file => file.Length);
        bool confirmed = _dialogs?.Confirm(
            $"将清理 {files.Count} 个日志 / 崩溃报告 / 缓存文件，约 {FormatSize(totalBytes)}。\n\n不会删除存档、模组和游戏本体。建议先关闭正在运行的游戏。\n\n确认继续？",
            "清理游戏垃圾") ?? true;
        if (!confirmed)
        {
            return;
        }

        int deleted = 0;
        long freed = 0;
        foreach (FileInfo file in files)
        {
            try
            {
                long length = file.Length;
                file.Delete();
                deleted++;
                freed += length;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Files locked by a running game are skipped silently.
            }
        }

        ShowInfo($"已清理 {deleted} 个文件，释放 {FormatSize(freed)}。", "清理游戏垃圾");
    }

    private void AddFiles(string directory, List<FileInfo> files)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        try
        {
            files.AddRange(new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "扫描垃圾文件失败：{Directory}", directory);
        }
    }

    private readonly List<FileInfo> filesCache = [];

    private async Task DoNotClickAsync()
    {
        _doNotClickCount++;
        string message = _doNotClickCount <= DoNotClickMessages.Length
            ? DoNotClickMessages[_doNotClickCount - 1]
            : $"你已经点了 {_doNotClickCount} 次了。这里真的没有隐藏功能了，去玩 Minecraft 吧。";
        if (_doNotClickCount is 3 or 5 or 7)
        {
            await ShakeWindowAsync().ConfigureAwait(true);
        }

        ShowInfo(message, "千万别点");
    }

    private void ShowRandomEcho()
    {
        string quote = EchoQuotes[Random.Shared.Next(EchoQuotes.Length)];
        ShowInfo(quote, "回声洞");
    }

    private async Task ShakeWindowAsync()
    {
        var window = System.Windows.Application.Current?.MainWindow;
        if (window is null)
        {
            return;
        }

        double left = window.Left;
        double top = window.Top;
        try
        {
            for (int index = 0; index < 6; index++)
            {
                window.Left = left + (Random.Shared.NextDouble() * 16 - 8);
                window.Top = top + (Random.Shared.NextDouble() * 12 - 6);
                await Task.Delay(40).ConfigureAwait(true);
            }
        }
        finally
        {
            window.Left = left;
            window.Top = top;
        }
    }

    private void BrowseDownloadFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择下载保存文件夹",
            InitialDirectory = Directory.Exists(DownloadDirectory) ? DownloadDirectory : null,
        };
        if (dialog.ShowDialog() == true)
        {
            DownloadDirectory = dialog.FolderName;
        }
    }

    private void OpenDownloadFolder()
    {
        try
        {
            Directory.CreateDirectory(DownloadDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{DownloadDirectory}\"") { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            DownloadStatus = "无法打开文件夹：" + exception.Message;
        }
    }

    private async Task StartDownloadAsync()
    {
        if (IsDownloading)
        {
            return;
        }

        if (!Uri.TryCreate(DownloadUrl.Trim(), UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            DownloadStatus = "下载地址无效：仅支持 http / https 链接。";
            return;
        }

        string partPath = string.Empty;
        try
        {
            Directory.CreateDirectory(DownloadDirectory);
            string fileName = string.IsNullOrWhiteSpace(DownloadFileName)
                ? Path.GetFileName(uri.LocalPath)
                : DownloadFileName.Trim();
            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = "download.bin";
            }

            char[] invalid = Path.GetInvalidFileNameChars();
            fileName = string.Concat(fileName.Select(character => invalid.Contains(character) ? '_' : character));
            string targetPath = Path.Combine(DownloadDirectory, fileName);
            partPath = targetPath + ".part";

            IsDownloading = true;
            DownloadProgress = 0;
            DownloadStatus = "正在连接…";
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(true);
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? -1;
            await using (Stream input = await response.Content.ReadAsStreamAsync().ConfigureAwait(true))
            await using (var output = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                byte[] buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await input.ReadAsync(buffer).ConfigureAwait(true)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(true);
                    received += read;
                    if (total > 0)
                    {
                        DownloadProgress = (double)received / total * 100;
                        DownloadStatus = $"正在下载… {FormatSize(received)} / {FormatSize(total)}";
                    }
                    else
                    {
                        DownloadStatus = $"正在下载… {FormatSize(received)}";
                    }
                }
            }

            File.Move(partPath, targetPath, overwrite: true);
            DownloadProgress = 100;
            DownloadStatus = "下载完成：" + targetPath;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException or UriFormatException)
        {
            _logger.LogWarning(exception, "自定义文件下载失败：{Url}", DownloadUrl);
            DownloadStatus = "下载失败：" + exception.Message;
            try
            {
                if (!string.IsNullOrEmpty(partPath) && File.Exists(partPath))
                {
                    File.Delete(partPath);
                }
            }
            catch (IOException)
            {
                // Leave the partial file for inspection.
            }
        }
        finally
        {
            IsDownloading = false;
        }
    }

    private void ShowInfo(string message, string title)
    {
        if (_dialogs is not null)
        {
            _dialogs.ShowInfo(message, title);
        }
        else
        {
            System.Windows.MessageBox.Show(message, title, System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }
    }

    private void ShowError(string message)
    {
        if (_dialogs is not null)
        {
            _dialogs.ShowError(message);
        }
        else
        {
            System.Windows.MessageBox.Show(message, "Astra Launcher", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
        {
            return $"{bytes / (1024.0 * 1024 * 1024):0.0} GB";
        }

        if (bytes >= 1024L * 1024)
        {
            return $"{bytes / (1024.0 * 1024):0.0} MB";
        }

        if (bytes >= 1024)
        {
            return $"{bytes / 1024.0:0.0} KB";
        }

        return bytes + " B";
    }
}
