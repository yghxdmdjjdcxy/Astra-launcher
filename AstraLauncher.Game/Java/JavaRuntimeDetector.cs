using System.Diagnostics;
using System.ComponentModel;
using System.Text.RegularExpressions;

namespace AstraLauncher.Game.Java;

public sealed record JavaRuntimeInfo(string ExecutablePath, string Version, int MajorVersion, string? Vendor);

public interface IJavaRuntimeDetector
{
    Task<IReadOnlyList<JavaRuntimeInfo>> DetectAsync(CancellationToken cancellationToken = default);
    Task<JavaRuntimeInfo?> InspectAsync(string executablePath, CancellationToken cancellationToken = default);
}

public sealed partial class JavaRuntimeDetector : IJavaRuntimeDetector
{
    public async Task<IReadOnlyList<JavaRuntimeInfo>> DetectAsync(CancellationToken cancellationToken = default)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome)) candidates.Add(Path.Combine(javaHome, "bin", "java.exe"));
        foreach (string root in ProgramFilesRoots())
        {
            foreach (string vendorDirectory in new[] { "Java", "Eclipse Adoptium", "Microsoft", "Zulu", "BellSoft" })
            {
                string directory = Path.Combine(root, vendorDirectory);
                if (!Directory.Exists(directory)) continue;
                foreach (string path in Directory.EnumerateFiles(directory, "java.exe", SearchOption.AllDirectories)) candidates.Add(path);
            }
        }
        foreach (string path in await FindOnPathAsync(cancellationToken).ConfigureAwait(false)) candidates.Add(path);
        var results = new List<JavaRuntimeInfo>();
        foreach (string candidate in candidates.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            JavaRuntimeInfo? runtime = await InspectAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (runtime is not null) results.Add(runtime);
        }
        return results;
    }

    public async Task<JavaRuntimeInfo?> InspectAsync(string executablePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath)) return null;
        try
        {
            using Process process = new();
            process.StartInfo = new ProcessStartInfo(executablePath, "-version") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            if (!process.Start()) return null;
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            string output = (await stdout.ConfigureAwait(false)) + Environment.NewLine + (await stderr.ConfigureAwait(false));
            return JavaVersionParser.TryParse(output, Path.GetFullPath(executablePath), out JavaRuntimeInfo? runtime) ? runtime : null;
        }
        catch (Win32Exception) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    private static async Task<IReadOnlyList<string>> FindOnPathAsync(CancellationToken cancellationToken)
    {
        try
        {
            using Process process = new();
            process.StartInfo = new ProcessStartInfo("where.exe", "java") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
            if (!process.Start()) return [];
            string output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            return output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(File.Exists).ToArray();
        }
        catch (Win32Exception) { return []; }
        catch (InvalidOperationException) { return []; }
    }

    private static IEnumerable<string> ProgramFilesRoots()
    {
        string[] roots = [Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)];
        return roots.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase);
    }
}

public static partial class JavaVersionParser
{
    [GeneratedRegex("(?:openjdk|java)\\s+version\\s+\\\"(?<version>[^\\\"]+)\\\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionRegex();

    public static bool TryParse(string output, string executablePath, out JavaRuntimeInfo? runtime)
    {
        Match match = VersionRegex().Match(output);
        if (!match.Success) { runtime = null; return false; }
        string version = match.Groups["version"].Value;
        string majorText = version.StartsWith("1.", StringComparison.Ordinal) ? version[2..].Split(['.', '_', '-'])[0] : version.Split(['.', '_', '-'])[0];
        if (!int.TryParse(majorText, out int major)) { runtime = null; return false; }
        string? vendor = output.Contains("Eclipse Adoptium", StringComparison.OrdinalIgnoreCase) ? "Eclipse Adoptium" : output.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) ? "Microsoft" : null;
        runtime = new JavaRuntimeInfo(executablePath, version, major, vendor);
        return true;
    }
}
