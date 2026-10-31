using System.IO;
using AstraLauncher.Core.Models;
using AstraLauncher.Game.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace AstraLauncher.Tests;

public sealed class CrashAnalyzerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AstraLauncher.Tests", Guid.NewGuid().ToString("N"));
    private readonly CrashAnalyzer _analyzer = new(NullLogger<CrashAnalyzer>.Instance);
    private readonly GameInstance _instance;

    public CrashAnalyzerTests()
    {
        _instance = new GameInstance(Guid.NewGuid(), "Crash Test", _root, "1.21.1", true);
        Directory.CreateDirectory(Path.Combine(_root, "logs"));
    }

    [Fact]
    public async Task AnalyzeAsync_MissingDependency_IsDetectedFromPclStyleLog()
    {
        await WriteLogAsync("""
        [23:23:22] [main/ERROR]: Missing or unsupported mandatory dependencies:
        Mod ID: 'create', Requested by: 'create_hypertube', Expected range: '[6.0.4,6.1.0)', Actual version: '[MISSING]'
        Mod ID: 'create', Requested by: 'create_dragons_plus', Expected range: '[6.0.8,6.1.0)', Actual version: '[MISSING]'
        """);

        CrashAnalysisResult? result = await _analyzer.AnalyzeAsync(_instance, 1);

        Assert.NotNull(result);
        IReadOnlyList<CrashIssue> missingIssues = result!.Issues.Where(item => item.Kind == CrashIssueKind.MissingDependency && item.ModId == "create").ToArray();
        Assert.Equal(2, missingIssues.Count);
        CrashIssue issue = missingIssues[0];
        Assert.Equal("create_hypertube", issue.RequiredBy);
        Assert.True(issue.CanAutoFix);
        Assert.Contains("create", result.Summary);
        Assert.True(result.CanAutoRepair);
    }

    [Fact]
    public async Task AnalyzeAsync_MissingGameFile_IsDetected()
    {
        await WriteLogAsync("java.io.FileNotFoundException: F:\\MC\\.minecraft\\versions\\1.21.1\\1.21.1.jar (系统找不到指定的文件)");

        CrashAnalysisResult? result = await _analyzer.AnalyzeAsync(_instance, 1);

        Assert.NotNull(result);
        CrashIssue issue = Assert.Single(result!.Issues, item => item.Kind == CrashIssueKind.MissingGameFiles);
        Assert.True(issue.CanAutoFix);
    }

    [Fact]
    public async Task AnalyzeAsync_OutOfMemory_IsDetected()
    {
        await WriteLogAsync("java.lang.OutOfMemoryError: Java heap space\n[Minecraft has crashed!]");

        CrashAnalysisResult? result = await _analyzer.AnalyzeAsync(_instance, 1);

        Assert.NotNull(result);
        Assert.Contains(result!.Issues, issue => issue.Kind == CrashIssueKind.OutOfMemory && issue.CanAutoFix);
    }

    [Fact]
    public async Task AnalyzeAsync_DuplicateMods_IsDetected()
    {
        await WriteLogAsync("java.lang.RuntimeException: Duplicate mods found for mod id 'examplemod' (duplicate mod file)");

        CrashAnalysisResult? result = await _analyzer.AnalyzeAsync(_instance, 1);

        Assert.NotNull(result);
        CrashIssue issue = Assert.Single(result!.Issues, item => item.Kind == CrashIssueKind.DuplicateMods);
        Assert.True(issue.CanAutoFix);
    }

    [Fact]
    public async Task AnalyzeAsync_SuspectedModsInCrashReport_IsDetected()
    {
        string crashReports = Path.Combine(_root, "crash-reports");
        Directory.CreateDirectory(crashReports);
        await File.WriteAllTextAsync(Path.Combine(crashReports, "crash-2026-10-29.txt"), """
        Minecraft has crashed!
        Suspected Mods: examplemod (Example Mod)
        """);

        CrashAnalysisResult? result = await _analyzer.AnalyzeAsync(_instance, -1);

        Assert.NotNull(result);
        Assert.Contains(result!.Issues, issue => issue.Kind == CrashIssueKind.SuspectedMod && issue.ModId == "examplemod" && issue.CanAutoFix);
    }

    [Fact]
    public async Task AnalyzeAsync_SuspectedModsNone_DoesNotProduceFalsePositives()
    {
        string crashReports = Path.Combine(_root, "crash-reports");
        Directory.CreateDirectory(crashReports);
        await File.WriteAllTextAsync(Path.Combine(crashReports, "crash-none.txt"), """
        Minecraft has crashed!
        Suspected Mods: NONE
        Stacktrace:
        	at net.minecraftforge.logging.CrashReportExtender.dumpModLoadingCrashReport(CrashReportExtender.java:63)
        """);

        CrashAnalysisResult? result = await _analyzer.AnalyzeAsync(_instance, 1);

        Assert.NotNull(result);
        Assert.DoesNotContain(result!.Issues, issue => issue.Kind == CrashIssueKind.SuspectedMod);
    }

    [Fact]
    public async Task AnalyzeAsync_SuspectedModsLine_IsParsedWithoutStackFrames()
    {
        string crashReports = Path.Combine(_root, "crash-reports");
        Directory.CreateDirectory(crashReports);
        await File.WriteAllTextAsync(Path.Combine(crashReports, "crash-suspect.txt"), """
        Minecraft has crashed!
        Suspected Mods: examplemod (Example Mod), othermod (Other Mod)
        Stacktrace:
        	at net.minecraftforge.logging.CrashReportExtender.dumpModLoadingCrashReport(CrashReportExtender.java:63)
        """);

        CrashAnalysisResult? result = await _analyzer.AnalyzeAsync(_instance, 1);

        Assert.NotNull(result);
        Assert.Contains(result!.Issues, issue => issue.Kind == CrashIssueKind.SuspectedMod && issue.ModId == "examplemod");
        Assert.Contains(result.Issues, issue => issue.Kind == CrashIssueKind.SuspectedMod && issue.ModId == "othermod");
        Assert.DoesNotContain(result.Issues, issue => issue.ModId is not null && issue.ModId.Contains("CrashReportExtender", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnalyzeAsync_StaleLogsBeforeNotBefore_ReturnsNull()
    {
        await WriteLogAsync("Minecraft has crashed!");
        DateTimeOffset threshold = DateTimeOffset.UtcNow.AddMinutes(5);

        CrashAnalysisResult? result = await _analyzer.AnalyzeAsync(_instance, 1, threshold);

        Assert.Null(result);
    }

    [Fact]
    public async Task AnalyzeAsync_ZeroExitCode_ReturnsNull()
    {
        await WriteLogAsync("normal shutdown");

        CrashAnalysisResult? result = await _analyzer.AnalyzeAsync(_instance, 0);

        Assert.Null(result);
    }

    [Fact]
    public async Task AnalyzeAsync_WithoutLogs_ReturnsNull()
    {
        CrashAnalysisResult? result = await _analyzer.AnalyzeAsync(_instance, 1);

        Assert.Null(result);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private Task WriteLogAsync(string text) => File.WriteAllTextAsync(Path.Combine(_root, "logs", "latest.log"), text);
}
