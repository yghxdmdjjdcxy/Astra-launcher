using System.IO.Compression;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;

string root = Environment.GetEnvironmentVariable("ASTRA_FEEDBACK_ROOT") ?? @"G:\反馈";
Directory.CreateDirectory(root);
const long maxPackageBytes = 64L * 1024 * 1024;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("ASTRA_FEEDBACK_URL") ?? "http://0.0.0.0:5213");
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = maxPackageBytes;
    options.ValueLengthLimit = 1024 * 1024;
});
var app = builder.Build();

app.MapGet("/api/health", () => Results.Json(new
{
    ok = true,
    service = "AstraLauncher.FeedbackServer",
    time = DateTimeOffset.Now,
}));

app.MapPost("/api/feedback", async (HttpRequest request) =>
{
    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new { ok = false, error = "multipart/form-data required" });
    }

    IFormCollection form;
    try
    {
        form = await request.ReadFormAsync();
    }
    catch (Exception exception) when (exception is InvalidDataException or BadHttpRequestException or IOException)
    {
        return Results.BadRequest(new { ok = false, error = "invalid form: " + exception.Message });
    }

    IFormFile? file = form.Files.GetFile("file");
    if (file is null || file.Length == 0)
    {
        return Results.BadRequest(new { ok = false, error = "missing file" });
    }

    if (file.Length > maxPackageBytes)
    {
        return Results.BadRequest(new { ok = false, error = "package too large" });
    }

    string remoteIp = ResolveIp(request);
    string ipFolder = Sanitize(remoteIp);
    string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
    string destination = Path.Combine(root, ipFolder, timestamp);
    int suffix = 1;
    while (Directory.Exists(destination))
    {
        destination = Path.Combine(root, ipFolder, $"{timestamp}-{suffix++}");
    }

    Directory.CreateDirectory(destination);
    string zipPath = Path.Combine(destination, "feedback.zip");
    await using (FileStream output = File.Create(zipPath))
    {
        await file.CopyToAsync(output);
    }

    try
    {
        ExtractSafely(zipPath, Path.Combine(destination, "content"));
    }
    catch (Exception exception) when (exception is InvalidDataException or IOException)
    {
        // Keep the original zip when it is not a valid archive.
        File.WriteAllText(Path.Combine(destination, "extract-error.txt"), exception.Message);
    }

    var metadata = new
    {
        id = Path.GetFileName(destination),
        receivedAt = DateTimeOffset.Now,
        remoteIp,
        launcherVersion = form["launcherVersion"].ToString(),
        machine = form["machine"].ToString(),
        packageBytes = file.Length,
        originalFileName = file.FileName,
    };
    await File.WriteAllTextAsync(
        Path.Combine(destination, "submission.json"),
        JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));

    app.Logger.LogInformation("Feedback {Id} from {Ip} ({Bytes} bytes) stored at {Path}.", metadata.id, remoteIp, file.Length, destination);
    return Results.Json(new { ok = true, id = metadata.id });
});

app.Run();

static string ResolveIp(HttpRequest request)
{
    string? cloudflare = request.Headers["CF-Connecting-IP"].FirstOrDefault();
    if (!string.IsNullOrWhiteSpace(cloudflare))
    {
        return cloudflare.Trim();
    }

    string? forwarded = request.Headers["X-Forwarded-For"].FirstOrDefault();
    if (!string.IsNullOrWhiteSpace(forwarded))
    {
        return forwarded.Split(',')[0].Trim();
    }

    return request.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}

static string Sanitize(string value)
{
    foreach (char invalid in Path.GetInvalidFileNameChars())
    {
        value = value.Replace(invalid, '_');
    }

    value = value.Replace(':', '_').Replace('/', '_').Replace('\\', '_');
    return string.IsNullOrWhiteSpace(value) ? "unknown" : value;
}

static void ExtractSafely(string zipPath, string destination)
{
    Directory.CreateDirectory(destination);
    string fullDestination = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
    using ZipArchive archive = ZipFile.OpenRead(zipPath);
    foreach (ZipArchiveEntry entry in archive.Entries)
    {
        string target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
        if (!target.StartsWith(fullDestination, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("ZIP entry escapes destination: " + entry.FullName);
        }

        if (string.IsNullOrEmpty(entry.Name))
        {
            Directory.CreateDirectory(target);
            continue;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        entry.ExtractToFile(target, overwrite: true);
    }
}
