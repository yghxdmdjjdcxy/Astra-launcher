using AstraLauncher.Core.Models;
using AstraLauncher.Game.Mods;

namespace AstraLauncher.Game.Diagnostics;

/// <summary>Categories of common Minecraft crash causes.</summary>
public enum CrashIssueKind
{
    MissingDependency,
    SuspectedMod,
    DuplicateMods,
    JavaVersion,
    OutOfMemory,
    GraphicsDriver,
    LoaderError,
    Unknown,
}

/// <summary>One detected cause with a human-readable explanation and a suggested next step.</summary>
public sealed record CrashIssue(
    CrashIssueKind Kind,
    string Title,
    string Details,
    string Suggestion,
    string? ModId = null,
    string? ModFile = null,
    string? RequiredBy = null,
    string? VersionRange = null,
    bool CanAutoFix = false);

/// <summary>Result of analyzing a failed game run.</summary>
public sealed record CrashAnalysisResult(
    IReadOnlyList<CrashIssue> Issues,
    string Summary,
    string? LatestLogPath,
    string? CrashReportPath,
    string RawExcerpt)
{
    public bool CanAutoRepair => Issues.Any(issue => issue.CanAutoFix);
}

/// <summary>Analyzes the latest game log / crash report of an instance after a non-zero exit code.</summary>
public interface ICrashAnalyzer
{
    /// <summary>
    /// Returns <see langword="null"/> when there is no fresh log evidence. <paramref name="notBefore"/> can be used to
    /// ignore stale crash reports/logs from earlier runs (for example when analyzing a crash while the game still runs).
    /// </summary>
    Task<CrashAnalysisResult?> AnalyzeAsync(
        GameInstance instance,
        int exitCode,
        DateTimeOffset? notBefore = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Outcome of one reversible repair action.</summary>
public sealed record CrashRepairAction(string Description, bool Success, string? Detail = null);

public sealed record CrashRepairResult(IReadOnlyList<CrashRepairAction> Actions, string Summary)
{
    public bool Changed => Actions.Any(action => action.Success);
}

/// <summary>Applies reversible fixes for a crash analysis result (disable mods, install missing dependencies, memory).</summary>
public interface ICrashRepairService
{
    Task<CrashRepairResult> RepairAsync(
        GameInstance instance,
        CrashAnalysisResult analysis,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Resolved Minecraft version and mod loader of an instance, used to pick compatible mods.</summary>
public sealed record InstanceModContext(string MinecraftVersion, ModPlatform Platform)
{
    public string ModrinthLoader => ModPlatformName(Platform);

    public static string ModPlatformName(ModPlatform platform) => platform switch
    {
        ModPlatform.Fabric => "fabric",
        ModPlatform.Quilt => "quilt",
        ModPlatform.Forge => "forge",
        ModPlatform.NeoForge => "neoforge",
        _ => "fabric",
    };
}
