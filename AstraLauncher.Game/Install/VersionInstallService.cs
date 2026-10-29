using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using AstraLauncher.Core.Models;
using AstraLauncher.Core.Services;
using AstraLauncher.Downloader;
using AstraLauncher.Game.Metadata;
using Microsoft.Extensions.Logging;

namespace AstraLauncher.Game.Install;

/// <summary>
/// Builds the complete install plan of a Minecraft version (version metadata, client, platform libraries, asset
/// index, assets and log configuration) and downloads every missing file through a <see cref="DownloadQueue"/>.
/// </summary>
public sealed class VersionInstallService : IVersionInstallService
{
    private const string VersionJsonCategory = "version-json";
    private const string ClientCategory = "client";
    private const string LibraryCategory = "library";
    private const string AssetIndexCategory = "asset-index";
    private const string AssetCategory = "asset";
    private const string LoggingCategory = "logging";
    private const string DownloadingStage = "下载游戏文件";
    private const string CompletedStage = "下载完成";
    private const string AssetObjectUrlFormat = "https://resources.download.minecraft.net/{0}/{1}";

    private static readonly TimeSpan ProgressReportInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan RuleRegexTimeout = TimeSpan.FromSeconds(1);

    private readonly ILauncherPaths _paths;
    private readonly IVersionManifestService _manifest;
    private readonly VersionMetadataStore _metadataStore;
    private readonly IDownloadSourceResolver _sources;
    private readonly ISettingsService _settings;
    private readonly DownloadQueue _downloadQueue;
    private readonly ILogger<VersionInstallService> _logger;

    public VersionInstallService(
        ILauncherPaths paths,
        IVersionManifestService manifest,
        VersionMetadataStore metadataStore,
        IDownloadSourceResolver sources,
        ISettingsService settings,
        DownloadQueue downloadQueue,
        ILogger<VersionInstallService> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(metadataStore);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(downloadQueue);
        ArgumentNullException.ThrowIfNull(logger);
        _paths = paths;
        _manifest = manifest;
        _metadataStore = metadataStore;
        _sources = sources;
        _settings = settings;
        _downloadQueue = downloadQueue;
        _logger = logger;
    }

    public async Task<VersionInstallPlan> CreatePlanAsync(string versionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);
        LauncherSettings settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        MinecraftVersionMetadata metadata = await _metadataStore.EnsureResolvedAsync(versionId, cancellationToken).ConfigureAwait(false);

        var files = new List<InstallFile>();
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(InstallFile file)
        {
            // The first entry for a destination wins; later duplicates (for example assets shared by category) are ignored.
            string destination = Path.GetFullPath(file.DestinationPath);
            if (destinations.Add(destination))
            {
                files.Add(file);
            }
        }

        await AddVersionJsonFilesAsync(versionId, Add, cancellationToken).ConfigureAwait(false);
        AddClientFile(versionId, metadata, Add);
        AddLibraryFiles(metadata, Add);
        await AddAssetFilesAsync(metadata, settings.DownloadSourceMode, Add, cancellationToken).ConfigureAwait(false);
        AddLoggingFile(metadata, Add);

        long knownTotalBytes = 0;
        foreach (InstallFile file in files)
        {
            knownTotalBytes += file.Size ?? 0;
        }

        _logger.LogInformation("Created install plan for version {VersionId} with {FileCount} files ({KnownTotalBytes} known bytes).", versionId, files.Count, knownTotalBytes);
        return new VersionInstallPlan(versionId, files, knownTotalBytes);
    }

    public async Task InstallAsync(string versionId, IProgress<VersionInstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);
        LauncherSettings settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        VersionInstallPlan plan = await CreatePlanAsync(versionId, cancellationToken).ConfigureAwait(false);
        int totalFiles = plan.Files.Count;
        long totalBytes = plan.KnownTotalBytes;

        var pending = new List<InstallFile>(totalFiles);
        int completedFiles = 0;
        long completedBytes = 0;
        foreach (InstallFile file in plan.Files)
        {
            if (IsFileComplete(file))
            {
                completedFiles++;
                completedBytes += file.Size ?? ExistingLength(file.DestinationPath);
            }
            else
            {
                pending.Add(file);
            }
        }

        if (completedFiles > 0)
        {
            _logger.LogInformation("Version {VersionId}: {CompletedFiles} of {TotalFiles} files are already present.", versionId, completedFiles, totalFiles);
        }

        long[] fileBytes = new long[pending.Count];
        var reportLock = new object();
        long lastReportTimestamp = Stopwatch.GetTimestamp();
        long reportIntervalTicks = Math.Max(1, (long)(Stopwatch.Frequency * ProgressReportInterval.TotalSeconds));

        void ReportProgress(string currentFile, bool force)
        {
            if (progress is null)
            {
                return;
            }

            long now = Stopwatch.GetTimestamp();
            if (force)
            {
                Interlocked.Exchange(ref lastReportTimestamp, now);
            }
            else
            {
                long previous = Interlocked.Read(ref lastReportTimestamp);
                if (now - previous < reportIntervalTicks || Interlocked.CompareExchange(ref lastReportTimestamp, now, previous) != previous)
                {
                    return;
                }
            }

            long downloadedBytes = completedBytes;
            for (int index = 0; index < fileBytes.Length; index++)
            {
                downloadedBytes += Volatile.Read(ref fileBytes[index]);
            }

            var snapshot = new VersionInstallProgress(totalFiles, Volatile.Read(ref completedFiles), totalBytes, downloadedBytes, DownloadingStage, currentFile);
            lock (reportLock)
            {
                progress.Report(snapshot);
            }
        }

        var downloadGate = new SemaphoreSlim(Math.Max(1, settings.MaxConcurrentDownloads));
        try
        {
            var tasks = new Task[pending.Count];
            for (int index = 0; index < pending.Count; index++)
            {
                tasks[index] = DownloadFileSlotAsync(index, pending[index]);
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        finally
        {
            downloadGate.Dispose();
        }

        progress?.Report(new VersionInstallProgress(totalFiles, totalFiles, totalBytes, totalBytes, CompletedStage, null));
        _logger.LogInformation("Version {VersionId} installed: {TotalFiles} files ({TotalBytes} known bytes).", versionId, totalFiles, totalBytes);

        async Task DownloadFileSlotAsync(int slot, InstallFile file)
        {
            await downloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                long writtenBytes = await DownloadFileAsync(
                    file,
                    settings.DownloadSourceMode,
                    received => { Interlocked.Exchange(ref fileBytes[slot], received); ReportProgress(file.DestinationPath, force: false); },
                    cancellationToken).ConfigureAwait(false);
                Interlocked.Exchange(ref fileBytes[slot], writtenBytes);
                Interlocked.Increment(ref completedFiles);
                ReportProgress(file.DestinationPath, force: true);
            }
            finally
            {
                downloadGate.Release();
            }
        }
    }

    public async Task<bool> IsVersionInstalledAsync(string versionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);
        try
        {
            await _metadataStore.EnsureResolvedAsync(versionId, cancellationToken).ConfigureAwait(false);
            return File.Exists(Path.Combine(_paths.VersionsDirectory, versionId, $"{versionId}.jar"));
        }
#pragma warning disable CA1031 // A probe for "is it installed" must answer false for every failure instead of breaking callers.
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }

    private async Task AddVersionJsonFilesAsync(string versionId, Action<InstallFile> add, CancellationToken cancellationToken)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        string? current = versionId;
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (!visited.Add(current))
            {
                throw new VersionMetadataException($"Version inheritance contains a cycle at '{current}'.");
            }

            string metadataPath = Path.Combine(_paths.VersionsDirectory, current, $"{current}.json");
            if (!File.Exists(metadataPath))
            {
                throw new InvalidOperationException($"The metadata file of version '{current}' is missing after resolution.");
            }

            // Loader profiles (Fabric/Quilt/Forge) and PCL-installed versions are not part of the Mojang manifest,
            // so their JSON cannot be re-downloaded from there; the local file is already complete.
            VersionSummary? summary = null;
            try
            {
                summary = await _manifest.GetVersionSummaryAsync(current, cancellationToken).ConfigureAwait(false);
            }
            catch (KeyNotFoundException)
            {
                _logger.LogDebug("Version {VersionId} is local-only and not in the version manifest; keeping its JSON as is.", current);
            }

            if (summary is not null)
            {
                add(new InstallFile(summary.Url, metadataPath, summary.Sha1, null, VersionJsonCategory));
            }

            current = await ReadInheritsFromAsync(metadataPath, cancellationToken).ConfigureAwait(false);
        }
    }

    private void AddClientFile(string versionId, MinecraftVersionMetadata metadata, Action<InstallFile> add)
    {
        if (!metadata.Downloads.TryGetValue("client", out DownloadArtifact? client) || client.Url is not Uri clientUri)
        {
            return;
        }

        add(new InstallFile(clientUri.AbsoluteUri, Path.Combine(_paths.VersionsDirectory, versionId, $"{versionId}.jar"), client.Sha1, client.Size, ClientCategory));
    }

    private void AddLibraryFiles(MinecraftVersionMetadata metadata, Action<InstallFile> add)
    {
        foreach (LibraryDescriptor library in metadata.Libraries)
        {
            if (!RulesAllow(library.Rules))
            {
                continue;
            }

            bool nativeLibrary = library.Natives.Count > 0;
            if (TryAddArtifact(library, out InstallFile artifactFile))
            {
                add(artifactFile);
            }
            else if (!nativeLibrary && TryCreateUrlFallback(library, classifier: null, out InstallFile urlArtifact))
            {
                // Modern loader profiles (Fabric/Quilt) list a Maven repository base URL instead of downloads blocks.
                add(urlArtifact);
            }
            else if (!nativeLibrary && TryCreateMavenFallback(library.Name, classifier: null, out InstallFile fallbackArtifact))
            {
                // Legacy metadata has no downloads block at all; the official Maven repository is the only source.
                // Natives-only libraries are skipped here because they publish no base jar, only the classifier below.
                add(fallbackArtifact);
            }

            if (!nativeLibrary
                || !library.Natives.TryGetValue(CurrentOperatingSystemName(), out string? classifierTemplate)
                || string.IsNullOrWhiteSpace(classifierTemplate))
            {
                continue;
            }

            string classifier = classifierTemplate.Replace("${arch}", CurrentArchitectureName(), StringComparison.Ordinal);
            if (TryAddNativeArtifact(library, classifier, out InstallFile nativeFile))
            {
                add(nativeFile);
            }
            else if (TryCreateUrlFallback(library, classifier, out InstallFile urlNative))
            {
                add(urlNative);
            }
            else if (TryCreateMavenFallback(library.Name, classifier, out InstallFile fallbackNative))
            {
                // Legacy natives-only entries reference their archive only through the Maven classifier.
                add(fallbackNative);
            }
        }
    }

    private bool TryAddArtifact(LibraryDescriptor library, out InstallFile file)
    {
        file = null!;
        if (library.Downloads?.Artifact is not { } artifact
            || !TryResolveLibraryPath(artifact.Path, library.Name, classifier: null, out string relativePath))
        {
            return false;
        }

        Uri uri = artifact.Url ?? CreateInferredLibraryUri(library.Name, relativePath);
        file = new InstallFile(uri.AbsoluteUri, Path.Combine(_paths.LibrariesDirectory, relativePath), artifact.Sha1, artifact.Size, LibraryCategory);
        return true;
    }

    private bool TryAddNativeArtifact(LibraryDescriptor library, string classifier, out InstallFile file)
    {
        file = null!;
        if (library.Downloads is not { } downloads
            || !downloads.Classifiers.TryGetValue(classifier, out DownloadArtifact? artifact)
            || !TryResolveLibraryPath(artifact.Path, library.Name, classifier, out string relativePath))
        {
            return false;
        }

        Uri uri = artifact.Url ?? CreateInferredLibraryUri(library.Name, relativePath);
        file = new InstallFile(uri.AbsoluteUri, Path.Combine(_paths.LibrariesDirectory, relativePath), artifact.Sha1, artifact.Size, LibraryCategory);
        return true;
    }

    /// <summary>
    /// Some loader-generated metadata (notably Forge) omits the download URL for artifacts that the installer generated
    /// locally. Infer the repository from the coordinate so the file can still be verified/downloaded when needed.
    /// </summary>
    private static Uri CreateInferredLibraryUri(string coordinate, string relativePath)
    {
        string group = coordinate.Split(':')[0];
        string baseUrl = group switch
        {
            "net.minecraftforge" => "https://maven.minecraftforge.net/",
            "net.neoforged" => "https://maven.neoforged.net/releases/",
            _ => "https://libraries.minecraft.net/",
        };
        return new Uri(new Uri(baseUrl), relativePath.Replace(Path.DirectorySeparatorChar, '/'));
    }

    /// <summary>
    /// Builds a download for modern loader metadata that names a Maven coordinate plus a repository base URL
    /// (for example Fabric/Quilt profiles); no size or SHA-1 is available, so only the presence of the file is verified.
    /// </summary>
    private bool TryCreateUrlFallback(LibraryDescriptor library, string? classifier, out InstallFile file)
    {
        file = null!;
        if (string.IsNullOrWhiteSpace(library.Url)
            || !TryGetMavenPath(library.Name, classifier, out string mavenPath)
            || !TryResolveLibraryPath(mavenPath, library.Name, classifier, out string relativePath))
        {
            return false;
        }

        string baseUrl = library.Url.Trim();
        if (!baseUrl.EndsWith('/'))
        {
            baseUrl += "/";
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? repository) || repository.Scheme is not ("https" or "http"))
        {
            return false;
        }

        string url = new Uri(repository, relativePath.Replace(Path.DirectorySeparatorChar, '/')).AbsoluteUri;
        file = new InstallFile(url, Path.Combine(_paths.LibrariesDirectory, relativePath), null, null, LibraryCategory);
        return true;
    }

    /// <summary>
    /// Builds a download for legacy metadata that only names the Maven coordinate: the official library repository is
    /// the implicit source and no size or SHA-1 is available, so only the presence of the file can be verified.
    /// </summary>
    private bool TryCreateMavenFallback(string coordinate, string? classifier, out InstallFile file)
    {
        file = null!;
        if (!TryGetMavenPath(coordinate, classifier, out string mavenPath)
            || !TryResolveLibraryPath(mavenPath, coordinate, classifier, out string relativePath))
        {
            return false;
        }

        string url = "https://libraries.minecraft.net/" + relativePath.Replace(Path.DirectorySeparatorChar, '/');
        file = new InstallFile(url, Path.Combine(_paths.LibrariesDirectory, relativePath), null, null, LibraryCategory);
        return true;
    }

    private async Task AddAssetFilesAsync(MinecraftVersionMetadata metadata, DownloadSourceMode mode, Action<InstallFile> add, CancellationToken cancellationToken)
    {
        if (metadata.AssetIndex is not { } assetIndex)
        {
            return;
        }

        string indexPath = Path.Combine(_paths.AssetsDirectory, "indexes", $"{assetIndex.Id}.json");
        if (assetIndex.Url is Uri indexUri)
        {
            var indexFile = new InstallFile(indexUri.AbsoluteUri, indexPath, assetIndex.Sha1, assetIndex.Size, AssetIndexCategory);
            add(indexFile);
            if (!File.Exists(indexPath))
            {
                // The plan must be complete, so the index is fetched while it is being created.
                await DownloadFileAsync(indexFile, mode, bytesReported: null, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Downloaded asset index {AssetIndexId} while building the install plan.", assetIndex.Id);
            }
        }
        else if (!File.Exists(indexPath))
        {
            throw new InvalidDataException($"The asset index '{assetIndex.Id}' has no download URL.");
        }

        byte[] payload = await File.ReadAllBytesAsync(indexPath, cancellationToken).ConfigureAwait(false);
        foreach (InstallFile assetFile in ParseAssetObjects(payload, indexPath))
        {
            add(assetFile);
        }
    }

    private IReadOnlyList<InstallFile> ParseAssetObjects(byte[] payload, string indexPath)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"The asset index '{indexPath}' is not valid JSON.", exception);
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("objects", out JsonElement objects) || objects.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"The asset index '{indexPath}' does not contain an 'objects' object.");
            }

            var files = new List<InstallFile>();
            var hashes = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in objects.EnumerateObject())
            {
                JsonElement value = property.Value;
                string? hash = value.ValueKind == JsonValueKind.Object && value.TryGetProperty("hash", out JsonElement hashElement) && hashElement.ValueKind == JsonValueKind.String
                    ? hashElement.GetString()
                    : null;
                if (hash is null || !IsSha1(hash))
                {
                    throw new InvalidDataException($"Asset '{property.Name}' in asset index '{indexPath}' does not have a valid SHA-1 hash.");
                }

                if (!hashes.Add(hash))
                {
                    continue;
                }

                long? size = value.TryGetProperty("size", out JsonElement sizeElement) && sizeElement.TryGetInt64(out long parsedSize) ? parsedSize : null;
                string prefix = hash[..2];
                files.Add(new InstallFile(
                    string.Format(System.Globalization.CultureInfo.InvariantCulture, AssetObjectUrlFormat, prefix, hash),
                    Path.Combine(_paths.AssetsDirectory, "objects", prefix, hash),
                    hash,
                    size,
                    AssetCategory));
            }

            return files;
        }
    }

    private void AddLoggingFile(MinecraftVersionMetadata metadata, Action<InstallFile> add)
    {
        if (metadata.Logging?.Client.File is not { } loggingFile || string.IsNullOrWhiteSpace(loggingFile.Id) || loggingFile.Url is not Uri loggingUri)
        {
            return;
        }

        if (!IsSafeFileName(loggingFile.Id))
        {
            _logger.LogWarning("Skipping logging file {LoggingFileId} of version {VersionId}: the id is not a single file name.", loggingFile.Id, metadata.Id);
            return;
        }

        add(new InstallFile(loggingUri.AbsoluteUri, Path.Combine(_paths.AssetsDirectory, "logging", loggingFile.Id), loggingFile.Sha1, loggingFile.Size, LoggingCategory));
    }

    private async Task<long> DownloadFileAsync(InstallFile file, DownloadSourceMode mode, Action<long>? bytesReported, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(file.Url, UriKind.Absolute, out Uri? officialUri) || officialUri.Scheme is not ("http" or "https"))
        {
            throw new IOException($"The download URL of '{file.DestinationPath}' is not a valid HTTP(S) URL: '{file.Url}'.");
        }

        IReadOnlyList<Uri> candidates = _sources.GetCandidates(officialUri, mode);
        if (candidates.Count == 0)
        {
            candidates = [officialUri];
        }

        Exception? lastError = null;
        foreach (Uri candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IDownloadHandle? handle = null;
            EventHandler<DownloadProgress>? handler = null;
            CancellationTokenRegistration registration = default;
            try
            {
                handle = _downloadQueue.Enqueue(DownloadRequest.Create(candidate, file.DestinationPath, NormalizeSha1(file.Sha1), NormalizeExpectedSize(file.Size)));
                if (bytesReported is not null)
                {
                    IDownloadHandle downloading = handle;
                    handler = (_, args) => bytesReported(args.BytesReceived);
                    downloading.ProgressChanged += handler;
                }

                registration = cancellationToken.Register(handle.Cancel);
                await handle.Completion.ConfigureAwait(false);
                return file.Size ?? handle.Progress.BytesReceived;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                lastError = exception;
                _logger.LogWarning(exception, "Download of {DestinationPath} from {Source} failed; trying the next source.", file.DestinationPath, candidate);
            }
            finally
            {
                registration.Dispose();
                if (handle is not null && handler is not null)
                {
                    handle.ProgressChanged -= handler;
                }
            }
        }

        throw new IOException($"Failed to download '{file.DestinationPath}' from all configured sources: {lastError?.Message}", lastError);
    }

    /// <summary>Evaluates Mojang library/argument rules: the last matching rule decides, no rules means allowed.</summary>
    private static bool RulesAllow(IReadOnlyList<ArgumentRule> rules)
    {
        if (rules.Count == 0)
        {
            return true;
        }

        bool allowed = false;
        foreach (ArgumentRule rule in rules)
        {
            if (RuleMatches(rule))
            {
                allowed = rule.Action.Equals("allow", StringComparison.OrdinalIgnoreCase);
            }
        }

        return allowed;
    }

    private static bool RuleMatches(ArgumentRule rule)
    {
        if (rule.OperatingSystemName is not null && !rule.OperatingSystemName.Equals(CurrentOperatingSystemName(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (rule.OperatingSystemArchitecture is not null && !NormalizeArchitecture(rule.OperatingSystemArchitecture).Equals(CurrentArchitectureName(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (rule.OperatingSystemVersion is not null && !MatchesOperatingSystemVersion(rule.OperatingSystemVersion))
        {
            return false;
        }

        // The launcher enables no optional features, so a rule only matches when every requested feature is disabled.
        return rule.Features.All(feature => !feature.Value);
    }

    private static bool MatchesOperatingSystemVersion(string pattern)
    {
        try
        {
            return Regex.IsMatch(Environment.OSVersion.Version.ToString(), pattern, RegexOptions.CultureInvariant, RuleRegexTimeout);
        }
        catch (ArgumentException)
        {
            // An invalid pattern cannot match anything; metadata must not break the plan.
            return false;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static string NormalizeArchitecture(string architecture) => architecture.ToUpperInvariant() switch
    {
        "X86_64" or "AMD64" => "x64",
        "AARCH64" => "arm64",
        _ => architecture.ToLowerInvariant(),
    };

    private static string CurrentOperatingSystemName() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows"
        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx"
        : "linux";

    private static string CurrentArchitectureName() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X86 => "x86",
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        Architecture.Arm => "arm",
        _ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
    };

    private bool TryResolveLibraryPath(string? metadataPath, string coordinate, string? classifier, out string relativePath)
    {
        relativePath = string.Empty;
        string? candidate;
        if (!string.IsNullOrWhiteSpace(metadataPath))
        {
            candidate = metadataPath;
        }
        else if (TryGetMavenPath(coordinate, classifier, out string mavenPath))
        {
            candidate = mavenPath;
        }
        else
        {
            _logger.LogWarning("Skipping library {Library}: the name cannot be resolved to a safe Maven path.", coordinate);
            return false;
        }

        if (Path.IsPathRooted(candidate))
        {
            _logger.LogWarning("Skipping library {Library}: the artifact path '{ArtifactPath}' is not a relative path.", coordinate, candidate);
            return false;
        }

        string normalized = candidate.Replace('/', Path.DirectorySeparatorChar);
        string root = Path.GetFullPath(_paths.LibrariesDirectory);
        string fullPath = Path.GetFullPath(Path.Combine(root, normalized));
        if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Skipping library {Library}: the artifact path '{ArtifactPath}' escapes the libraries directory.", coordinate, candidate);
            return false;
        }

        relativePath = normalized;
        return true;
    }

    private static bool TryGetMavenPath(string coordinate, string? classifier, out string mavenPath)
    {
        mavenPath = string.Empty;
        string[] segments = coordinate.Split(':');
        if (segments.Length < 3
            || !IsSafeMavenSegment(segments[0])
            || !IsSafeMavenSegment(segments[1])
            || !IsSafeMavenSegment(segments[2]))
        {
            return false;
        }

        string? effectiveClassifier = classifier ?? (segments.Length > 3 ? segments[3] : null);
        if (effectiveClassifier is not null && !IsSafeMavenSegment(effectiveClassifier))
        {
            return false;
        }

        string group = segments[0].Replace('.', Path.DirectorySeparatorChar);
        string fileName = effectiveClassifier is null
            ? $"{segments[1]}-{segments[2]}.jar"
            : $"{segments[1]}-{segments[2]}-{effectiveClassifier}.jar";
        mavenPath = Path.Combine(group, segments[1], segments[2], fileName);
        return true;
    }

    private static bool IsSafeMavenSegment(string segment) =>
        segment.Length > 0
        && segment is not "." and not ".."
        && !segment.Contains("..", StringComparison.Ordinal)
        && !segment.Contains('/')
        && !segment.Contains('\\');

    private static async Task<string?> ReadInheritsFromAsync(string metadataPath, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(metadataPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        MinecraftVersionMetadata metadata = await VersionMetadataParser.ParseAsync(stream, metadataPath, cancellationToken).ConfigureAwait(false);
        return metadata.InheritsFrom;
    }

    private static bool IsFileComplete(InstallFile file)
    {
        if (!File.Exists(file.DestinationPath))
        {
            return false;
        }

        return file.Size is not long expectedSize || new FileInfo(file.DestinationPath).Length == expectedSize;
    }

    private static long ExistingLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static string? NormalizeSha1(string? sha1) => string.IsNullOrWhiteSpace(sha1) ? null : sha1.Trim();

    private static long? NormalizeExpectedSize(long? size) => size is > 0 ? size : null;

    private static bool IsSha1(string value) => value.Length == 40 && value.All(char.IsAsciiHexDigit);

    private static bool IsSafeFileName(string fileName) =>
        !Path.IsPathRooted(fileName)
        && fileName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && !fileName.Contains(Path.DirectorySeparatorChar)
        && !fileName.Contains(Path.AltDirectorySeparatorChar)
        && fileName is not "." and not "..";
}
