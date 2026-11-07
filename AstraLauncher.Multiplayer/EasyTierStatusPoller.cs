using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace AstraLauncher.Multiplayer;

/// <summary>Runs <c>easytier-cli -o json peer</c> against the local RPC portal and parses its machine-readable output.</summary>
public static class EasyTierStatusPoller
{
    public static async Task<IReadOnlyList<PeerInfo>> QueryAsync(
        string cliExecutable,
        int rpcPort,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cliExecutable);

        var startInfo = new ProcessStartInfo(cliExecutable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in new[]
                 {
                     "-p", "127.0.0.1:" + rpcPort.ToString(CultureInfo.InvariantCulture),
                     "-o", "json",
                     "peer",
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            return [];
        }

        Task<string> stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            string output = await stdout.ConfigureAwait(false);
            await stderr.ConfigureAwait(false);
            return ParsePeerJson(output);
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                // Best effort: the CLI is only a short-lived status probe.
            }
        }
    }

    /// <summary>
    /// Tolerates leading log lines (including bracketed ones such as "[INFO]"), trailing whitespace and both
    /// array / {"peers": [...]} shapes returned by different EasyTier builds.
    /// </summary>
    public static IReadOnlyList<PeerInfo> ParsePeerJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        for (int index = 0; index < text.Length; index++)
        {
            if (text[index] is not ('[' or '{'))
            {
                continue;
            }

            IReadOnlyList<PeerInfo>? parsed = TryParseFrom(text, index);
            if (parsed is not null)
            {
                return parsed;
            }
        }

        return [];
    }

    private static IReadOnlyList<PeerInfo>? TryParseFrom(string text, int start)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(text[start..]);
            JsonElement root = document.RootElement;
            JsonElement peers = root.ValueKind == JsonValueKind.Array
                ? root
                : (root.TryGetProperty("peers", out JsonElement nested) && nested.ValueKind == JsonValueKind.Array
                    ? nested
                    : default);
            if (peers.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var result = new List<PeerInfo>();
            foreach (JsonElement item in peers.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                result.Add(new PeerInfo(
                    ReadString(item, "ipv4"),
                    ReadString(item, "hostname"),
                    ReadString(item, "cost"),
                    ReadDouble(item, "lat_ms"),
                    ReadString(item, "tunnel_proto")));
            }

            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ReadString(JsonElement item, string name) =>
        item.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static double? ReadDouble(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double parsed)
                ? parsed
                : null;
    }
}
