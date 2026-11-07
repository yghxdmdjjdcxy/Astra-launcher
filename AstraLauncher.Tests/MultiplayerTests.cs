using System.IO;
using System.IO.Compression;
using AstraLauncher.Core.Models;
using AstraLauncher.Downloader;
using AstraLauncher.Multiplayer;
using AstraLauncher.Plugin.Abstractions;

namespace AstraLauncher.Tests;

public sealed class MultiplayerTests
{
    private static EasyTierOptions Options(RoomRole role, EasyTierMode mode = EasyTierMode.NoTun) => new(
        "test-room", "s3cr3t", role, mode, "tcp://public.easytier.cn:11010",
        "10.77.0.1", 25565, 25566, 15890, "test-pc");

    private static PluginLaunchRequest LaunchRequest() => new(
        @"C:\minecraft", @"C:\instances\demo", "1.21.1", "demo", "Steve",
        @"C:\java\bin\java.exe", 4096, [], []);

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "astra-mp-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void InviteCode_RoundTripsWithoutLosingTheSecret()
    {
        var invite = new RoomInvite("my-room", "a-very-long-secret", "10.77.0.1", 25565,
            "tcp://public.easytier.cn:11010", "friends");

        string code = RoomInviteCodec.Encode(invite);

        Assert.StartsWith("ASTRA1.", code);
        Assert.True(RoomInviteCodec.TryDecode(code, out RoomInvite? decoded));
        Assert.NotNull(decoded);
        Assert.Equal(invite.NetworkName, decoded.NetworkName);
        Assert.Equal(invite.NetworkSecret, decoded.NetworkSecret);
        Assert.Equal(invite.HostVirtualIp, decoded.HostVirtualIp);
        Assert.Equal(invite.GamePort, decoded.GamePort);
        Assert.Equal(invite.SharedNode, decoded.SharedNode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("ASTRA1.!!!not-base64!!!")]
    public void InviteCode_RejectsGarbage(string code)
    {
        Assert.False(RoomInviteCodec.TryDecode(code, out RoomInvite? decoded));
        Assert.Null(decoded);
    }

    [Fact]
    public void InviteCode_RejectsInvalidHostOrNode()
    {
        Assert.Throws<EasyTierException>(() => RoomInviteCodec.Encode(
            new RoomInvite("room", "secret", "not-an-ip", 25565, "tcp://node:11010")));
        Assert.Throws<EasyTierException>(() => RoomInviteCodec.Encode(
            new RoomInvite("room", "secret", "10.77.0.1", 25565, "ftp://node")));
    }

    [Fact]
    public void HostConfig_UsesFixedIpAndWhitelist_WithoutPortForward()
    {
        string toml = EasyTierConfigWriter.Build(Options(RoomRole.Host));

        Assert.Contains("ipv4 = \"10.77.0.1/24\"", toml);
        Assert.Contains("dhcp = false", toml);
        Assert.Contains("tcp_whitelist = [ \"25565\" ]", toml);
        Assert.Contains("network_secret = \"s3cr3t\"", toml);
        Assert.Contains("no_tun = true", toml);
        Assert.DoesNotContain("[[port_forward]]", toml);
    }

    [Fact]
    public void GuestConfig_UsesStableCliFlagForPortForward()
    {
        EasyTierOptions options = Options(RoomRole.Guest);
        string toml = EasyTierConfigWriter.Build(options);

        Assert.Contains("dhcp = true", toml);
        Assert.Contains("no_tun = true", toml);
        Assert.DoesNotContain("port_forward", toml);
        Assert.DoesNotContain("tcp_whitelist", toml);

        IReadOnlyList<string> arguments = EasyTierService.BuildExtraArguments(options);
        Assert.Equal(2, arguments.Count);
        Assert.Equal("--port-forward", arguments[0]);
        Assert.Equal("tcp://127.0.0.1:25566/10.77.0.1:25565", arguments[1]);
    }

    [Fact]
    public void ExtraArguments_AreEmptyForHostAndFullTunGuest()
    {
        Assert.Empty(EasyTierService.BuildExtraArguments(Options(RoomRole.Host)));
        Assert.Empty(EasyTierService.BuildExtraArguments(Options(RoomRole.Guest, EasyTierMode.FullTun)));
    }

    [Fact]
    public void FullTunGuest_HasNoPortForwardAndCreatesTun()
    {
        string toml = EasyTierConfigWriter.Build(Options(RoomRole.Guest, EasyTierMode.FullTun));

        Assert.Contains("no_tun = false", toml);
        Assert.DoesNotContain("[[port_forward]]", toml);
    }

    [Fact]
    public void ConfigWriter_EscapesSecrets()
    {
        string toml = EasyTierConfigWriter.Build(Options(RoomRole.Guest) with { NetworkSecret = "a\"b\\c" });
        Assert.Contains("network_secret = \"a\\\"b\\\\c\"", toml);
    }

    [Fact]
    public void ConfigWriter_RejectsInvalidPorts()
    {
        Assert.Throws<EasyTierException>(() => EasyTierConfigWriter.Build(Options(RoomRole.Guest) with { LocalPort = 70000 }));
        Assert.Throws<EasyTierException>(() => EasyTierConfigWriter.Build(Options(RoomRole.Guest) with { RpcPort = 25566 }));
        Assert.Throws<EasyTierException>(() => EasyTierConfigWriter.Build(Options(RoomRole.Host) with { SharedNode = "ftp://node" }));
    }

    [Fact]
    public async Task ConfigWriter_WritesAFile()
    {
        string directory = CreateTempDirectory();
        try
        {
            string path = await EasyTierConfigWriter.WriteAsync(Options(RoomRole.Guest), directory);
            Assert.True(File.Exists(path));
            Assert.Contains("network_secret", await File.ReadAllTextAsync(path));
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public void PeerJson_ParsesArrayWithLeadingLogLine()
    {
        const string json = "[{\"ipv4\":\"10.77.0.1\",\"hostname\":\"host\",\"cost\":\"p2p\",\"lat_ms\":3.45,\"tunnel_proto\":\"udp\"},"
            + "{\"ipv4\":\"10.77.0.2\",\"hostname\":\"guest\",\"cost\":\"relay\",\"lat_ms\":\"27.8\",\"tunnel_proto\":\"tcp\"}]";
        IReadOnlyList<PeerInfo> peers = EasyTierStatusPoller.ParsePeerJson("[INFO] starting\n" + json);

        Assert.Equal(2, peers.Count);
        Assert.True(peers[0].IsDirect);
        Assert.Equal(3.45, peers[0].LatencyMs!.Value);
        Assert.False(peers[1].IsDirect);
        Assert.Equal(27.8, peers[1].LatencyMs!.Value);
    }

    [Fact]
    public void PeerJson_AcceptsObjectWrapperAndToleratesGarbage()
    {
        const string json = "{\"peers\":[{\"ipv4\":\"10.77.0.1\",\"cost\":\"p2p\"}]}";
        Assert.Single(EasyTierStatusPoller.ParsePeerJson(json));
        Assert.Empty(EasyTierStatusPoller.ParsePeerJson(""));
        Assert.Empty(EasyTierStatusPoller.ParsePeerJson("no json here"));
    }

    [Fact]
    public async Task LaunchHook_OnlyRewritesWhenConnectedAndNoExplicitServer()
    {
        var service = new FakeEasyTierService
        {
            Status = new EasyTierStatus(true, 42, "ok", "127.0.0.1:25566", []),
        };
        var hook = new EasyTierLaunchHook(service);

        PluginLaunchRequest joined = await hook.BeforeBuildAsync(LaunchRequest(), CancellationToken.None);
        Assert.Equal("127.0.0.1", joined.ServerAddress);
        Assert.Equal(25566, joined.ServerPort);

        PluginLaunchRequest explicitServer = LaunchRequest() with { ServerAddress = "mc.example.com", ServerPort = 25565 };
        Assert.Equal("mc.example.com",
            (await hook.BeforeBuildAsync(explicitServer, CancellationToken.None)).ServerAddress);

        service.AutoJoinEnabled = false;
        Assert.Null((await hook.BeforeBuildAsync(LaunchRequest(), CancellationToken.None)).ServerAddress);

        service.AutoJoinEnabled = true;
        service.Status = EasyTierStatus.Idle();
        Assert.Null((await hook.BeforeBuildAsync(LaunchRequest(), CancellationToken.None)).ServerAddress);
    }

    [Fact]
    public void EntryAddress_DependsOnRoleAndMode()
    {
        Assert.Equal("127.0.0.1:25565", EasyTierService.ResolveEntryAddress(Options(RoomRole.Host)));
        Assert.Equal("127.0.0.1:25566", EasyTierService.ResolveEntryAddress(Options(RoomRole.Guest)));
        Assert.Equal("10.77.0.1:25565",
            EasyTierService.ResolveEntryAddress(Options(RoomRole.Guest, EasyTierMode.FullTun)));
    }

    [Fact]
    public void TrySplitAddress_HandlesGoodAndBadValues()
    {
        Assert.True(EasyTierLaunchHook.TrySplitAddress("127.0.0.1:25566", out string host, out int port));
        Assert.Equal("127.0.0.1", host);
        Assert.Equal(25566, port);
        Assert.False(EasyTierLaunchHook.TrySplitAddress("25566", out _, out _));
        Assert.False(EasyTierLaunchHook.TrySplitAddress("host:notaport", out _, out _));
        Assert.False(EasyTierLaunchHook.TrySplitAddress("host:70000", out _, out _));
    }

    [Fact]
    public void ExtractSafely_RejectsZipSlip()
    {
        string root = CreateTempDirectory();
        try
        {
            string zip = Path.Combine(root, "bad.zip");
            using (ZipArchive archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                using StreamWriter writer = new(archive.CreateEntry("../escaped.txt").Open());
                writer.Write("bad");
            }

            Assert.Throws<EasyTierException>(() => EasyTierBinaryResolver.ExtractSafely(zip, Path.Combine(root, "out")));
            Assert.False(File.Exists(Path.Combine(root, "escaped.txt")));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void ExtractSafely_ExtractsOrdinaryEntries()
    {
        string root = CreateTempDirectory();
        try
        {
            string zip = Path.Combine(root, "good.zip");
            using (ZipArchive archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                using StreamWriter writer = new(archive.CreateEntry("bin/easytier-core.exe").Open());
                writer.Write("exe");
            }

            string destination = Path.Combine(root, "out");
            EasyTierBinaryResolver.ExtractSafely(zip, destination);
            Assert.True(File.Exists(Path.Combine(destination, "bin", "easytier-core.exe")));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void DownloadOptions_UseThirtyTwoParallelSegments()
    {
        DownloadQueueOptions options = EasyTierBinaryResolver.CreateDownloadOptions();

        Assert.Equal(32, EasyTierBinaryResolver.DownloadThreads);
        Assert.Equal(32, options.SegmentsPerFile);
        Assert.Equal(32, options.MaximumConcurrentSegments);
        Assert.Equal(32, options.MaximumConcurrentDownloadsPerHost);
        Assert.True(options.EnableSegmentedDownloads);
    }

    [Fact]
    public void Settings_NormalizesMultiplayerValues()
    {
        var settings = new LauncherSettings
        {
            EasyTierGamePort = 99999,
            EasyTierLocalPort = 0,
            EasyTierSharedNode = "ftp://node",
            EasyTierHostVirtualIp = "not-an-ip",
            EasyTierLastRoomName = new string('x', 200),
        };

        settings.Normalize();

        Assert.Equal(LauncherSettings.DefaultEasyTierGamePort, settings.EasyTierGamePort);
        Assert.Equal(LauncherSettings.DefaultEasyTierLocalPort, settings.EasyTierLocalPort);
        Assert.Equal(LauncherSettings.DefaultEasyTierSharedNode, settings.EasyTierSharedNode);
        Assert.Equal(LauncherSettings.DefaultEasyTierHostVirtualIp, settings.EasyTierHostVirtualIp);
        Assert.Equal(LauncherSettings.DefaultEasyTierRoomName, settings.EasyTierLastRoomName);
    }

    private static void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class FakeEasyTierService : IEasyTierService
    {
        public bool IsRunning => Status.IsRunning;
        public bool AutoJoinEnabled { get; set; } = true;
        public EasyTierStatus Status { get; set; } = EasyTierStatus.Idle();

        public event Action<EasyTierStatus>? StatusChanged
        {
            add { }
            remove { }
        }

        public EasyTierBinaries? TryResolveBinaries(string? configuredDirectory) => null;

        public Task<EasyTierBinaries> EnsureInstalledAsync(
            string? configuredDirectory,
            IProgress<string>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new EasyTierBinaries("core.exe", "cli.exe", "."));

        public Task StartAsync(
            EasyTierOptions options,
            string? configuredDirectory,
            IProgress<string>? progress = null,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync() => Task.CompletedTask;
        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Dispose() { }
    }
}
