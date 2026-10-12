using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using PlayMap.Core.Numerics;
using PlayMap.Core.Vision;
using PlayMap.Vision.Models;

namespace PlayMap.Vision.Tests;

/// <summary>The store installs only what the operator's accepted licence covers, only between shows, only with a
/// verified checksum, resumes an interrupted download with a range request, discards a file whose checksum is
/// wrong, and lists what is on disk — against a real HTTP server on loopback.</summary>
public sealed class ModelStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("playmap-vision-store-").FullName;
    private readonly byte[] _payload = Payload(300_000);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static byte[] Payload(int n)
    {
        var rng = new Pcg32(5);
        var b = new byte[n];
        for (var i = 0; i < n; i++)
            b[i] = (byte)rng.NextUInt();
        return b;
    }

    private static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    private static VisionModelInfo Entry(string url, string? sha, VisionLicence? licence = null, bool gated = false, long size = 0) => new(
        "test-model", "Test model", VisionTask.Depth, licence ?? ModelCatalog.Apache2,
        [new VisionModelFile("model", "model.onnx", url, sha, size)], 64, 64, gated, "");

    private ModelStore Store(VisionModelInfo entry, Func<bool>? allowed = null) =>
        new(_dir, new ModelCatalog([entry]), new ModelStoreOptions(DownloadsAllowed: allowed, StallTimeout: TimeSpan.FromSeconds(5)));

    [Fact]
    public async Task AModelWhoseLicenceIsNotAcceptedIsRefusedWithoutTouchingTheNetwork()
    {
        using var server = new LoopbackServer(_payload);
        using var store = Store(Entry(server.Url, Sha(_payload)));
        var r = await store.InstallAsync("test-model", null, null, CancellationToken.None);
        Assert.Equal(ModelInstallStatus.Refused, r.Status);
        Assert.Contains("has not been accepted", r.Words, StringComparison.Ordinal);
        Assert.Equal(0, server.Requests);
        Assert.Empty(store.ListInstalled());

        Assert.True(store.AcceptLicence("test-model", "operator").Accepted);
        Assert.Equal("Apache-2.0", store.Acceptance("test-model")!.LicenceId);
        var ok = await store.InstallAsync("test-model", null, null, CancellationToken.None);
        Assert.Equal(ModelInstallStatus.Installed, ok.Status);
        var installed = Assert.Single(store.ListInstalled());
        Assert.Equal(Sha(_payload), installed.Files[0].Sha256);
        Assert.Equal(_payload, await File.ReadAllBytesAsync(installed.PathOf("model")!));

        Assert.True(store.RevokeLicence("test-model"));
        Assert.Null(store.Acceptance("test-model"));
    }

    [Fact]
    public async Task AWrongChecksumDiscardsTheDownloadAndInstallsNothing()
    {
        using var server = new LoopbackServer(_payload);
        using var store = Store(Entry(server.Url, new string('0', 64)));
        store.AcceptLicence("test-model", "operator");
        var r = await store.InstallAsync("test-model", null, null, CancellationToken.None);
        Assert.Equal(ModelInstallStatus.Failed, r.Status);
        Assert.Contains("checksum mismatch", r.Words, StringComparison.Ordinal);
        Assert.Contains(Sha(_payload), r.Words, StringComparison.Ordinal);
        Assert.Empty(store.ListInstalled());
        Assert.Empty(Directory.GetFiles(Path.Combine(store.Root, "test-model")));
    }

    [Fact]
    public async Task AnInterruptedDownloadResumesWithARangeRequestAndThenStaysInstalled()
    {
        using var server = new LoopbackServer(_payload) { CutFirstResponseAfter = 120_000 };
        using var store = Store(Entry(server.Url, Sha(_payload)));
        store.AcceptLicence("test-model", "operator");
        var progress = new ConcurrentQueue<ModelDownloadProgress>();

        var first = await store.InstallAsync("test-model", new SyncProgress(progress.Enqueue), null, CancellationToken.None);
        Assert.Equal(ModelInstallStatus.Failed, first.Status);
        Assert.Contains("install again to resume", first.Words, StringComparison.Ordinal);
        var partial = Path.Combine(store.Root, "test-model", "model.onnx.partial");
        var kept = new FileInfo(partial).Length;
        Assert.InRange(kept, 1, 120_000);

        var second = await store.InstallAsync("test-model", new SyncProgress(progress.Enqueue), null, CancellationToken.None);
        Assert.Equal(ModelInstallStatus.Installed, second.Status);
        Assert.Equal(_payload.Length - kept, second.BytesDownloaded);
        Assert.Contains("bytes=" + kept + "-", server.RangeHeaders);
        Assert.False(File.Exists(partial));
        Assert.True(await store.VerifyAsync("test-model", CancellationToken.None));
        Assert.Equal(_payload.Length, progress.Last().Received);

        var requestsBefore = server.Requests;
        var third = await store.InstallAsync("test-model", null, null, CancellationToken.None);
        Assert.Equal(ModelInstallStatus.AlreadyInstalled, third.Status);
        Assert.Equal(requestsBefore, server.Requests);
    }

    [Fact]
    public async Task AServerThatIgnoresTheRangeStartsTheFileAgain()
    {
        using var server = new LoopbackServer(_payload) { CutFirstResponseAfter = 50_000, IgnoreRange = true };
        using var store = Store(Entry(server.Url, Sha(_payload)));
        store.AcceptLicence("test-model", "operator");
        Assert.Equal(ModelInstallStatus.Failed, (await store.InstallAsync("test-model", null, null, CancellationToken.None)).Status);
        var r = await store.InstallAsync("test-model", null, null, CancellationToken.None);
        Assert.Equal(ModelInstallStatus.Installed, r.Status);
        Assert.Equal(_payload.Length, r.BytesDownloaded);
        Assert.True(await store.VerifyAsync("test-model", CancellationToken.None));
    }

    [Fact]
    public async Task NoDownloadRunsWhileTheShowGateIsShut()
    {
        using var server = new LoopbackServer(_payload);
        var showRunning = true;
        using var store = Store(Entry(server.Url, Sha(_payload)), () => !showRunning);
        store.AcceptLicence("test-model", "operator");
        var r = await store.InstallAsync("test-model", null, null, CancellationToken.None);
        Assert.Equal(ModelInstallStatus.Refused, r.Status);
        Assert.Contains("show", r.Words, StringComparison.Ordinal);
        Assert.Equal(0, server.Requests);
        showRunning = false;
        Assert.True((await store.InstallAsync("test-model", null, null, CancellationToken.None)).IsInstalled);
    }

    [Fact]
    public async Task ANonCommercialLicenceNeedsItsAcknowledgementAndAGatedModelItsToken()
    {
        using var server = new LoopbackServer(_payload);
        using (var nc = Store(Entry(server.Url, Sha(_payload), ModelCatalog.CcByNc4)))
        {
            var refused = nc.AcceptLicence("test-model", "operator");
            Assert.False(refused.Accepted);
            Assert.Contains("commercial", refused.Words, StringComparison.Ordinal);
            Assert.Equal(ModelInstallStatus.Refused, (await nc.InstallAsync("test-model", null, null, CancellationToken.None)).Status);
            Assert.True(nc.AcceptLicence("test-model", "operator", acknowledgeNonCommercial: true).Accepted);
            Assert.True((await nc.InstallAsync("test-model", null, null, CancellationToken.None)).IsInstalled);
            nc.Remove("test-model");
        }
        using var gated = Store(Entry(server.Url, Sha(_payload), ModelCatalog.SamLicence, gated: true));
        gated.AcceptLicence("test-model", "operator");
        var noToken = await gated.InstallAsync("test-model", null, null, CancellationToken.None);
        Assert.Equal(ModelInstallStatus.Refused, noToken.Status);
        Assert.Contains("access token", noToken.Words, StringComparison.Ordinal);
        var withToken = await gated.InstallAsync("test-model", null, "secret-token", CancellationToken.None);
        Assert.True(withToken.IsInstalled);
        Assert.Equal("Bearer secret-token", server.LastAuthorization);
        Assert.DoesNotContain("secret-token", withToken.Words, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFileUrlInstallsFromAnOfflineMirror()
    {
        var mirror = Path.Combine(_dir, "mirror.onnx");
        await File.WriteAllBytesAsync(mirror, _payload);
        using var store = Store(Entry(new Uri(mirror).AbsoluteUri, Sha(_payload)));
        store.AcceptLicence("test-model", "operator");
        Assert.Equal(ModelInstallStatus.Installed, (await store.InstallAsync("test-model", null, null, CancellationToken.None)).Status);
        Assert.NotNull(store.Installed("test-model"));
    }

    [Fact]
    public async Task TheBuiltInCatalogueIsUnverifiedAndNeverDefaultsToANonCommercialModel()
    {
        using var store = new ModelStore(_dir, ModelCatalog.Default);
        foreach (var m in ModelCatalog.Default.Models)
        {
            store.AcceptLicence(m.Id, "operator", acknowledgeNonCommercial: true);
            var r = await store.InstallAsync(m.Id, null, "token", CancellationToken.None);
            Assert.Equal(ModelInstallStatus.Refused, r.Status);
            Assert.Contains("no verified checksum", r.Words, StringComparison.Ordinal);
        }
        Assert.Equal("depth-anything-3-small", ModelCatalog.Default.DefaultFor(VisionTask.Depth)!.Id);
        Assert.Equal("sam2.1-tiny", ModelCatalog.Default.DefaultFor(VisionTask.PromptSegmentation)!.Id);
        Assert.Null(ModelCatalog.Default.DefaultFor(VisionTask.TextSegmentation));
        Assert.Contains("non-commercial", ModelStore.Describe(ModelCatalog.Default.Find("depth-anything-3-large")!), StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => ModelCatalog.Default.With(Entry("https://x", null) with { Id = "../evil" }));
    }

    private sealed class SyncProgress(Action<ModelDownloadProgress> report) : IProgress<ModelDownloadProgress>
    {
        public void Report(ModelDownloadProgress value) => report(value);
    }

    /// <summary>A file server on 127.0.0.1 that honours byte ranges, can cut its first response short, and
    /// records what it was asked.</summary>
    private sealed class LoopbackServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly byte[] _content;
        private readonly Task _loop;
        private int _requests;

        public LoopbackServer(byte[] content)
        {
            _content = content;
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Url = $"http://127.0.0.1:{port}/model.onnx";
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _loop = Task.Run(Loop);
        }

        public string Url { get; }
        public int CutFirstResponseAfter { get; init; } = -1;
        public bool IgnoreRange { get; init; }
        public int Requests => Volatile.Read(ref _requests);
        public ConcurrentBag<string> RangeHeaders { get; } = [];
        public string? LastAuthorization { get; private set; }

        private async Task Loop()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync();
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    // The listener stopped: the test is over.
                    return;
                }
                var n = Interlocked.Increment(ref _requests);
                var range = ctx.Request.Headers["Range"];
                LastAuthorization = ctx.Request.Headers["Authorization"];
                if (range is not null)
                    RangeHeaders.Add(range);
                long from = 0;
                if (range is not null && !IgnoreRange)
                {
                    from = long.Parse(range["bytes=".Length..].TrimEnd('-'));
                    ctx.Response.StatusCode = 206;
                    ctx.Response.AddHeader("Content-Range", $"bytes {from}-{_content.Length - 1}/{_content.Length}");
                }
                var body = _content.AsMemory((int)from);
                ctx.Response.ContentLength64 = body.Length;
                try
                {
                    if (n == 1 && CutFirstResponseAfter >= 0)
                    {
                        await ctx.Response.OutputStream.WriteAsync(body[..CutFirstResponseAfter]);
                        await ctx.Response.OutputStream.FlushAsync();
                        ctx.Response.Abort();
                        continue;
                    }
                    await ctx.Response.OutputStream.WriteAsync(body);
                    ctx.Response.Close();
                }
                catch (Exception ex) when (ex is HttpListenerException or IOException)
                {
                    // The client went away mid-body; the next request is served afresh.
                }
            }
        }

        public void Dispose()
        {
            _listener.Stop();
            _listener.Close();
            _loop.Wait(TimeSpan.FromSeconds(5));
        }
    }
}
