using System.Diagnostics;
using System.Security.Cryptography;
using PlayMap.Core.Time;
using PlayMap.Net.Fleet;
using PlayMap.Net.Media;
using PlayMap.Net.Tls;
using PlayMap.Tests;

namespace PlayMap.Net.Tests.Fleet;

/// <summary>Where a backup refuses to pull before anything is sent: another machine in clear (its hello offers no certificate), and a
/// hello that drops the certificate a pin already holds; a pull from this machine may go plain; the source never prints its token; and a
/// file's body that falls silent is counted on the node's clock.</summary>
public sealed class MirrorPullerTests
{
    [Fact]
    public async Task AFilesSilenceIsCountedOnTheNodesClock()
    {
        // A primary on this machine (plain HTTP may go to loopback) whose file falls silent after 64 KiB, and a quiet of ten minutes on
        // the node's clock, which the test moves by hand: the try ends at the clock's first step past the quiet, in moments of real time.
        var bytes = RandomNumberGenerator.GetBytes(300_000);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        await using var peer = new FakeMediaPeer(hash, bytes, stallAfter: 64 * 1024, mediaPath: MirrorWebModule.MediaPath);
        var clock = new ManualClock(1_000_000_000_000);
        var quiet = TimeSpan.FromMinutes(10);
        using var puller = new MirrorPuller(new MirrorSource("0badc0de", "FOH-A", "127.0.0.1", peer.Port, "", ""), new PinStore(), () => 1_760_000_000_000, clock,
            pulls: new MediaTransferOptions { Quiet = quiet, ChunkAttempts = 1 });
        var dir = Directory.CreateTempSubdirectory("playmap-mirror-");
        try
        {
            var pull = puller.FileAsync(hash, bytes.Length, Path.Combine(dir.FullName, "clip.bin"), CancellationToken.None);
            var sw = Stopwatch.StartNew();
            while (!pull.IsCompleted && sw.Elapsed < TimeSpan.FromSeconds(30))
            {
                clock.Advance(quiet.Ticks * TimeSpan.NanosecondsPerTick + 1_000_000);
                await Task.WhenAny(pull, Task.Delay(100));
            }

            Assert.True(pull.IsCompleted, "the pull still waits on the silent primary after 30 s");
            var result = await pull;
            Assert.Equal(MediaPullOutcome.Failed, result.Outcome);
            Assert.Contains($"chunk 1 of 1 from 127.0.0.1:{peer.Port} could not be had: sent nothing for 600 s, ", result.Words, StringComparison.Ordinal);
            Assert.Equal(1, peer.FileAsks);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private const string Fingerprint = "sha256:" + "ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34";

    private static MirrorPuller Puller(string address, string fingerprint, PinStore pins) =>
        new(new MirrorSource("0badc0de", "FOH-A", address, 9941, fingerprint, "K7QM-3XWD-P9RA"), pins, () => 1_760_000_000_000);

    [Fact]
    public async Task APullInClearToAnotherMachineIsRefusedBeforeAnythingIsSent()
    {
        using var puller = Puller("10.0.0.21", "", new PinStore());
        Assert.False(puller.Tls);
        Assert.Contains("goes over HTTPS only", puller.Refusal, StringComparison.Ordinal);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => puller.ProjectAsync(CancellationToken.None));
        Assert.Equal(puller.Refusal, ex.Message);
        var file = await puller.FileAsync(new string('1', 64), 10, Path.Combine(Path.GetTempPath(), "never-written.bin"), CancellationToken.None);
        Assert.Equal(PlayMap.Net.Media.MediaPullOutcome.Failed, file.Outcome);
    }

    [Fact]
    public void AHelloThatDropsAPinnedCertificateIsRefusedAndThisMachineMayPullPlain()
    {
        var pins = new PinStore();
        using (var plain = Puller("127.0.0.1", "", pins))
            Assert.Equal("", plain.Refusal);
        pins.Pin(PinStore.NodeKey("0badc0de"), Fingerprint);
        using (var downgraded = Puller("127.0.0.1", "", pins))
            Assert.Contains("one is pinned for it", downgraded.Refusal, StringComparison.Ordinal);
        using var pinned = Puller("10.0.0.21", Fingerprint, pins);
        Assert.Equal(("", true), (pinned.Refusal, pinned.Tls));
        Assert.StartsWith("https://10.0.0.21:9941/", pinned.Base.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ASourceNeverPrintsItsToken()
    {
        var source = new MirrorSource("0badc0de", "FOH-A", "10.0.0.21", 9941, Fingerprint, "K7QM-3XWD-P9RA");
        Assert.DoesNotContain("K7QM", source.ToString(), StringComparison.Ordinal);
        Assert.Contains("FOH-A", source.ToString(), StringComparison.Ordinal);
    }
}
