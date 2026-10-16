using PlayMap.Net.Fleet;
using PlayMap.Net.Tls;

namespace PlayMap.Net.Tests.Fleet;

/// <summary>Where a backup refuses to pull before anything is sent: another machine in clear (its hello offers no certificate), and a
/// hello that drops the certificate a pin already holds; a pull from this machine may go plain; the source never prints its token.</summary>
public sealed class MirrorPullerTests
{
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
