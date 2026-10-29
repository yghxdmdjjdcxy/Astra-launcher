using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AstraLauncher.Core.Models;
using Microsoft.Extensions.Logging;

namespace AstraLauncher.Game.Diagnostics;

/// <summary>
/// Scans <c>logs/latest.log</c> and the newest <c>crash-reports/*.txt</c> for common Minecraft crash patterns and turns
/// them into user-facing causes. The analyzer is deliberately defensive: unreadable or missing files simply produce no
/// analysis instead of failing the launcher.
/// </summary>
public sealed partial class CrashAnalyzer : ICrashAnalyzer
{
    private const int MaximumLogBytes = 4 * 1024 * 1024;
    private const int MaximumExcerptLength = 3000;

    private readonly ILogger<CrashAnalyzer> _logger;

    public CrashAnalyzer(ILogger<CrashAnalyzer> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public Task<CrashAnalysisResult?> AnalyzeAsync(
        GameInstance instance,
        int exitCode,
        DateTimeOffset? notBefore = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (string.IsNullOrWhiteSpace(instance.DirectoryPath) || !Directory.Exists(instance.DirectoryPath))
        {
            return Task.FromResult<CrashAnalysisResult?>(null);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? latestLogPath = Path.Combine(instance.DirectoryPath, "logs", "latest.log");
            if (!File.Exists(latestLogPath) || (notBefore is DateTimeOffset threshold && File.GetLastWriteTimeUtc(latestLogPath) < threshold.UtcDateTime))
            {
                latestLogPath = null;
            }

            string? crashReportPath = FindNewestCrashReport(instance.DirectoryPath, notBefore);
            if (latestLogPath is null && crashReportPath is null)
            {
                return Task.FromResult<CrashAnalysisResult?>(null);
            }

            string logText = latestLogPath is null ? string.Empty : ReadTail(latestLogPath);
            string reportText = crashReportPath is null ? string.Empty : ReadTail(crashReportPath);
            string combined = logText + "\n" + reportText;
            if (string.IsNullOrWhiteSpace(combined))
            {
                return Task.FromResult<CrashAnalysisResult?>(null);
            }

            // A clean run without crash evidence should stay silent. The game can exit with code 0 after showing a
            // mod-loading crash screen, so crash markers (or a crash report) also count when the exit code is zero.
            if (exitCode == 0 && crashReportPath is null && !ContainsCrashEvidence(combined))
            {
                return Task.FromResult<CrashAnalysisResult?>(null);
            }

            var issues = new List<CrashIssue>();
            DetectMissingDependencies(combined, issues);
            DetectDuplicateMods(combined, issues);
            DetectSuspectedMods(reportText.Length > 0 ? reportText : combined, issues);
            DetectJavaVersion(combined, issues);
            DetectOutOfMemory(combined, issues);
            DetectGraphics(combined, issues);
            DetectLoaderErrors(combined, issues);

            if (issues.Count == 0)
            {
                issues.Add(new CrashIssue(
                    CrashIssueKind.Unknown,
                    "游戏异常退出",
                    $"游戏进程以退出码 {exitCode} 结束，日志中没有识别到已知的崩溃模式。",
                    "查看游戏日志和 crash-report 了解详细信息；也可以尝试禁用最近安装的 MOD 后重试。"));
            }

            var uniqueIssues = issues
                .GroupBy(issue => (issue.Kind, issue.ModId, issue.ModFile, issue.RequiredBy))
                .Select(group => group.First())
                .ToArray();
            string summary = BuildSummary(uniqueIssues, exitCode);
            string excerpt = BuildExcerpt(reportText, logText);
            _logger.LogInformation("Analyzed failed game run for {Instance}: {IssueCount} issue(s) detected.", instance.Name, uniqueIssues.Length);
            return Task.FromResult<CrashAnalysisResult?>(new CrashAnalysisResult(
                uniqueIssues,
                summary,
                latestLogPath,
                crashReportPath,
                excerpt));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(exception, "分析崩溃日志失败：{Instance}", instance.Name);
            return Task.FromResult<CrashAnalysisResult?>(null);
        }
    }

    private static void DetectMissingDependencies(string text, List<CrashIssue> issues)
    {
        foreach (Match match in MissingDependencyRegex().Matches(text))
        {
            string modId = match.Groups[1].Value.Trim();
            string requiredBy = match.Groups[2].Value.Trim();
            string range = match.Groups[3].Value.Trim();
            if (issues.Any(issue => issue.Kind == CrashIssueKind.MissingDependency && issue.ModId == modId && issue.RequiredBy == requiredBy))
            {
                continue;
            }

            issues.Add(new CrashIssue(
                CrashIssueKind.MissingDependency,
                $"缺少前置 MOD：{modId}",
                $"MOD「{requiredBy}」要求安装「{modId}」{FormatRange(range)}，但 MOD 目录中缺少它。",
                $"安装「{modId}」后重试；也可以先禁用需要它的 MOD「{requiredBy}」。",
                ModId: modId,
                RequiredBy: requiredBy,
                VersionRange: string.IsNullOrWhiteSpace(range) ? null : range,
                CanAutoFix: true));
        }

        // Fabric/Quilt style: "requires ... of fabric-api, which is missing!"
        foreach (Match match in FabricMissingDependencyRegex().Matches(text))
        {
            string modId = match.Groups[1].Value.Trim();
            if (issues.Any(issue => issue.Kind == CrashIssueKind.MissingDependency && issue.ModId == modId))
            {
                continue;
            }

            issues.Add(new CrashIssue(
                CrashIssueKind.MissingDependency,
                $"缺少前置 MOD：{modId}",
                $"MOD 加载器报告缺少依赖「{modId}」。",
                $"安装「{modId}」后重试。",
                ModId: modId,
                CanAutoFix: true));
        }

        if (!issues.Any(issue => issue.Kind == CrashIssueKind.MissingDependency)
            && (text.Contains("Missing or unsupported mandatory dependencies", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Missing dependencies", StringComparison.OrdinalIgnoreCase)))
        {
            issues.Add(new CrashIssue(
                CrashIssueKind.MissingDependency,
                "缺少必要的前置 MOD",
                "崩溃报告指出存在缺失或不支持的强制依赖，但未能解析出具体 MOD ID。",
                "查看崩溃报告中的「Missing or unsupported mandatory dependencies」部分并安装对应前置 MOD。",
                CanAutoFix: false));
        }
    }

    private static void DetectDuplicateMods(string text, List<CrashIssue> issues)
    {
        if (!text.Contains("duplicate mod", StringComparison.OrdinalIgnoreCase)
            && !text.Contains("DuplicateModsFoundException", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string? modId = DuplicateModIdRegex().Match(text) is { Success: true } match ? match.Groups[1].Value : null;
        issues.Add(new CrashIssue(
            CrashIssueKind.DuplicateMods,
            "存在重复的 MOD",
            modId is null ? "mods 目录中存在重复的 MOD 文件/ID。" : $"MOD ID「{modId}」存在多个版本。",
            "保留一个版本，禁用或删除其余重复文件后重试。",
            ModId: modId,
            CanAutoFix: true));
    }

    private static void DetectSuspectedMods(string text, List<CrashIssue> issues)
    {
        const string marker = "Suspected Mods:";
        int index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return;
        }

        string[] lines = text[(index + marker.Length)..].Split('\n');
        for (int lineIndex = 0; lineIndex < Math.Min(lines.Length, 8); lineIndex++)
        {
            string line = lines[lineIndex].Trim();
            if (line.Length == 0 || line.StartsWith("--", StringComparison.Ordinal) || line.StartsWith("Stacktrace", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (line.StartsWith("at ", StringComparison.OrdinalIgnoreCase) || line.StartsWith("NONE", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (string entry in line.Split(','))
            {
                Match match = SuspectedModRegex().Match(entry.Trim());
                if (!match.Success)
                {
                    continue;
                }

                string modId = match.Groups[1].Value.Trim();
                string name = match.Groups[2].Value.Trim();
                if (issues.Any(issue => issue.Kind is CrashIssueKind.MissingDependency && issue.ModId == modId))
                {
                    continue;
                }

                issues.Add(new CrashIssue(
                    CrashIssueKind.SuspectedMod,
                    $"可疑 MOD：{name}",
                    $"崩溃报告将 MOD「{name}」({modId}) 列为疑似导致崩溃的 MOD。",
                    $"尝试禁用 MOD「{name}」后重新启动；若问题消失，再检查该 MOD 的版本或依赖。",
                    ModId: modId,
                    CanAutoFix: true));
            }
        }
    }

    private static void DetectJavaVersion(string text, List<CrashIssue> issues)
    {
        if (!text.Contains("UnsupportedClassVersionError", StringComparison.OrdinalIgnoreCase)
            && !text.Contains("compiled by a more recent version of the Java Runtime", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Match requireMatch = JavaRequirementRegex().Match(text);
        string detail = requireMatch.Success
            ? $"日志表明该版本需要 Java {requireMatch.Groups[1].Value} 或更新版本。"
            : "日志中出现 Java 版本不兼容（UnsupportedClassVersionError / requires Java）。";
        issues.Add(new CrashIssue(
            CrashIssueKind.JavaVersion,
            "Java 版本不兼容",
            detail,
            "在「设置」中切换到匹配的 Java（例如 1.21.1 使用 Java 21），或安装对应版本的 Java 后重试。"));
    }

    private static void DetectOutOfMemory(string text, List<CrashIssue> issues)
    {
        if (!text.Contains("OutOfMemoryError", StringComparison.OrdinalIgnoreCase)
            && !text.Contains("Java heap space", StringComparison.OrdinalIgnoreCase)
            && !text.Contains("GC overhead limit exceeded", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        issues.Add(new CrashIssue(
            CrashIssueKind.OutOfMemory,
            "内存不足",
            "游戏因 Java 堆内存不足而退出（OutOfMemoryError）。",
            "调大启动内存（建议至少 4–8 GB，视整合包大小而定）或减少同时加载的 MOD。",
            CanAutoFix: true));
    }

    private static void DetectGraphics(string text, List<CrashIssue> issues)
    {
        if (!text.Contains("GLFW error", StringComparison.OrdinalIgnoreCase)
            && !text.Contains("Failed to create window", StringComparison.OrdinalIgnoreCase)
            && !text.Contains("Failed to create display", StringComparison.OrdinalIgnoreCase)
            && !text.Contains("Failed to create GL context", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        issues.Add(new CrashIssue(
            CrashIssueKind.GraphicsDriver,
            "显卡/OpenGL 初始化失败",
            "游戏无法创建图形窗口或 OpenGL 上下文，通常是显卡驱动或远程桌面环境导致。",
            "更新显卡驱动；如使用远程桌面，请在本机物理显示器上启动；也可尝试降低图形设置。"));
    }

    private static void DetectLoaderErrors(string text, List<CrashIssue> issues)
    {
        if (issues.Count > 0)
        {
            return;
        }

        if (text.Contains("Mixin apply failed", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Mixin transformation", StringComparison.OrdinalIgnoreCase)
            || text.Contains("ModResolutionException", StringComparison.OrdinalIgnoreCase)
            || text.Contains("FormattedException", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Failure during construction of mixin", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new CrashIssue(
                CrashIssueKind.LoaderError,
                "MOD 加载器错误",
                "日志显示 MOD 加载/Mixin 应用阶段失败，常见于多个 MOD 冲突或版本不匹配。",
                "检查最近安装的 MOD 与加载器版本是否匹配；逐个禁用可疑 MOD 定位冲突。"));
        }
    }

    private static string BuildSummary(IReadOnlyList<CrashIssue> issues, int exitCode)
    {
        CrashIssue? missing = issues.FirstOrDefault(issue => issue.Kind == CrashIssueKind.MissingDependency && issue.ModId is not null);
        if (missing is not null)
        {
            return $"缺少前置 MOD「{missing.ModId}」（由「{missing.RequiredBy}」要求）";
        }

        CrashIssue first = issues[0];
        return issues.Count == 1
            ? first.Title
            : $"{first.Title}（共检测到 {issues.Count} 个问题，退出码 {exitCode}）";
    }

    private static bool ContainsCrashEvidence(string text) =>
        text.Contains("Minecraft has crashed", StringComparison.OrdinalIgnoreCase)
        || text.Contains("Mod Loading has failed", StringComparison.OrdinalIgnoreCase)
        || text.Contains("Crash report saved to", StringComparison.OrdinalIgnoreCase)
        || text.Contains("Missing or unsupported mandatory dependencies", StringComparison.OrdinalIgnoreCase)
        || text.Contains("OutOfMemoryError", StringComparison.OrdinalIgnoreCase)
        || text.Contains("UnsupportedClassVersionError", StringComparison.OrdinalIgnoreCase)
        || text.Contains("A fatal error has been detected by the Java Runtime Environment", StringComparison.OrdinalIgnoreCase);

    private static string BuildExcerpt(string reportText, string logText)
    {
        string source = reportText.Length > 0 ? reportText : logText;
        if (source.Length <= MaximumExcerptLength)
        {
            return source;
        }

        int anchor = source.IndexOf("Caused by", StringComparison.OrdinalIgnoreCase);
        if (anchor < 0)
        {
            anchor = source.IndexOf("Exception", StringComparison.OrdinalIgnoreCase);
        }

        int start = anchor > 0 ? Math.Max(0, anchor - 400) : source.Length - MaximumExcerptLength;
        int length = Math.Min(MaximumExcerptLength, source.Length - start);
        return source.Substring(start, length);
    }

    private static string? FindNewestCrashReport(string gameDirectory, DateTimeOffset? notBefore)
    {
        string directory = Path.Combine(gameDirectory, "crash-reports");
        if (!Directory.Exists(directory))
        {
            return null;
        }

        try
        {
            return Directory.EnumerateFiles(directory, "*.txt")
                .Where(path => notBefore is not DateTimeOffset threshold || File.GetLastWriteTimeUtc(path) >= threshold.UtcDateTime)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string ReadTail(string path)
    {
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, FileOptions.SequentialScan);
            long length = stream.Length;
            long start = Math.Max(0, length - MaximumLogBytes);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static string FormatRange(string range) => string.IsNullOrWhiteSpace(range) ? string.Empty : $"（版本范围 {range}）";

    [GeneratedRegex(@"Mod ID:\s*'([^']+)',\s*Requested by:\s*'([^']+)'(?:,\s*Expected range:\s*'([^']*)')?[^\n]*Actual version:\s*'\[MISSING\]'", RegexOptions.IgnoreCase)]
    private static partial Regex MissingDependencyRegex();

    [GeneratedRegex(@"(?:requires|missing)[^\n]*?['""]?([a-z0-9_.-]+)['""]?[^\n]*(?:which is missing|is missing)", RegexOptions.IgnoreCase)]
    private static partial Regex FabricMissingDependencyRegex();

    [GeneratedRegex(@"(?:mod(?:ule)? id|mod id)\s*['""]([^'""]+)['""][^\n]*duplicate", RegexOptions.IgnoreCase)]
    private static partial Regex DuplicateModIdRegex();

    [GeneratedRegex(@"^([a-z][a-z0-9_-]{1,63})\s*\(([^)]+)\)$", RegexOptions.IgnoreCase)]
    private static partial Regex SuspectedModRegex();

    [GeneratedRegex(@"requires (?:at least )?Java\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex JavaRequirementRegex();
}
