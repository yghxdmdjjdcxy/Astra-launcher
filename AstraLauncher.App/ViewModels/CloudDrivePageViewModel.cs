using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows.Input;
using AstraLauncher.App.Commands;
using AstraLauncher.App.Services;
using AstraLauncher.Core.Models;
using AstraLauncher.Core.Services;
using AstraLauncher.Game.Instances;
using Microsoft.Extensions.Logging;

namespace AstraLauncher.App.ViewModels;

/// <summary>One of the four fixed cloud-drive game-version slots.</summary>
public sealed class CloudDriveSlot : ObservableObject
{
    private string _remoteInfo = "未上传";
    private GameInstance? _selectedInstance;

    public CloudDriveSlot(int number)
    {
        Number = number;
        RemoteName = $"astra-slot{number}.zip";
    }

    public int Number { get; }

    public string RemoteName { get; }

    public string Title => $"游戏版本槽位 {Number}";

    public string RemoteInfo
    {
        get => _remoteInfo;
        set => SetProperty(ref _remoteInfo, value);
    }

    public GameInstance? SelectedInstance
    {
        get => _selectedInstance;
        set => SetProperty(ref _selectedInstance, value);
    }
}

/// <summary>
/// 云盘：登录 Cloudreve（WebDAV），为每个用户保留 4 个游戏版本槽位。上传时把实例的模组、配置和存档
/// 自动压缩成一个 zip；下载时解压并重新登记为本地实例。
/// </summary>
public sealed class CloudDrivePageViewModel : PageViewModel
{
    private readonly ICloudDriveService _cloud;
    private readonly IGameInstanceRepository _instances;
    private readonly ILauncherPaths _paths;
    private readonly ISettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly ILogger<CloudDrivePageViewModel> _logger;
    private string _serverUrl;
    private string _email;
    private string _password = string.Empty;
    private bool _isConnected;
    private string _statusMessage = "填写 Cloudreve WebDAV 地址与账号后连接。";

    public CloudDrivePageViewModel(
        ICloudDriveService cloud,
        IGameInstanceRepository instances,
        ILauncherPaths paths,
        ISettingsService settings,
        IDialogService dialogs,
        ILogger<CloudDrivePageViewModel> logger) : base("云盘")
    {
        ArgumentNullException.ThrowIfNull(cloud);
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(logger);
        _cloud = cloud;
        _instances = instances;
        _paths = paths;
        _settings = settings;
        _dialogs = dialogs;
        _logger = logger;
        _serverUrl = "http://127.0.0.1:5212/dav/";
        _email = string.Empty;
        for (int index = 1; index <= 4; index++)
        {
            Slots.Add(new CloudDriveSlot(index));
        }

        ConnectCommand = new AsyncRelayCommand(ConnectAsync);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        UploadSlotCommand = new RelayCommand(parameter =>
        {
            if (parameter is CloudDriveSlot slot)
            {
                _ = UploadSlotAsync(slot);
            }
        });
        DownloadSlotCommand = new RelayCommand(parameter =>
        {
            if (parameter is CloudDriveSlot slot)
            {
                _ = DownloadSlotAsync(slot);
            }
        });
        _ = InitializeAsync();
    }

    public ObservableCollection<CloudDriveSlot> Slots { get; } = [];

    public ObservableCollection<GameInstance> Instances { get; } = [];

    public ICommand ConnectCommand { get; }

    public ICommand RefreshCommand { get; }

    public ICommand UploadSlotCommand { get; }

    public ICommand DownloadSlotCommand { get; }

    public string ServerUrl
    {
        get => _serverUrl;
        set => SetProperty(ref _serverUrl, value);
    }

    public string Email
    {
        get => _email;
        set => SetProperty(ref _email, value);
    }

    public string Password
    {
        get => _password;
        set => SetProperty(ref _password, value);
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set => SetProperty(ref _isConnected, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    private async Task InitializeAsync()
    {
        try
        {
            LauncherSettings settings = await _settings.GetAsync().ConfigureAwait(true);
            ServerUrl = settings.CloudDriveUrl ?? ServerUrl;
            Email = settings.CloudDriveEmail ?? Email;

            IReadOnlyList<GameInstance> instances = await _instances.ListAsync().ConfigureAwait(true);
            Instances.Clear();
            foreach (GameInstance instance in instances)
            {
                Instances.Add(instance);
            }

            foreach (CloudDriveSlot slot in Slots)
            {
                slot.SelectedInstance = Instances.FirstOrDefault();
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "加载云盘实例列表失败。");
            StatusMessage = "加载实例失败：" + exception.Message;
        }
    }

    private async Task ConnectAsync()
    {
        if (string.IsNullOrWhiteSpace(ServerUrl) || string.IsNullOrWhiteSpace(Email))
        {
            StatusMessage = "请填写 WebDAV 地址和账号。";
            return;
        }

        StatusMessage = "正在连接 Cloudreve…";
        try
        {
            await _cloud.TestAsync(ServerUrl.Trim(), Email.Trim(), Password).ConfigureAwait(true);
            IsConnected = true;
            LauncherSettings settings = await _settings.GetAsync().ConfigureAwait(true);
            settings.CloudDriveUrl = ServerUrl.Trim();
            settings.CloudDriveEmail = Email.Trim();
            await _settings.SaveAsync(settings).ConfigureAwait(true);
            StatusMessage = "连接成功。";
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (UnauthorizedAccessException exception)
        {
            IsConnected = false;
            StatusMessage = exception.Message;
            _dialogs.ShowError(StatusMessage, "云盘登录");
        }
        catch (Exception exception)
        {
            IsConnected = false;
            _logger.LogError(exception, "Cloudreve 连接失败。");
            StatusMessage = "连接失败：" + exception.Message;
            _dialogs.ShowError(StatusMessage, "云盘登录");
        }
    }

    private async Task RefreshAsync()
    {
        if (!IsConnected)
        {
            StatusMessage = "请先连接云盘。";
            return;
        }

        StatusMessage = "正在读取云盘槽位…";
        try
        {
            IReadOnlyList<CloudDriveEntry> entries = await _cloud
                .ListAsync(ServerUrl.Trim(), Email.Trim(), Password)
                .ConfigureAwait(true);
            foreach (CloudDriveSlot slot in Slots)
            {
                CloudDriveEntry? entry = entries.FirstOrDefault(item => string.Equals(item.Name, slot.RemoteName, StringComparison.OrdinalIgnoreCase));
                slot.RemoteInfo = entry is null
                    ? "未上传"
                    : $"{FormatSize(entry.Size)} · {entry.Modified?.ToLocalTime():yyyy-MM-dd HH:mm}";
            }

            StatusMessage = "云盘槽位已刷新。";
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "读取云盘槽位失败。");
            StatusMessage = "读取槽位失败：" + exception.Message;
        }
    }

    private async Task UploadSlotAsync(CloudDriveSlot slot)
    {
        if (!IsConnected)
        {
            StatusMessage = "请先连接云盘。";
            return;
        }

        if (slot.SelectedInstance is null)
        {
            StatusMessage = "请选择要上传的实例。";
            return;
        }

        string fileName = slot.RemoteName;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "AstraLauncher", "Cloud");
        Directory.CreateDirectory(tempDirectory);
        string zipPath = Path.Combine(tempDirectory, fileName);
        try
        {
            StatusMessage = $"正在压缩「{slot.SelectedInstance.Name}」…";
            CreateArchive(slot.SelectedInstance, zipPath);
            StatusMessage = $"正在上传到槽位 {slot.Number}…";
            await _cloud.UploadAsync(
                ServerUrl.Trim(),
                Email.Trim(),
                Password,
                zipPath,
                fileName,
                new Progress<string>(message => StatusMessage = message)).ConfigureAwait(true);
            StatusMessage = $"槽位 {slot.Number} 上传完成。";
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "上传云盘槽位失败。");
            StatusMessage = "上传失败：" + exception.Message;
            _dialogs.ShowError(StatusMessage, "云盘上传");
        }
        finally
        {
            TryDelete(zipPath);
        }
    }

    private async Task DownloadSlotAsync(CloudDriveSlot slot)
    {
        if (!IsConnected)
        {
            StatusMessage = "请先连接云盘。";
            return;
        }

        if (string.Equals(slot.RemoteInfo, "未上传", StringComparison.Ordinal))
        {
            StatusMessage = $"槽位 {slot.Number} 还没有上传版本。";
            return;
        }

        string tempDirectory = Path.Combine(Path.GetTempPath(), "AstraLauncher", "Cloud");
        Directory.CreateDirectory(tempDirectory);
        string zipPath = Path.Combine(tempDirectory, slot.RemoteName);
        string targetDirectory = Path.Combine(_paths.InstancesDirectory, $"cloud-slot{slot.Number}-{DateTime.Now:yyyyMMddHHmmss}");
        try
        {
            StatusMessage = $"正在下载槽位 {slot.Number}…";
            await _cloud.DownloadAsync(
                ServerUrl.Trim(),
                Email.Trim(),
                Password,
                slot.RemoteName,
                zipPath,
                new Progress<string>(message => StatusMessage = message)).ConfigureAwait(true);
            StatusMessage = "下载完成，正在解压…";
            Directory.CreateDirectory(targetDirectory);
            ZipFile.ExtractToDirectory(zipPath, targetDirectory, overwriteFiles: true);
            (string name, string versionId) = ReadManifest(targetDirectory, slot.Number);
            GameInstance instance = await _instances.ImportAsync($"{name}（云盘）", versionId, targetDirectory).ConfigureAwait(true);
            Instances.Add(instance);
            StatusMessage = $"已恢复为本地实例：{instance.Name}";
            _dialogs.ShowInfo($"槽位 {slot.Number} 已恢复为本地实例「{instance.Name}」。", "云盘下载");
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "下载云盘槽位失败。");
            StatusMessage = "下载失败：" + exception.Message;
            _dialogs.ShowError(StatusMessage, "云盘下载");
            TryDeleteDirectory(targetDirectory);
        }
        finally
        {
            TryDelete(zipPath);
        }
    }

    private static void CreateArchive(GameInstance instance, string zipPath)
    {
        TryDelete(zipPath);
        using ZipArchive archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        string[] directories = ["mods", "config", "saves", "resourcepacks", "shaderpacks", "scripts", "defaultconfigs", "datapacks"];
        foreach (string directory in directories)
        {
            string fullDirectory = Path.Combine(instance.DirectoryPath, directory);
            if (!Directory.Exists(fullDirectory))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(fullDirectory, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(instance.DirectoryPath, file).Replace('\\', '/');
                archive.CreateEntryFromFile(file, relative, CompressionLevel.Optimal);
            }
        }

        string[] looseFiles = ["options.txt", "servers.dat", "optionsof.txt", "iris.properties"];
        foreach (string file in looseFiles)
        {
            string fullPath = Path.Combine(instance.DirectoryPath, file);
            if (File.Exists(fullPath))
            {
                archive.CreateEntryFromFile(fullPath, file, CompressionLevel.Optimal);
            }
        }

        ZipArchiveEntry manifest = archive.CreateEntry("astra-instance.json");
        using Stream stream = manifest.Open();
        using var writer = new StreamWriter(stream);
        writer.Write(JsonSerializer.Serialize(new
        {
            name = instance.Name,
            versionId = instance.VersionId,
            createdAt = DateTimeOffset.UtcNow,
        }));
    }

    private static (string Name, string VersionId) ReadManifest(string directory, int slot)
    {
        string manifestPath = Path.Combine(directory, "astra-instance.json");
        if (!File.Exists(manifestPath))
        {
            return ($"云盘槽位 {slot}", "1.21.1");
        }

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        string name = document.RootElement.TryGetProperty("name", out JsonElement nameElement) ? nameElement.GetString() ?? "云盘实例" : "云盘实例";
        string versionId = document.RootElement.TryGetProperty("versionId", out JsonElement versionElement)
            ? versionElement.GetString() ?? "1.21.1"
            : "1.21.1";
        File.Delete(manifestPath);
        return (name, versionId);
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):0.0} GB",
        >= 1024L * 1024 => $"{bytes / (1024.0 * 1024):0.0} MB",
        >= 1024 => $"{bytes / 1024.0:0.0} KB",
        _ => bytes + " B",
    };

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort.
        }
    }
}
