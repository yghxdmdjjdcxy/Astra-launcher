using AstraLauncher.Core.Models;
using AstraLauncher.Core.Services;
using AstraLauncher.Game.Mods;
using Microsoft.Extensions.Logging;

namespace AstraLauncher.Game.Diagnostics;

/// <summary>
/// Applies only reversible repairs: it can install a missing dependency from Modrinth, disable suspected or duplicate
/// mod jars (rename to <c>.disabled</c>) and raise the configured memory. Java/driver issues are reported as suggestions.
/// </summary>
public sealed class CrashRepairService : ICrashRepairService
{
    private readonly ILocalModService _mods;
    private readonly IModrinthClient _modrinth;
    private readonly ISettingsService _settings;
    private readonly ILauncherPaths _paths;
    private readonly IVersionInstallService? _installService;
    private readonly ILogger<CrashRepairService> _logger;

    public CrashRepairService(
        ILocalModService mods,
        IModrinthClient modrinth,
        ISettingsService settings,
        ILauncherPaths paths,
        ILogger<CrashRepairService> logger,
        IVersionInstallService? installService = null)
    {
        ArgumentNullException.ThrowIfNull(mods);
        ArgumentNullException.ThrowIfNull(modrinth);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(logger);
        _mods = mods;
        _modrinth = modrinth;
        _settings = settings;
        _paths = paths;
        _logger = logger;
        _installService = installService;
    }

    public async Task<CrashRepairResult> RepairAsync(
        GameInstance instance,
        CrashAnalysisResult analysis,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(analysis);
        InstanceModContext context = InstanceModContextResolver.Resolve(_paths, instance.VersionId);
        var actions = new List<CrashRepairAction>();
        IReadOnlyList<LocalMod> installedMods;
        try
        {
            installedMods = await _mods.ListAsync(instance, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new CrashRepairResult([new CrashRepairAction("读取 MOD 列表", false, exception.Message)], "无法读取 MOD 目录，未执行修复。");
        }

        foreach (CrashIssue issue in analysis.Issues)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (issue.Kind)
            {
                case CrashIssueKind.MissingDependency when !string.IsNullOrWhiteSpace(issue.ModId):
                    actions.Add(await InstallMissingDependencyAsync(instance, issue, context, progress, cancellationToken).ConfigureAwait(false));
                    break;
                case CrashIssueKind.SuspectedMod when !string.IsNullOrWhiteSpace(issue.ModId):
                    actions.AddRange(await DisableModsAsync(instance, installedMods, issue.ModId, $"可疑 MOD「{issue.ModId}」", cancellationToken).ConfigureAwait(false));
                    break;
                case CrashIssueKind.DuplicateMods:
                    actions.AddRange(await DisableDuplicatesAsync(instance, installedMods, cancellationToken).ConfigureAwait(false));
                    break;
                case CrashIssueKind.OutOfMemory:
                    actions.Add(await IncreaseMemoryAsync(cancellationToken).ConfigureAwait(false));
                    break;
                case CrashIssueKind.MissingGameFiles:
                    actions.Add(await RepairGameFilesAsync(instance, progress, cancellationToken).ConfigureAwait(false));
                    break;
            }
        }

        if (actions.Count == 0)
        {
            return new CrashRepairResult(actions, "没有可以自动执行的可逆修复操作，请按建议手动处理。");
        }

        string summary = actions.Any(action => action.Success)
            ? $"已执行 {actions.Count(action => action.Success)} 项自动修复（可撤销：禁用的 MOD 可重新启用，新下载的 MOD 可删除，内存可改回）。"
            : "自动修复未能成功执行，请按建议手动处理。";
        _logger.LogInformation("Applied {SuccessCount} crash repair action(s) for instance {Instance}.", actions.Count(action => action.Success), instance.Name);
        return new CrashRepairResult(actions, summary);
    }

    private async Task<CrashRepairAction> InstallMissingDependencyAsync(
        GameInstance instance,
        CrashIssue issue,
        InstanceModContext context,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        string modId = issue.ModId!;
        string description = $"自动安装缺失前置 MOD「{modId}」";
        try
        {
            progress?.Report($"正在 Modrinth 查找「{modId}」…");
            ModrinthSearchResult search = await _modrinth
                .SearchAsync(modId, context.MinecraftVersion, context.ModrinthLoader, limit: 10, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            ModrinthSearchHit? hit = search.Hits.FirstOrDefault(candidate => string.Equals(candidate.Slug, modId, StringComparison.OrdinalIgnoreCase))
                ?? search.Hits.FirstOrDefault();
            if (hit is null)
            {
                return new CrashRepairAction(description, false, "Modrinth 上没有找到适合当前游戏版本/加载器的 MOD。");
            }

            IReadOnlyList<ModrinthVersion> versions = await _modrinth
                .GetVersionsAsync(hit.ProjectId, context.MinecraftVersion, context.ModrinthLoader, cancellationToken)
                .ConfigureAwait(false);
            ModrinthVersion? version = versions.FirstOrDefault();
            if (version is null)
            {
                return new CrashRepairAction(description, false, $"「{hit.Title}」没有兼容 {context.MinecraftVersion} / {context.Platform} 的版本。");
            }

            progress?.Report($"正在下载「{hit.Title}」…");
            string installedPath = await _modrinth
                .DownloadAsync(version, _mods.GetModsDirectory(instance), null, cancellationToken)
                .ConfigureAwait(false);
            return new CrashRepairAction(description, true, $"已安装 {Path.GetFileName(installedPath)}（可从 mods 目录删除以撤销）。");
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "自动安装缺失前置 MOD 失败：{ModId}", modId);
            return new CrashRepairAction(description, false, exception.Message);
        }
    }

    private async Task<IReadOnlyList<CrashRepairAction>> DisableModsAsync(
        GameInstance instance,
        IReadOnlyList<LocalMod> installedMods,
        string modIdOrFile,
        string reason,
        CancellationToken cancellationToken)
    {
        var actions = new List<CrashRepairAction>();
        foreach (LocalMod mod in installedMods.Where(mod => mod.IsEnabled
                     && (string.Equals(mod.ModId, modIdOrFile, StringComparison.OrdinalIgnoreCase)
                         || string.Equals(mod.FileName, modIdOrFile, StringComparison.OrdinalIgnoreCase))))
        {
            try
            {
                await _mods.SetEnabledAsync(instance, mod, enabled: false, cancellationToken).ConfigureAwait(false);
                actions.Add(new CrashRepairAction($"禁用{reason}", true, $"{mod.FileName} → .disabled（可重新启用）"));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                actions.Add(new CrashRepairAction($"禁用{reason}", false, exception.Message));
            }
        }

        return actions;
    }

    private async Task<IReadOnlyList<CrashRepairAction>> DisableDuplicatesAsync(
        GameInstance instance,
        IReadOnlyList<LocalMod> installedMods,
        CancellationToken cancellationToken)
    {
        var actions = new List<CrashRepairAction>();
        foreach (IGrouping<string, LocalMod> group in installedMods.GroupBy(mod => mod.ModId, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
        {
            LocalMod keep = group.FirstOrDefault(mod => mod.IsEnabled) ?? group.First();
            foreach (LocalMod duplicate in group.Where(mod => mod.IsEnabled && !ReferenceEquals(mod, keep)))
            {
                try
                {
                    await _mods.SetEnabledAsync(instance, duplicate, enabled: false, cancellationToken).ConfigureAwait(false);
                    actions.Add(new CrashRepairAction($"禁用重复 MOD「{duplicate.DisplayName}」", true, $"{duplicate.FileName} → .disabled（保留 {keep.FileName}）"));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    actions.Add(new CrashRepairAction($"禁用重复 MOD「{duplicate.DisplayName}」", false, exception.Message));
                }
            }
        }

        return actions;
    }

    private async Task<CrashRepairAction> RepairGameFilesAsync(
        GameInstance instance,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        const string description = "自动补全缺失/损坏的游戏文件";
        if (_installService is null)
        {
            return new CrashRepairAction(description, false, "文件补全服务不可用。");
        }

        try
        {
            progress?.Report("正在校验并补全游戏文件…");
            await _installService.InstallAsync(instance.VersionId, null, cancellationToken).ConfigureAwait(false);
            return new CrashRepairAction(description, true, $"版本 {instance.VersionId} 已重新校验并补全客户端、依赖库与资源文件。");
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "自动补全游戏文件失败：{Version}", instance.VersionId);
            return new CrashRepairAction(description, false, exception.Message);
        }
    }

    private async Task<CrashRepairAction> IncreaseMemoryAsync(CancellationToken cancellationToken)
    {
        try
        {
            LauncherSettings settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
            int previous = settings.MemoryMiB;
            int raised = Math.Clamp(previous + 1024, LauncherSettings.MinimumMemoryMiB, LauncherSettings.MaximumMemoryMiB);
            if (raised == previous)
            {
                return new CrashRepairAction("调大启动内存", false, "内存已经是允许范围内的最大值。");
            }

            settings.MemoryMiB = raised;
            await _settings.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
            return new CrashRepairAction("调大启动内存", true, $"{previous} MiB → {raised} MiB（可在设置页改回）。");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new CrashRepairAction("调大启动内存", false, exception.Message);
        }
    }
}
